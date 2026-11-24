using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows.Forms;
using Jigfdt.Fdt100;
using LasecFDT.Core;
using LasecFDT.Core.Project;
using LasecFDT.Core.Rpc;
using LasecFDT.Fdt1.Com;
using LasecFDT.Fdt1.Comm;
using LasecFDT.Fdt1.Frame;
using LasecFDT.Fdt1.Ui;

namespace LasecFDT.Worker.Fdt1
{
    /// <summary>
    /// Janela invisível dona da thread STA do worker. Recebe requisições JSON-RPC numa thread de leitura e as
    /// executa em FILA na STA (uma por vez): as esperas do protocolo FDT bombeiam mensagens (DoEvents), e sem a
    /// fila uma segunda requisição poderia executar aninhada no meio da primeira.
    /// </summary>
    internal sealed class WorkerHost : Form, IFrameUi
    {
        private readonly JsonLineChannel _rpc;
        private readonly string _dep;
        private readonly string _log;
        private readonly FrameContainer _frame;
        private readonly Queue<RpcMessage> _queue = new Queue<RpcMessage>();
        private bool _busy;
        private bool _shuttingDown;

        private DtmSession _session;
        private TopologyLink _childLink;               // este DTM como filho (canal recebido)
        private readonly List<DtmSession.ChannelRef> _channels = new List<DtmSession.ChannelRef>();
        private DtmWindow _window;
        private static readonly TimeSpan T = TimeSpan.FromSeconds(15);

        public int ExitCode { get; private set; }

        /// <summary>Garante que respostas/eventos pendentes saíram antes de encerrar o processo.</summary>
        public void FlushOutgoing() => _rpc.Drain(TimeSpan.FromSeconds(5));

        public WorkerHost(System.IO.Stream pipe, string dep, string log)
        {
            _rpc = new JsonLineChannel(pipe);
            _dep = dep;
            _log = log;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            Opacity = 0;
            Size = new Size(1, 1);
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);

            _frame = new FrameContainer { ProjectName = "LasecFDT", Ui = this };
            _frame.EventRaised += e => Notify(new WorkerEvent { Kind = "frame", Interface = e.Interface, Method = e.Method, SystemTag = e.SystemTag, InvokeId = e.InvokeId, Detail = e.Detail });
            DtmSession.Trace = m => WriteLog(_log, m);
            CommunicationGuard.Trace = m => { WriteLog(_log, m); Notify(new WorkerEvent { Kind = "comm", Detail = m }); };

            // A janela nunca fica visível (SetVisibleCore), então Load não dispara: o handle (necessário para
            // BeginInvoke) é criado aqui, e a leitura do pipe começa em seguida.
            CreateHandle();
            WriteLog(_log, $"worker iniciado: pid={System.Diagnostics.Process.GetCurrentProcess().Id} {_dep}");
            new Thread(ReadLoop) { IsBackground = true, Name = "rpc-reader" }.Start();
            _sta = Thread.CurrentThread;
            new Thread(Watchdog) { IsBackground = true, Name = "watchdog" }.Start();
        }

        protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false); // nunca visível

        public static void WriteLog(string path, string m)
        {
            if (path == null) return;
            try { lock (typeof(WorkerHost)) File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {m}{Environment.NewLine}"); } catch { }
        }

        // ================================================================ transporte

        private void ReadLoop()
        {
            try
            {
                while (true)
                {
                    var m = _rpc.Read();
                    if (m == null) break;
                    BeginInvoke(new Action(() => Enqueue(m)));
                }
            }
            catch (Exception ex) { WriteLog(_log, "leitura do pipe: " + ex.Message); }
            // Supervisor sumiu: liberar o DTM pelo protocolo e encerrar.
            try { BeginInvoke(new Action(() => Shutdown(fromPipeLoss: true))); } catch { }
        }

        private void Enqueue(RpcMessage m)
        {
            _queue.Enqueue(m);
            if (!_busy) ProcessQueue();
        }

        private void ProcessQueue()
        {
            while (_queue.Count > 0 && !_shuttingDown)
            {
                var m = _queue.Dequeue();
                _busy = true;
                try { HandleRequest(m); }
                finally { _busy = false; }
            }
        }

        private Thread _sta;
        private string _current;
        private DateTime _currentSince;

        /// <summary>Cão de guarda: registra a pilha da STA se uma requisição demorar (diagnóstico de travamentos em DTMs).</summary>
        private void Watchdog()
        {
            string reported = null;
            while (true)
            {
                Thread.Sleep(2000);
                string cur = _current;
                if (cur == null || cur == reported || DateTime.Now - _currentSince < TimeSpan.FromSeconds(10)) continue;
                reported = cur;
                string stack;
#pragma warning disable 618 // StackTrace(Thread) — obsoleto, mas é o único meio no .NET Framework para outra thread
                try { _sta.Suspend(); try { stack = new System.Diagnostics.StackTrace(_sta, false).ToString(); } finally { _sta.Resume(); } }
                catch (Exception ex) { stack = "(pilha indisponível: " + ex.Message + ")"; }
#pragma warning restore 618
                WriteLog(_log, $"[watchdog] '{cur}' há {(DateTime.Now - _currentSince).TotalSeconds:0} s; pilha da STA:{Environment.NewLine}{stack}");
            }
        }

        private void HandleRequest(RpcMessage m)
        {
            WriteLog(_log, "<- " + m.Method);
            _current = m.Method + "#" + m.Id; _currentSince = DateTime.Now;
            RpcMessage reply;
            try
            {
                var result = Dispatch(m.Method, m.Params);
                reply = new RpcMessage { Id = m.Id, Result = result ?? JsonValue.Create(true) };
            }
            catch (RpcException ex) { reply = new RpcMessage { Id = m.Id, Error = new RpcError { Code = ex.Code, Message = ex.Message } }; }
            catch (Exception ex)
            {
                var inner = ex is System.Reflection.TargetInvocationException t && t.InnerException != null ? t.InnerException : ex;
                reply = new RpcMessage { Id = m.Id, Error = new RpcError { Code = RpcException.Internal, Message = inner.GetType().Name + ": " + inner.Message } };
            }
            _current = null;
            WriteLog(_log, "-> " + m.Method + (reply.Error != null ? " ERRO " + reply.Error.Message : " ok"));
            try { _rpc.Write(reply); } catch (Exception ex) { WriteLog(_log, "escrita no pipe: " + ex.Message); }
            if (m.Method == WorkerMethods.Shutdown) BeginInvoke(new Action(() => { ExitCode = 0; Close(); }));
        }

        private void Notify(WorkerEvent e)
        {
            WriteLog(_log, $"[evento] {e.Kind} {e.Interface}.{e.Method} {e.Detail}");
            try { _rpc.Write(new RpcMessage { Method = WorkerMethods.Event, Params = JsonLineChannel.ToNode(e) }); } catch { }
        }

        private static T P<T>(JsonNode n) where T : new() => n == null ? new T() : JsonLineChannel.FromNode<T>(n);
        private static JsonNode R<T>(T v) => JsonLineChannel.ToNode(v);

        // ================================================================ métodos

        private JsonNode Dispatch(string method, JsonNode p)
        {
            switch (method)
            {
                case WorkerMethods.Hello:
                    return R(new WorkerInfo
                    {
                        ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id,
                        Architecture = Environment.Is64BitProcess ? "x64" : "x86",
                        Dep = _dep,
                        Version = typeof(WorkerHost).Assembly.GetName().Version.ToString(),
                        SchemaPath = _frame.SchemaPath,
                    });

                case WorkerMethods.Describe:
                {
                    RequireNoSession();
                    var a = P<DescribeParams>(p);
                    var clsid = DtmLifecycle.ResolveClsid(a.Dtm);
                    var info = DtmLifecycle.Describe(_frame, clsid, a.Lcid);
                    return R(new DtmDescription
                    {
                        Clsid = Core.FdtIds.ToFdtUuid(clsid), FdtVersion = info.FdtVersion, Name = info.Name, Vendor = info.Vendor, Version = info.Version, Date = info.Date,
                        DeviceTypes = info.DeviceTypes.Select(d => new DeviceTypeDto { Index = d.Index, Name = d.Name, Vendor = d.Vendor, Version = d.Version, BusCategories = d.BusCategories, ManufacturerId = d.ManufacturerId, DeviceTypeId = d.DeviceTypeId }).ToList(),
                    });
                }

                case WorkerMethods.Create:
                {
                    RequireNoSession();
                    var a = P<CreateDtmParams>(p);
                    var clsid = DtmLifecycle.ResolveClsid(a.Dtm);
                    _session = a.Properties != null && a.Properties.Count > 0
                        ? DtmLifecycle.CreateWithProperties(_frame, clsid, a.Properties, m => WriteLog(_log, m), a.DeviceTypeIndex, a.Lcid, a.SystemTag)
                        : DtmLifecycle.CreateConfigured(_frame, clsid, null, a.DeviceTypeIndex, a.Lcid, a.SystemTag);
                    return R(Summary());
                }

                case WorkerMethods.Load:
                {
                    RequireNoSession();
                    var a = P<LoadParams>(p);
                    _session = DtmLifecycle.LoadConfigured(_frame, DtmLifecycle.ResolveClsid(a.Dtm), a.DataSet, a.Lcid, a.SystemTag);
                    return R(Summary());
                }

                case WorkerMethods.Save:
                    return R(Session().PersistSave() ?? throw Fail("IPersistXXX::Save"));

                case WorkerMethods.GetFunctions:
                    return R(Session().GetFunctions("notSupported", out _).Select(f => new FunctionDto
                    { FunctionId = f.FunctionId, Name = f.Name, ApplicationId = f.ApplicationId, Enabled = f.Enabled, Hidden = f.Hidden }).ToList());

                case WorkerMethods.SetCommunicationNull:
                    if (!Session().SetCommunicationNull()) throw Fail("IDtm::SetCommunication(NULL)");
                    return JsonValue.Create(true);

                case WorkerMethods.GetChannels:
                    _channels.Clear();
                    _channels.AddRange(Session().GetChannels(HartSimulatedChannel.HartProtocolId));
                    return R(_channels.Select(c => new ChannelDto { Path = c.Path, IsCommunicationChannel = c.IsCommunicationChannel, HasSubTopology = c.HasSubTopology, HasActiveX = c.HasActiveX, SupportedProtocolsXml = c.SupportedProtocolsXml }).ToList());

                case WorkerMethods.InvokeFunction:
                {
                    var a = P<InvokeParams>(p);
                    var f = Session().GetFunctions("notSupported", out _).FirstOrDefault(x => x.FunctionId == a.FunctionId)
                            ?? throw new RpcException(RpcException.InvalidParams, "Função inexistente: " + a.FunctionId);
                    string invokeId = Guid.NewGuid().ToString("D").ToUpperInvariant();
                    // A chamada é assíncrona pela spec, mas um DTM pode abrir um diálogo modal dentro dela: a resposta
                    // RPC sai antes, e a chamada ao DTM roda na próxima volta do laço de mensagens.
                    BeginInvoke(new Action(() =>
                    {
                        // Ocupado durante a chamada: se o DTM abrir um diálogo modal, requisições que chegarem ficam
                        // na fila em vez de executarem aninhadas dentro do laço modal do DTM.
                        bool prev = _busy;
                        _busy = true;
                        try
                        {
                            Notify(new WorkerEvent { Kind = "ui", Method = "functionStarting", InvokeId = invokeId, Detail = f.Name });
                            bool ok = _session != null && _session.InvokeFunction(invokeId, FdtXml.FunctionCall(f.FunctionId, f.ApplicationId));
                            Notify(new WorkerEvent { Kind = "ui", Method = ok ? "functionStarted" : "functionRefused", InvokeId = invokeId, Detail = f.Name + (ok ? "" : ": " + LastError()) });
                        }
                        finally { _busy = prev; }
                        if (!prev) ProcessQueue();
                    }));
                    return R(new InvokeResult { Started = true, InvokeId = invokeId, FunctionName = f.Name });
                }

                case WorkerMethods.WaitFunction:
                {
                    var a = P<WaitFunctionParams>(p);
                    var s = Session();
                    return JsonValue.Create(DtmSession.PumpUntil(() => s.FunctionFinished(a.InvokeId), TimeSpan.FromMilliseconds(a.TimeoutMs)));
                }

                case WorkerMethods.GetChannelFunctions:
                    return R(Session().GetChannelFunctions(Channel(P<ChannelRefParams>(p).ChannelPath), "notSupported", out _).Select(f => new FunctionDto
                    { FunctionId = f.FunctionId, Name = f.Name, ApplicationId = f.ApplicationId, Enabled = f.Enabled, Hidden = f.Hidden }).ToList());

                case WorkerMethods.OpenChannelUi:
                {
                    var a = P<ChannelUiParams>(p);
                    var ch = Channel(a.ChannelPath);
                    var funcs = Session().GetChannelFunctions(ch, "notSupported", out var def);
                    return R(OpenWindow(funcs, def, a.FunctionId, a.OwnerHwnd, a.Visible, a.Embedded, $"canal {ch.Path}",
                        f => DtmActiveXHost.ForChannel(_session, ch, FdtXml.FunctionCall(f.FunctionId, f.ApplicationId))));
                }

                case WorkerMethods.MarshalChannel:
                {
                    var ch = Channel(P<ChannelRefParams>(p).ChannelPath);
                    if (!ch.IsCommunicationChannel) throw new RpcException(RpcException.InvalidParams, $"O canal {ch.Path} não é canal de comunicação.");
                    var bytes = ComMarshal.Marshal(ch.Channel, typeof(IFdtCommunication).GUID);
                    return R(new MarshaledChannel { ChannelPath = ch.Path, ObjRefBase64 = Convert.ToBase64String(bytes) });
                }

                case WorkerMethods.AddChild:
                {
                    var a = P<ChildParams>(p);
                    var link = _frame.AddRemoteChild(Session(), Channel(a.ChannelPath), a.ChildTag);
                    if (link == null) throw Fail("ValidateAddChild/OnAddChild");
                    return JsonValue.Create(true);
                }

                case WorkerMethods.RemoveChild:
                    return JsonValue.Create(_frame.RemoveRemoteChild(P<ChildParams>(p).ChildTag));

                case WorkerMethods.ConnectChannel:
                {
                    var a = P<ConnectChannelParams>(p);
                    var proxy = (IFdtCommunication)ComMarshal.Unmarshal(Convert.FromBase64String(a.ObjRefBase64), typeof(IFdtCommunication).GUID);
                    _childLink = _frame.ConnectToRemoteChannel(Session(), proxy, a.ParentTag, a.ChannelPath, ParsePolicy(a.Policy));
                    return R(new ConnectResult { Accepted = _childLink.CommunicationAccepted, State = _session.State.ToString(), Error = _childLink.CommunicationAccepted ? null : LastError() });
                }

                case WorkerMethods.ConnectSimulator:
                {
                    var a = P<SimulatorParams>(p);
                    var sim = new HartSimulatedChannel("SIM-HART", HartDevice.FromDeviceType(Session().DeviceTypeXml));
                    _childLink = _frame.ConnectToSimulator(_session, sim, ParsePolicy(a.Policy));
                    return R(new ConnectResult { Accepted = _childLink.CommunicationAccepted, State = _session.State.ToString(), Error = _childLink.CommunicationAccepted ? null : LastError() });
                }

                case WorkerMethods.ReleaseCommunication:
                {
                    bool ok = _session != null && _frame.Disconnect(_session.SystemTag, T);
                    _childLink = null;
                    return JsonValue.Create(ok);
                }

                case WorkerMethods.Scan:
                {
                    var a = P<ScanParams>(p);
                    var ch = Channel(a.ChannelPath);
                    if (!ch.HasSubTopology) throw new RpcException(RpcException.InvalidParams, $"O canal {ch.Path} não oferece busca de instrumentos (IFdtChannelSubTopology).");
                    string invokeId = Guid.NewGuid().ToString("D").ToUpperInvariant();
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    bool accepted;
                    try { accepted = ((IFdtChannelSubTopology)ch.Channel).ScanRequest(invokeId); }
                    catch (Exception ex) { return R(new ScanResult { InvokeId = invokeId, Error = ex.Message }); }
                    if (!accepted) return R(new ScanResult { InvokeId = invokeId, Error = "O DTM recusou a busca (ScanRequest = FALSE)." });
                    // A resposta vem por IDtmEvents::OnScanResponse; o progresso (OnProgress) segue como evento para a UI.
                    bool done = DtmSession.PumpUntil(() => { lock (_frame.ScanResponses) return _frame.ScanResponses.ContainsKey(invokeId); }, TimeSpan.FromMilliseconds(a.TimeoutMs));
                    string xml = null;
                    if (done) lock (_frame.ScanResponses) { xml = _frame.ScanResponses[invokeId]; _frame.ScanResponses.Remove(invokeId); }
                    return R(new ScanResult { Accepted = true, Completed = done, InvokeId = invokeId, Xml = xml, DurationMs = sw.ElapsedMilliseconds,
                                              Error = done ? null : $"Sem resposta da busca em {a.TimeoutMs / 1000} s." });
                }

                case WorkerMethods.GetParameters:
                {
                    var a = P<ParameterPathParams>(p);
                    var ip = Session().ComObject as IDtmParameter ?? throw new RpcException(RpcException.InvalidParams, "O DTM não implementa IDtmParameter.");
                    return JsonValue.Create(ip.GetParameters(string.IsNullOrEmpty(a.Path) ? ParameterRootPath() : a.Path));
                }

                case WorkerMethods.SetParameters:
                {
                    var a = P<SetParametersParams>(p);
                    var ip = Session().ComObject as IDtmParameter ?? throw new RpcException(RpcException.InvalidParams, "O DTM não implementa IDtmParameter.");
                    return JsonValue.Create(ip.SetParameters(string.IsNullOrEmpty(a.Path) ? ParameterRootPath() : a.Path, a.Xml));
                }

                case WorkerMethods.Upload:
                case WorkerMethods.Download:
                    return R(OnlineParameter(method == WorkerMethods.Upload, P<OnlineParameterParams>(p)));

                case WorkerMethods.OnlineCompare:
                    return JsonValue.Create((Session().ComObject as IDtmOnlineDiagnosis ?? throw new RpcException(RpcException.InvalidParams, "O DTM não implementa IDtmOnlineDiagnosis.")).Compare());

                case WorkerMethods.DeviceStatus:
                    return JsonValue.Create((Session().ComObject as IDtmOnlineDiagnosis ?? throw new RpcException(RpcException.InvalidParams, "O DTM não implementa IDtmOnlineDiagnosis.")).GetDeviceStatus());

                case WorkerMethods.GetCommLog:
                    return R((_childLink?.Guard?.Log ?? new List<CommRecord>()).Select(r => new CommRecordDto
                    { Time = r.Time.ToString("HH:mm:ss.fff"), Direction = r.Direction, Method = r.Method, Forwarded = r.Forwarded, Note = r.Note, Xml = r.Xml }).ToList());

                case WorkerMethods.WaitOnline:
                {
                    var a = P<WaitParams>(p);
                    var s = Session();
                    return JsonValue.Create(DtmSession.PumpUntil(() => s.Events.Any(e => e.Method == "OnOnlineStateChanged" && e.Detail == "online=True"), TimeSpan.FromMilliseconds(a.TimeoutMs)));
                }

                case WorkerMethods.OpenUi:
                    return R(OpenUi(P<OpenUiParams>(p)));

                case WorkerMethods.CloseUi:
                    return JsonValue.Create(CloseUi());

                case WorkerMethods.Screenshot:
                    return JsonValue.Create(Screenshot(P<ScreenshotParams>(p).Path));

                case WorkerMethods.Release:
                    return JsonValue.Create(ReleaseAll());

                case WorkerMethods.Shutdown:
                    _shuttingDown = true;
                    ReleaseAll();
                    return JsonValue.Create(true);

                default:
                    throw new RpcException(RpcException.MethodNotFound, "Método desconhecido: " + method);
            }
        }

        /// <summary>Caminho da raiz do documento de parâmetros: "/FDT" (raiz de todo documento FDT).</summary>
        private static string ParameterRootPath() => "/FDT";

        /// <summary>
        /// IDtmOnlineParameter::UploadRequest/DownloadRequest (§4.3.12): assíncronos; o resultado chega por
        /// IDtmEvents::OnUploadFinished/OnDownloadFinished com o mesmo invokeId. Erros do DTM chegam por OnErrorMessage.
        /// </summary>
        private OnlineParameterResult OnlineParameter(bool upload, OnlineParameterParams a)
        {
            var s = Session();
            var op = s.ComObject as IDtmOnlineParameter ?? throw new RpcException(RpcException.InvalidParams, "O DTM não implementa IDtmOnlineParameter.");
            string path = string.IsNullOrEmpty(a.Path) ? ParameterRootPath() : a.Path;
            string invokeId = Guid.NewGuid().ToString("D").ToUpperInvariant();
            var r = new OnlineParameterResult { InvokeId = invokeId, Path = path };
            string finished = upload ? "OnUploadFinished" : "OnDownloadFinished";
            bool? success = null;
            Action<FrameEvent> h = e =>
            {
                if (e.Method == finished && string.Equals(e.InvokeId, invokeId, StringComparison.OrdinalIgnoreCase)) success = e.Detail == "success=True";
                else if (e.Method == "OnErrorMessage" && (e.SystemTag == null || e.SystemTag == s.SystemTag)) lock (r.Messages) r.Messages.Add(e.Detail);
            };
            _frame.EventRaised += h;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                try { r.Accepted = upload ? op.UploadRequest(invokeId, path) : op.DownloadRequest(invokeId, path); }
                catch (Exception ex) { r.Error = ex.Message; return r; }
                if (!r.Accepted) { r.Error = $"O DTM recusou {(upload ? "UploadRequest" : "DownloadRequest")}({path})."; return r; }
                r.Completed = DtmSession.PumpUntil(() => success.HasValue, TimeSpan.FromMilliseconds(a.TimeoutMs));
                r.Success = success == true;
                if (!r.Completed)
                {
                    try { op.CancelAction(invokeId); } catch { }
                    r.Error = $"Sem {finished} em {a.TimeoutMs / 1000} s (ação cancelada).";
                }
                else if (!r.Success) r.Error = r.Messages.LastOrDefault() ?? "o DTM informou falha sem detalhar";
                return r;
            }
            finally { _frame.EventRaised -= h; r.DurationMs = sw.ElapsedMilliseconds; }
        }

        private static CommPolicy ParsePolicy(string s) =>
            Enum.TryParse<CommPolicy>(s ?? "Blocked", true, out var p) ? p : throw new RpcException(RpcException.InvalidParams, "Política inválida: " + s);

        private void RequireNoSession()
        {
            if (_session != null) throw new RpcException(RpcException.InvalidParams, "Este worker já hospeda uma instância de DTM (um processo por instância).");
        }

        private DtmSession Session() => _session ?? throw new RpcException(RpcException.InvalidParams, "Nenhuma instância de DTM criada (use create ou load).");

        private DtmSession.ChannelRef Channel(string path)
        {
            if (_channels.Count == 0) _channels.AddRange(Session().GetChannels(HartSimulatedChannel.HartProtocolId));
            return _channels.FirstOrDefault(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase))
                   ?? throw new RpcException(RpcException.InvalidParams, "Canal inexistente: " + path);
        }

        private string LastError() => _session?.Steps.LastOrDefault(x => !x.Ok)?.Error;
        private RpcException Fail(string what) => new RpcException(RpcException.DtmFailure, $"Falha em {what}: {LastError() ?? "(sem detalhe)"}");

        private DtmSummary Summary() => new DtmSummary
        {
            SystemTag = _session.SystemTag,
            Clsid = _session.ClsidString,
            State = _session.State.ToString(),
            FdtVersion = _session.Information?.FdtVersion,
            Name = _session.Information?.Name,
            Vendor = _session.Information?.Vendor,
            Version = _session.Information?.Version,
            DeviceTypeXml = _session.DeviceTypeXml,
            PersistenceKind = _session.PersistenceKind,
            Interfaces = _session.Interfaces.Where(k => k.Value).Select(k => k.Key).ToList(),
        };

        // ================================================================ UI do DTM

        private UiResult OpenUi(OpenUiParams a)
        {
            var s = Session();
            var funcs = s.GetFunctions("notSupported", out var def);
            return OpenWindow(funcs, def, a.FunctionId, a.OwnerHwnd, a.Visible, a.Embedded, null,
                f => new DtmActiveXHost(s, FdtXml.FunctionCall(f.FunctionId, f.ApplicationId)));
        }

        /// <summary>Tenta as funções na ordem: pedida, default, parametrização/configuração offline, demais.</summary>
        private UiResult OpenWindow(List<FdtXml.FunctionInfo> all, string def, string functionId, long owner, bool visible, bool embedded, string what,
                                    Func<FdtXml.FunctionInfo, DtmActiveXHost> hostFactory)
        {
            var s = Session();
            if (_window != null) throw new RpcException(RpcException.InvalidParams, "Já há uma UI aberta neste worker.");
            var funcs = all.Where(f => f.Enabled && !f.Hidden).ToList();
            var ordered = funcs.Where(f => f.FunctionId == functionId)
                .Concat(funcs.Where(f => functionId == null && f.FunctionId == def))
                .Concat(funcs.Where(f => functionId == null && (f.ApplicationId == "fdtOfflineParameterize" || f.ApplicationId == "fdtConfiguration")))
                .Concat(functionId == null ? funcs : Enumerable.Empty<FdtXml.FunctionInfo>())
                .Distinct().ToList();
            if (ordered.Count == 0) return new UiResult { Opened = false, Error = functionId == null ? "Sem funções visíveis." : "Função inexistente: " + functionId };

            var errors = new List<string>();
            foreach (var f in ordered)
            {
                var w = new DtmWindow($"{s.Information?.Name ?? s.ClsidString}{(what != null ? " — " + what : "")} — {f.Name} [{s.SystemTag}]", embedded);
                if (owner != 0) w.SetOwnerHandle(new IntPtr(owner));
                if (visible) w.Show(); else w.CreateControl();
                var host = hostFactory(f);
                if (host.Open(w.Body))
                {
                    w.Host = host;
                    w.UserClosing += () => { CloseUi(); Notify(new WorkerEvent { Kind = "ui", Method = "closedByUser", SystemTag = s.SystemTag }); };
                    _window = w;
                    return new UiResult { Opened = true, FunctionId = f.FunctionId, FunctionName = f.Name, ControlClsid = host.ControlClsid, WindowHandle = w.Handle.ToInt64() };
                }
                errors.Add($"{f.FunctionId} '{f.Name}': {host.LastError}");
                w.ForceClose();
            }
            return new UiResult { Opened = false, Error = string.Join(" | ", errors) };
        }

        private bool CloseUi()
        {
            if (_window == null) return true;
            var w = _window;
            _window = null;
            bool ok = w.Host?.Close(T) ?? true;
            w.ForceClose();
            return ok;
        }

        private int Screenshot(string path)
        {
            if (_window?.Host?.Control == null) throw new RpcException(RpcException.InvalidParams, "Nenhuma UI aberta.");
            DtmSession.PumpUntil(() => false, TimeSpan.FromMilliseconds(300));
            // PrintWindow renderiza a própria janela, mesmo coberta por outras (o worker roda em segundo plano
            // e não pode tomar o primeiro plano); CopyFromScreen copiaria o que estiver por cima.
            var win = new Rectangle(Point.Empty, _window.Size);
            var area = _window.Host.Control.RectangleToScreen(_window.Host.Control.ClientRectangle);
            var origin = _window.Location;
            area.Offset(-origin.X, -origin.Y);
            using (var bmp = new Bitmap(win.Width, win.Height))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    try { PrintWindow(_window.Handle, hdc, 2 /*PW_RENDERFULLCONTENT*/); }
                    finally { g.ReleaseHdc(hdc); }
                }
                if (path != null) bmp.Save(path, ImageFormat.Png);
                var rel = area;
                rel.Intersect(new Rectangle(Point.Empty, bmp.Size));
                var colors = new HashSet<int>();
                for (int y = rel.Top; y < rel.Bottom; y += 3)
                    for (int x = rel.Left; x < rel.Right; x += 3)
                        colors.Add(bmp.GetPixel(x, y).ToArgb());
                return colors.Count;
            }
        }

        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        // ================================================================ liberação

        private bool ReleaseAll()
        {
            bool ok = CloseUi();
            if (_session == null) return ok;
            if (_childLink != null) { ok &= _frame.Disconnect(_session.SystemTag, T); _childLink = null; }
            ok &= _session.Release(T);   // filhos remotos: RemoveRemoteChild já deve ter sido pedido pelo supervisor
            _session = null;
            _channels.Clear();
            LegacyActiveXProcess.CleanupComOnStaThread();
            return ok;
        }

        private void Shutdown(bool fromPipeLoss)
        {
            if (_shuttingDown && !fromPipeLoss) return;
            _shuttingDown = true;
            WriteLog(_log, "pipe fechado: liberando o DTM e encerrando");
            try { ReleaseAll(); } catch (Exception ex) { WriteLog(_log, "liberação: " + ex.Message); }
            ExitCode = 4;
            Close();
        }

        // ================================================================ IFrameUi

        public bool OpenActiveXRequest(string systemTag, string functionCall, bool modal)
        {
            Notify(new WorkerEvent { Kind = "ui", Method = "openRequested", SystemTag = systemTag, Detail = functionCall });
            return false; // abrir telas adicionais pedidas pelo DTM: decisão da UI (Fase 4)
        }

        public bool CloseActiveXRequest(string invokeId)
        {
            if (_window?.Host == null || !string.Equals(_window.Host.InvokeId, invokeId, StringComparison.OrdinalIgnoreCase)) return false;
            BeginInvoke(new Action(() => { CloseUi(); Notify(new WorkerEvent { Kind = "ui", Method = "closedByDtm", InvokeId = invokeId }); }));
            return true;
        }

        public string UserDialog(string systemTag, string userMessage) => null; // resposta padrão; a UI (Fase 4) poderá exibir
    }

    /// <summary>Janela de topo que hospeda a UI ActiveX de um DTM (flutuante; dono opcional).</summary>
    internal sealed class DtmWindow : Form
    {
        public Panel Body { get; } = new Panel { Dock = DockStyle.Fill };
        public DtmActiveXHost Host { get; set; }
        public event Action UserClosing;
        private bool _force;

        public DtmWindow(string title, bool embedded = false)
        {
            Text = title;
            Width = 1100; Height = 760;
            StartPosition = FormStartPosition.CenterScreen;
            if (embedded)
            {
                // Encaixada na UI: sem moldura e fora da barra de tarefas; a UI posiciona (SetWindowPos assíncrono)
                // e mostra/esconde. Começa fora da tela para não piscar antes do primeiro posicionamento.
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                Location = new Point(-32000, -32000);
            }
            Controls.Add(Body);
            FormClosing += (s, e) =>
            {
                if (_force) return;
                e.Cancel = true;            // fechamento pelo usuário passa pelo protocolo (§6.13.4)
                BeginInvoke(new Action(() => UserClosing?.Invoke()));
            };
        }

        public void SetOwnerHandle(IntPtr owner) { CreateHandle(); SetWindowLong(Handle, -8 /*GWL_HWNDPARENT*/, owner); }
        public void ForceClose() { _force = true; Close(); Dispose(); }

        [DllImport("user32.dll")] private static extern IntPtr SetWindowLong(IntPtr hWnd, int index, IntPtr value);
    }
}
