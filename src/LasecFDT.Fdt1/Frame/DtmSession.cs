using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Jigfdt.Fdt100;
using LasecFDT.Core.Project;
using LasecFDT.Fdt1.Com;

namespace LasecFDT.Fdt1.Frame
{
    /// <summary>Estados da máquina de estados do DTM (FDT 1.2.1 §4.2.2, Figura 4).</summary>
    public enum DtmState
    {
        Up, NewCreated, ExistingCreated, NewEnabled, Running, Configured,
        CommunicationSet, GoingOnline, Online, GoingOffline, Destroyed, Zombie, Released, Failed
    }

    /// <summary>Registro de uma chamada feita ao DTM (para relatório e testes).</summary>
    public sealed class StepRecord
    {
        public string Step { get; set; }
        public bool Ok { get; set; }
        public string Result { get; set; }
        public string Error { get; set; }
        public string HResult { get; set; }
        public long DurationMs { get; set; }
        public string StateAfter { get; set; }
        public override string ToString() => $"{(Ok ? "OK  " : "FAIL")} {Step,-48} {DurationMs,5} ms  {StateAfter,-16} {(Error ?? Result)}";
    }

    /// <summary>
    /// Uma instância de DTM hospedada pelo Frame. Encapsula a sequência de chamadas documentada:
    ///   §6.13.1 nova instância:  CoCreateInstance → QI(IPersistXXX) → InitNew → IDtm(2)::Environment(2)
    ///                            → IDtmInformation::GetInformation → IDtm::InitNew(deviceType)
    ///   §4.3.1.1               → IDtm::Config(userInfo)                          ⇒ estado "configured"
    ///   §6.13.2 instância existente: CoCreateInstance → QI(IPersistXXX) → Load → Environment(2) → Config
    ///   §6.14.2 salvar: IPersistXXX::Save
    ///   §4.3.1.7 liberar: IDtm::PrepareToRelease → (IDtmEvents::OnPreparedToRelease) → Release
    /// Deve ser usada sempre na mesma thread STA (os DTMs registram ThreadingModel=Apartment).
    /// </summary>
    public sealed class DtmSession : IDisposable
    {
        private readonly FrameContainer _frame;
        private object _instance;
        private readonly HashSet<string> _invokeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly int _threadId;

        public Guid Clsid { get; }
        public string ClsidString => Core.FdtIds.ToFdtUuid(Clsid);
        public string SystemTag { get; }
        public DtmState State { get; private set; } = DtmState.Up;
        public List<StepRecord> Steps { get; } = new List<StepRecord>();

        /// <summary>
        /// Rastreamento imediato de cada passo (antes/depois da chamada). Essencial porque um DTM pode
        /// derrubar o processo (ex.: violação de acesso) antes que qualquer relatório seja gravado.
        /// </summary>
        public static Action<string> Trace { get; set; }
        public List<FrameEvent> Events { get; } = new List<FrameEvent>();
        public Dictionary<string, bool> Interfaces { get; } = new Dictionary<string, bool>();
        public string PersistenceKind { get; private set; }
        public string InformationXml { get; private set; }
        public FdtXml.DtmInfo Information { get; private set; }
        public string FunctionsXml { get; private set; }
        public string DeviceTypeXml { get; private set; }
        public bool SaveRequested { get; internal set; }
        public bool DataSetLocked { get; internal set; }
        public bool PreparedToRelease { get; private set; }

        public IDtm Dtm => _instance as IDtm;
        public IDtm2 Dtm2 => _instance as IDtm2;
        public object ComObject => _instance;

        private DtmSession(FrameContainer frame, Guid clsid, string systemTag)
        {
            _frame = frame;
            Clsid = clsid;
            SystemTag = systemTag;
            _threadId = Thread.CurrentThread.ManagedThreadId;
        }

        // ================================================================ criação

        /// <summary>CoCreateInstance do business object do DTM + inventário de interfaces por QueryInterface.</summary>
        public static DtmSession Create(FrameContainer frame, Guid clsid, string systemTag = null)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("DTMs FDT 1.x devem ser criados numa thread STA.");
            var s = new DtmSession(frame, clsid, systemTag ?? "LASEC-" + Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant());
            s.Run("CoCreateInstance", () =>
            {
                var t = Type.GetTypeFromCLSID(clsid, true);
                s._instance = Activator.CreateInstance(t);
                return s._instance.GetType().FullName;
            }, DtmState.Up);
            if (s._instance == null) { s.State = DtmState.Failed; return s; }
            frame.Register(s);
            s.ProbeInterfaces();
            return s;
        }

        /// <summary>QueryInterface para cada interface FDT conhecida (lidas da FDT100.dll) e as de persistência.</summary>
        public void ProbeInterfaces()
        {
            IntPtr unk = Marshal.GetIUnknownForObject(_instance);
            try
            {
                foreach (var t in FdtInterfaceTable.Default.Interfaces)
                    Interfaces[t.Name] = Qi(unk, new Guid(t.Guid));
                Interfaces["IPersistStreamInit"] = Qi(unk, typeof(IPersistStreamInit).GUID);
                Interfaces["IPersistPropertyBag"] = Qi(unk, typeof(IPersistPropertyBag).GUID);
                Interfaces["IOleObject"] = Qi(unk, new Guid("00000112-0000-0000-C000-000000000046"));
            }
            finally { Marshal.Release(unk); }
            Record("QueryInterface (inventário)", true, string.Join(", ", Interfaces.Where(k => k.Value).Select(k => k.Key)), null);
        }

        private static bool Qi(IntPtr unk, Guid iid)
        {
            int hr = Marshal.QueryInterface(unk, ref iid, out IntPtr p);
            if (hr == 0 && p != IntPtr.Zero) { Marshal.Release(p); return true; }
            return false;
        }

        public bool Supports(string name) => Interfaces.TryGetValue(name, out var v) && v;

        // ================================================================ persistência

        /// <summary>IPersistXXX::InitNew (§6.13.1). Prefere IPersistPropertyBag, como no diagrama da spec; o Frame aceita ambos (§2.7.1).</summary>
        public bool PersistInitNew()
        {
            if (Supports("IPersistPropertyBag"))
            {
                PersistenceKind = "PropertyBag";
                return Run("IPersistPropertyBag::InitNew", () => { PersistCalls.PropertyBagInitNew(_instance); return null; }, DtmState.NewCreated);
            }
            if (Supports("IPersistStreamInit"))
            {
                PersistenceKind = "StreamInit";
                return Run("IPersistStreamInit::InitNew", () => { PersistCalls.StreamInitNew(_instance); return null; }, DtmState.NewCreated);
            }
            Record("IPersistXXX::InitNew", false, null, "DTM não implementa IPersistPropertyBag nem IPersistStreamInit (obrigatório, §4.10.1).");
            return false;
        }

        /// <summary>IPersistXXX::Load (§6.13.2) a partir de um data set salvo.</summary>
        public bool PersistLoad(PersistedDataSet data)
        {
            PersistenceKind = data.Kind;
            if (data.Kind == "PropertyBag" && Supports("IPersistPropertyBag"))
                return Run("IPersistPropertyBag::Load", () =>
                {
                    var bag = PropertyBagStore.Import(data.Properties);
                    PersistCalls.PropertyBagLoad(_instance, bag);
                    return bag.Count + " propriedades";
                }, DtmState.ExistingCreated);
            if (data.Kind == "StreamInit" && Supports("IPersistStreamInit"))
                return Run("IPersistStreamInit::Load", () =>
                {
                    var bytes = Convert.FromBase64String(data.StreamBase64 ?? "");
                    PersistCalls.StreamLoad(_instance, new ManagedIStream(bytes));
                    return bytes.Length + " bytes";
                }, DtmState.ExistingCreated);
            Record("IPersistXXX::Load", false, null, "Tipo de persistência incompatível: " + data.Kind);
            return false;
        }

        /// <summary>IPersistXXX::Save (§6.14.2). Retorna o data set opaco do DTM.</summary>
        public PersistedDataSet PersistSave()
        {
            PersistedDataSet result = null;
            if (PersistenceKind == "PropertyBag")
                Run("IPersistPropertyBag::Save", () =>
                {
                    var bag = new PropertyBagStore();
                    PersistCalls.PropertyBagSave(_instance, bag, true, true);
                    result = new PersistedDataSet { Kind = "PropertyBag", Properties = bag.Export() };
                    return bag.Count + " propriedades" + (bag.Warnings.Count > 0 ? "; avisos: " + string.Join(" | ", bag.Warnings) : "");
                }, null);
            else if (PersistenceKind == "StreamInit")
                Run("IPersistStreamInit::Save", () =>
                {
                    var st = new ManagedIStream();
                    PersistCalls.StreamSave(_instance, st, true);
                    var bytes = st.ToArray();
                    result = new PersistedDataSet { Kind = "StreamInit", StreamBase64 = Convert.ToBase64String(bytes) };
                    return bytes.Length + " bytes";
                }, null);
            if (result != null) SaveRequested = false;
            return result;
        }

        // ================================================================ IDtm / IDtm2

        /// <summary>IDtm2::Environment2 se suportado (obrigatório para Frames 1.2.1, §3.3/§4.3.2); senão IDtm::Environment.</summary>
        public bool SetEnvironment()
        {
            var target = State == DtmState.ExistingCreated ? DtmState.Running : DtmState.NewEnabled;
            if (Supports("IDtm2"))
                return Run("IDtm2::Environment2", () => Check(Dtm2.Environment2(SystemTag, _frame, FdtXml.FrameInfo())), target);
            return Run("IDtm::Environment", () => Check(Dtm.Environment(SystemTag, _frame)), target);
        }

        public bool SetLanguage(int lcid) => Run($"IDtm::SetLanguage(0x{lcid:X4})", () => Check(Dtm.SetLanguage(lcid)), null, allowFalse: true);

        public bool SetSystemGuiLabel(string label) =>
            Supports("IDtm2") && Run("IDtm2::SetSystemGuiLabel", () => Check(Dtm2.SetSystemGuiLabel(FdtXml.SystemGuiLabel(label))), null, allowFalse: true);

        /// <summary>IDtmInformation::GetInformation — permitido a partir de "new enabled" (§4.2.2).</summary>
        public FdtXml.DtmInfo GetInformation()
        {
            if (!Supports("IDtmInformation")) { Record("IDtmInformation::GetInformation", false, null, "Interface não suportada"); return null; }
            Run("IDtmInformation::GetInformation", () =>
            {
                InformationXml = ((IDtmInformation)_instance).GetInformation();
                Information = FdtXml.ParseInformation(InformationXml);
                return $"FDT {Information.FdtVersion}; {Information.Vendor} {Information.Name} {Information.Version}; {Information.DeviceTypes.Count} device type(s)";
            }, null);
            return Information;
        }

        /// <summary>IDtm::InitNew(deviceType) — "new enabled" → "running" (§4.3.1.4).</summary>
        public bool InitNew(string dtmDeviceTypeOuterXml)
        {
            DeviceTypeXml = dtmDeviceTypeOuterXml;
            return Run("IDtm::InitNew", () => Check(Dtm.InitNew(FdtXml.InitDeviceType(dtmDeviceTypeOuterXml))), DtmState.Running);
        }

        /// <summary>IDtm::Config(userInfo) — "running" → "configured" (§4.3.1.1).</summary>
        public bool Config(string userLevel = "maintenance", string userName = null) =>
            Run("IDtm::Config", () => Check(Dtm.Config(FdtXml.UserInfo(_frame.ProjectName, userName ?? Environment.UserName, userLevel))), DtmState.Configured);

        public List<FdtXml.FunctionInfo> GetFunctions(string operationPhase, out string defaultFunctionId)
        {
            List<FdtXml.FunctionInfo> list = null;
            string def = null;
            Run("IDtm::GetFunctions", () =>
            {
                FunctionsXml = Dtm.GetFunctions(FdtXml.OperationPhase(operationPhase));
                list = FunctionsXml == null ? new List<FdtXml.FunctionInfo>() : FdtXml.ParseFunctions(FunctionsXml, out def);
                return $"{list.Count} função(ões); default={def}";
            }, null);
            defaultFunctionId = def;
            return list ?? new List<FdtXml.FunctionInfo>();
        }

        /// <summary>
        /// IDtm::SetCommunication(NULL) para DTM raiz (§4.3.1.11): "configured" → "communication set".
        /// Um ponteiro nulo não dá ao DTM nenhum meio de comunicação — operação segura em modo offline.
        /// </summary>
        public bool SetCommunicationNull() =>
            Run("IDtm::SetCommunication(NULL)", () => Check(Dtm.SetCommunication(null)), DtmState.CommunicationSet);

        /// <summary>
        /// IDtm::SetCommunication(ponteiro) para DTM filho (§4.3.1.11, §6.2.2): "configured" → "communication set".
        /// O Frame passa um <see cref="Comm.CommunicationGuard"/>, nunca o canal real diretamente.
        /// </summary>
        public bool SetCommunication(IFdtCommunication communication, string description) =>
            Run($"IDtm::SetCommunication({description})", () => Check(Dtm.SetCommunication(communication)), DtmState.CommunicationSet);

        /// <summary>
        /// IDtm::PrepareToReleaseCommunication → IDtmEvents::OnPreparedToReleaseCommunication (assíncrono, §4.3.1.8)
        /// → IDtm::ReleaseCommunication (§4.3.1.9): "communication set" → "configured".
        /// </summary>
        public bool ReleaseCommunication(TimeSpan timeout)
        {
            if (State != DtmState.CommunicationSet || Dtm == null) return false;
            PreparedToReleaseCommunication = false;
            bool notified = false;
            if (Run("IDtm::PrepareToReleaseCommunication", () => Check(Dtm.PrepareToReleaseCommunication()), null))
            {
                notified = PumpUntil(() => PreparedToReleaseCommunication, timeout);
                Record("aguardar IDtmEvents::OnPreparedToReleaseCommunication", notified, null, notified ? null : $"Evento não recebido em {timeout.TotalSeconds:0} s");
            }
            return Run("IDtm::ReleaseCommunication", () => Check(Dtm.ReleaseCommunication()), DtmState.Configured) && notified;
        }

        public bool PreparedToReleaseCommunication { get; private set; }

        /// <summary>Um canal exposto pelo DTM via IDtmChannel::GetChannels (§4.3.5.1).</summary>
        public sealed class ChannelRef
        {
            public IFdtChannel Channel { get; set; }
            public string Path { get; set; }
            public bool IsCommunicationChannel { get; set; }   // QI IFdtCommunication (canal gateway)
            public bool HasSubTopology { get; set; }           // QI IFdtChannelSubTopology
            public bool HasActiveX { get; set; }               // QI IFdtChannelActiveXInformation (UI do canal, §4.5.2)
            public string SupportedProtocolsXml { get; set; }
            public string ParametersXml { get; set; }
        }

        /// <summary>IDtmChannel::GetChannels + leitura do caminho, protocolos e parâmetros de cada canal (somente leitura).</summary>
        public List<ChannelRef> GetChannels(string protocolId = null)
        {
            var list = new List<ChannelRef>();
            if (!Supports("IDtmChannel")) { Record("IDtmChannel::GetChannels", false, null, "Interface não suportada"); return list; }
            Run("IDtmChannel::GetChannels", () =>
            {
                var coll = ((IDtmChannel)_instance).GetChannels();
                if (coll == null) return "coleção nula";
                foreach (object o in coll)
                {
                    var ch = o as IFdtChannel;
                    if (ch == null) continue;
                    var r = new ChannelRef { Channel = ch, IsCommunicationChannel = ch is IFdtCommunication, HasSubTopology = ch is IFdtChannelSubTopology, HasActiveX = ch is IFdtChannelActiveXInformation };
                    try { r.Path = ch.GetChannelPath(); } catch (Exception ex) { r.Path = "(erro: " + ex.Message + ")"; }
                    if (r.IsCommunicationChannel) try { r.SupportedProtocolsXml = ((IFdtCommunication)ch).GetSupportedProtocols(); } catch { }
                    if (protocolId != null) try { r.ParametersXml = ch.GetChannelParameters("", protocolId); } catch { }
                    list.Add(r);
                }
                return $"{list.Count} canal(is): " + string.Join("; ", list.Select(c => $"{c.Path}{(c.IsCommunicationChannel ? " [comm]" : "")}{(c.HasSubTopology ? " [subtopology]" : "")}{(c.HasActiveX ? " [ui]" : "")}"));
            }, null);
            return list;
        }

        /// <summary>IFdtChannelActiveXInformation::GetChannelFunctions (§4.5.2.3) — DTMChannelFunctionsSchema.</summary>
        public List<FdtXml.FunctionInfo> GetChannelFunctions(ChannelRef channel, string operationPhase, out string defaultFunctionId)
        {
            List<FdtXml.FunctionInfo> list = null;
            string def = null;
            if (!(channel.Channel is IFdtChannelActiveXInformation ui))
            {
                Record("IFdtChannelActiveXInformation::GetChannelFunctions", false, null, "O canal não tem UI (interface não suportada)");
                defaultFunctionId = null;
                return new List<FdtXml.FunctionInfo>();
            }
            Run($"IFdtChannelActiveXInformation::GetChannelFunctions({channel.Path})", () =>
            {
                string xml = ui.GetChannelFunctions(FdtXml.OperationPhase(operationPhase));
                list = xml == null ? new List<FdtXml.FunctionInfo>() : FdtXml.ParseFunctions(xml, out def);
                return $"{list.Count} função(ões) de canal; default={def}";
            }, null);
            defaultFunctionId = def;
            return list ?? new List<FdtXml.FunctionInfo>();
        }

        /// <summary>IFdtChannelSubTopology::ValidateAddChild + OnAddChild (§6.2.1.1).</summary>
        public bool AddChildToChannel(ChannelRef channel, string childSystemTag)
        {
            if (!(channel.Channel is IFdtChannelSubTopology st))
            {
                Record("IFdtChannelSubTopology", true, "canal sem sub-topologia; ligação aceita pelo Frame", null);
                return true;
            }
            if (!Run($"IFdtChannelSubTopology::ValidateAddChild({childSystemTag})", () => Check(st.ValidateAddChild(childSystemTag)), null)) return false;
            return Run($"IFdtChannelSubTopology::OnAddChild({childSystemTag})", () => { st.OnAddChild(childSystemTag); return null; }, null);
        }

        /// <summary>IFdtChannelSubTopology::ValidateRemoveChild + OnRemoveChild.</summary>
        public bool RemoveChildFromChannel(ChannelRef channel, string childSystemTag)
        {
            if (!(channel.Channel is IFdtChannelSubTopology st)) return true;
            Run($"IFdtChannelSubTopology::ValidateRemoveChild({childSystemTag})", () => Check(st.ValidateRemoveChild(childSystemTag)), null, allowFalse: true);
            return Run($"IFdtChannelSubTopology::OnRemoveChild({childSystemTag})", () => { st.OnRemoveChild(childSystemTag); return null; }, null);
        }

        public bool PrivateDialogEnabled(bool enabled) =>
            Run($"IDtm::PrivateDialogEnabled({enabled})", () => Check(Dtm.PrivateDialogEnabled(enabled)), null, allowFalse: true);

        /// <summary>IDtmActiveXInformation::GetActiveXGuid (§4.3.3.1). Retorna null/vazio se a função não tem UI ActiveX.</summary>
        public string GetActiveXGuid(string functionCall)
        {
            if (!Supports("IDtmActiveXInformation")) return null;
            string guid = null;
            Run("IDtmActiveXInformation::GetActiveXGuid", () => guid = ((IDtmActiveXInformation)_instance).GetActiveXGuid(functionCall), null);
            return string.IsNullOrWhiteSpace(guid) ? null : guid;
        }

        public string GetActiveXProgId(string functionCall)
        {
            if (!Supports("IDtmActiveXInformation")) return null;
            string progId = null;
            Run("IDtmActiveXInformation::GetActiveXProgId", () => progId = ((IDtmActiveXInformation)_instance).GetActiveXProgId(functionCall), null, allowFalse: true);
            return progId;
        }

        /// <summary>IDtmApplication::StartApplication (§4.3.4) — UI não embutível do próprio DTM.</summary>
        public bool StartApplication(string invokeId, string functionCall, string title)
        {
            if (!Supports("IDtmApplication")) return false;
            _invokeIds.Add(invokeId);
            return Run("IDtmApplication::StartApplication", () => Check(((IDtmApplication)_instance).StartApplication(invokeId, functionCall, title)), null);
        }

        public bool ExitApplication(string invokeId) =>
            Supports("IDtmApplication") && Run("IDtmApplication::ExitApplication", () => Check(((IDtmApplication)_instance).ExitApplication(invokeId)), null);

        /// <summary>
        /// IDtm::InvokeFunctionRequest (§4.3.1.5): inicia uma função que não tem controle ActiveX — sem UI, ou com
        /// janela própria do DTM (permitida por PrivateDialogEnabled). Assíncrona: o fim chega por
        /// IDtmEvents::OnInvokedFunctionFinished (sem UI) ou IDtmEvents::OnApplicationClosed (com UI).
        /// </summary>
        public bool InvokeFunction(string invokeId, string functionCall)
        {
            _invokeIds.Add(invokeId);
            return Run("IDtm::InvokeFunctionRequest", () => Check(Dtm.InvokeFunctionRequest(invokeId, functionCall)), null);
        }

        /// <summary>True quando chegou OnInvokedFunctionFinished ou OnApplicationClosed para o invokeId.</summary>
        public bool FunctionFinished(string invokeId) =>
            Events.Exists(e => (e.Method == "OnInvokedFunctionFinished" || e.Method == "OnApplicationClosed") && string.Equals(e.InvokeId, invokeId, StringComparison.OrdinalIgnoreCase));

        public void TrackInvokeId(string invokeId) => _invokeIds.Add(invokeId);
        internal bool OwnsInvokeId(string invokeId) => _invokeIds.Contains(invokeId);

        // ================================================================ liberação

        /// <summary>
        /// IDtm::PrepareToRelease → aguarda IDtmEvents::OnPreparedToRelease (assíncrono, §4.3.1.7) → libera a referência COM.
        /// Mesmo sem o evento (timeout), a referência é liberada para não vazar o objeto.
        /// </summary>
        public bool Release(TimeSpan timeout)
        {
            bool notified = false;
            if (State == DtmState.CommunicationSet && _frame.HasParentLink(SystemTag))
                _frame.Disconnect(SystemTag, timeout); // liberar a cadeia antes do DTM (§4.3.1.11, ordem inversa)
            _frame.DisconnectChildrenOf(SystemTag, timeout);   // filhos antes do pai
            if (_instance != null && State != DtmState.Up && State != DtmState.NewCreated && State != DtmState.ExistingCreated && State != DtmState.Failed)
            {
                if (Run("IDtm::PrepareToRelease", () => Check(Dtm.PrepareToRelease()), null))
                {
                    var sw = Stopwatch.StartNew();
                    notified = PumpUntil(() => PreparedToRelease, timeout);
                    Record("aguardar IDtmEvents::OnPreparedToRelease", notified, notified ? $"{sw.ElapsedMilliseconds} ms" : null,
                        notified ? null : $"Evento não recebido em {timeout.TotalSeconds:0} s");
                    if (notified) State = DtmState.Zombie;
                }
            }
            ReleaseComObject();
            return notified;
        }

        private void ReleaseComObject()
        {
            if (_instance == null) return;
            var sw = Stopwatch.StartNew();
            Trace?.Invoke($"-> {SystemTag} IUnknown::Release (final)");
            // DTM .NET in-process: o objeto é gerenciado (não é RCW); a liberação fica com o GC.
            int remaining = Marshal.IsComObject(_instance) ? Marshal.FinalReleaseComObject(_instance) : 0;
            _instance = null;
            _frame.Unregister(this);
            State = DtmState.Released;
            Record("IUnknown::Release (final)", true, $"refs RCW restantes={remaining}", null, sw.ElapsedMilliseconds);
            Trace?.Invoke("-> GC.Collect/WaitForPendingFinalizers");
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Trace?.Invoke("<- GC concluído");
        }

        public void Dispose() { if (_instance != null) Release(TimeSpan.FromSeconds(5)); }

        // ================================================================ eventos do Frame

        internal void OnFrameEvent(FrameEvent e)
        {
            Events.Add(e);
            if (e.Method == "OnPreparedToRelease") PreparedToRelease = true;
            if (e.Method == "OnPreparedToReleaseCommunication") PreparedToReleaseCommunication = true;
        }

        /// <summary>Bombeia mensagens Windows (necessário para DTMs que notificam via fila de mensagens).</summary>
        public static bool PumpUntil(Func<bool> condition, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (!condition())
            {
                if (sw.Elapsed > timeout) return false;
                Application.DoEvents();
                Thread.Sleep(10);
            }
            return true;
        }

        // ================================================================ infraestrutura

        private static string Check(bool b) => b ? "TRUE" : "FALSE";

        /// <summary>
        /// Executa uma chamada ao DTM, registrando resultado/erro. Um retorno "FALSE" é falha FDT (§4.10.2);
        /// HRESULT de erro é falha COM. Em caso de falha o estado não muda (§4.2.2, "transition … in case of errors").
        /// </summary>
        private bool Run(string step, Func<string> call, DtmState? nextState, bool allowFalse = false)
        {
            if (Thread.CurrentThread.ManagedThreadId != _threadId)
                throw new InvalidOperationException("Chamada ao DTM fora da thread STA dona da instância.");
            var sw = Stopwatch.StartNew();
            Trace?.Invoke($"-> {SystemTag} {step}");
            try
            {
                string r = call();
                bool ok = r != "FALSE" || allowFalse;
                if (ok && r != "FALSE" && nextState.HasValue) State = nextState.Value;
                Record(step, ok, r, ok ? null : "DTM retornou FALSE", sw.ElapsedMilliseconds);
                return ok && r != "FALSE";
            }
            catch (Exception ex)
            {
                var inner = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                var rec = new StepRecord
                {
                    Step = step, Ok = false, Error = inner.GetType().Name + ": " + inner.Message.Trim(),
                    HResult = "0x" + inner.HResult.ToString("X8"), DurationMs = sw.ElapsedMilliseconds, StateAfter = State.ToString(),
                };
                Steps.Add(rec);
                Trace?.Invoke($"<- {SystemTag} {rec}");
                return false;
            }
        }

        private void Record(string step, bool ok, string result, string error, long ms = 0)
        {
            var r = new StepRecord { Step = step, Ok = ok, Result = result, Error = error, DurationMs = ms, StateAfter = State.ToString() };
            Steps.Add(r);
            Trace?.Invoke($"<- {SystemTag} {r}");
        }
    }
}
