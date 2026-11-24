using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Jigfdt.Fdt100;
using LasecFDT.Fdt1.Frame;

namespace LasecFDT.Fdt1.Ui
{
    /// <summary>
    /// AxHost genérico. Pode criar o controle pelo CLSID (caminho normal) ou adotar uma instância
    /// já criada (para a estratégia InitBeforeEmbed).
    /// </summary>
    internal sealed class GenericAxHost : AxHost
    {
        private readonly object _preCreated;
        public GenericAxHost(string clsid, object preCreated = null) : base(clsid) { _preCreated = preCreated; }
        protected override object CreateInstanceCore(Guid clsid) => _preCreated ?? base.CreateInstanceCore(clsid);

        /// <summary>
        /// Leva o controle até o estado OLE "running" (instância criada, IOleObject::SetClientSite e
        /// IPersistStreamInit::InitNew feitos) sem ativação in-place. Usa o método interno
        /// AxHost.TransitionUpTo(OC_RUNNING=2) do .NET Framework (detalhe de implementação do WinForms).
        /// </summary>
        public void TransitionToRunning()
        {
            var m = typeof(AxHost).GetMethod("TransitionUpTo", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (m == null) throw new NotSupportedException("AxHost.TransitionUpTo não encontrado nesta versão do WinForms.");
            m.Invoke(this, new object[] { 2 });
        }
    }

    /// <summary>Ordem de conexão entre o presentation object e o DTM.</summary>
    public enum ActiveXInitOrder
    {
        /// <summary>
        /// CoCreateInstance → embedding/ativação in-place → Init. Ordem desenhada em §6.13.3 e PADRÃO: validada em
        /// 22 DTMs. Controles ATL 3.0 (ex.: Smar.*DTM.1) só criam o controle interno (VB6) na ativação, então um
        /// Init antes da ativação falha com _com_error E_POINTER. Exige host sem NXCOMPAT (LegacyActiveXProcess).
        /// </summary>
        EmbedThenInit,
        /// <summary>CoCreateInstance → IPersistStreamInit::InitNew (do controle) → Init → embedding/ativação.</summary>
        InitBeforeEmbed,
        /// <summary>CoCreateInstance → SetClientSite + InitNew (estado OLE "running") → Init → ativação in-place.</summary>
        SiteThenInit,
    }

    /// <summary>
    /// Presentation object de um DTM (FDT 1.2.1 §2.10 "Presentation", §4.4) embutido num Control WinForms.
    ///
    /// Abertura (§6.13.3):  IDtmActiveXInformation::GetActiveXGuid(functionCall) → CoCreateInstance + embedding
    ///                      → IDtmActiveXControl::Init(invokeId, functionCall, IDtm)
    /// Fechamento (§6.13.4): IDtmActiveXControl::PrepareToRelease → IDtmEvents::OnApplicationClosed(invokeId) → Release
    ///
    /// Afinidade de thread: deve ser criado e destruído na thread STA de UI que também é dona do DTM.
    /// </summary>
    public sealed class DtmActiveXHost : IDisposable
    {
        private GenericAxHost _ax;
        /// <summary>Controle .NET (servidor COM em mscoree.dll) hospedado diretamente, sem AxHost.</summary>
        private Control _managed;
        public DtmSession Session { get; }
        public string InvokeId { get; } = Guid.NewGuid().ToString("D").ToUpperInvariant();
        public string FunctionCall { get; }
        public ActiveXInitOrder Order { get; }
        public string ControlClsid { get; private set; }
        public string ControlProgId { get; private set; }
        public Control Control => (Control)_ax ?? _managed;
        public bool Initialized { get; private set; }
        public bool Closed { get; private set; }
        public string LastError { get; private set; }

        public DtmActiveXHost(DtmSession session, string functionCall, ActiveXInitOrder order = ActiveXInitOrder.EmbedThenInit)
        {
            Session = session;
            FunctionCall = functionCall;
            Order = order;
        }

        /// <summary>Canal cuja UI está hospedada (null = UI do próprio DTM).</summary>
        public DtmSession.ChannelRef Channel { get; private set; }

        /// <summary>
        /// UI de um canal (§4.5.2, §4.6, §6.24): GUID por IFdtChannelActiveXInformation::GetChannelActiveXGuid;
        /// controle iniciado por IFdtChannelActiveXControl2::Init2 (ou ::Init, se o controle não tiver a versão 2);
        /// fechamento por IFdtChannelActiveXControl::PrepareToRelease → IDtmEvents::OnApplicationClosed.
        /// </summary>
        public static DtmActiveXHost ForChannel(DtmSession owner, DtmSession.ChannelRef channel, string functionCall) =>
            new DtmActiveXHost(owner, functionCall) { Channel = channel };

        /// <summary>Cria o controle dentro de <paramref name="parent"/> e o conecta ao DTM. Retorna false se o DTM não fornece UI ActiveX.</summary>
        public bool Open(Control parent)
        {
            if (Channel != null)
            {
                try { ControlClsid = ((IFdtChannelActiveXInformation)Channel.Channel).GetChannelActiveXGuid(FunctionCall); }
                catch (Exception ex) { LastError = "GetChannelActiveXGuid lançou " + ex.Message; return false; }
                if (string.IsNullOrWhiteSpace(ControlClsid)) { ControlClsid = null; LastError = "GetChannelActiveXGuid retornou vazio: função de canal sem UI."; return false; }
            }
            else
            {
                ControlClsid = Session.GetActiveXGuid(FunctionCall);
                if (ControlClsid == null)
                {
                    // §4.3.3.2: o controle também pode ser identificado pelo ProgID. Alguns DTMs (ex.: CodeWrights
                    // HART, VB6) devolvem GUID vazio e só o ProgID.
                    string progId = Session.GetActiveXProgId(FunctionCall);
                    if (!string.IsNullOrWhiteSpace(progId))
                    {
                        var t = Type.GetTypeFromProgID(progId.Trim(), false);
                        if (t != null) { ControlClsid = Core.FdtIds.ToFdtUuid(t.GUID); ControlProgId = progId.Trim(); }
                        else LastError = $"GetActiveXProgId devolveu '{progId}', que não está registrado.";
                    }
                }
            }
            if (ControlClsid == null) { LastError = LastError ?? "GetActiveXGuid e GetActiveXProgId vazios: função sem UI ActiveX."; return false; }
            Session.TrackInvokeId(InvokeId);
            var sw = Stopwatch.StartNew();
            DtmSession.Trace?.Invoke($"ActiveX {ControlClsid} ordem={Order}: criando");

            object instance = null;
            if (IsManagedServer(ControlClsid))
                return OpenManaged(parent, sw);
            if (Order == ActiveXInitOrder.InitBeforeEmbed)
            {
                try
                {
                    instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid(ControlClsid.Trim('{', '}')), true));
                    // Contrato OLE: inicializar o estado do próprio controle antes de qualquer outro uso.
                    if (instance is Com.IPersistStreamInit psi) psi.InitNew();
                }
                catch (Exception ex) { LastError = "CoCreateInstance/InitNew do controle falhou: " + ex.Message; return false; }
                if (!InitControl(instance)) { if (Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance); return false; }
            }

            try
            {
                _ax = new GenericAxHost(ControlClsid.Trim('{', '}'), instance) { Dock = DockStyle.Fill };
                ((System.ComponentModel.ISupportInitialize)_ax).BeginInit();
                if (Order == ActiveXInitOrder.SiteThenInit)
                {
                    _ax.Visible = false;
                    parent.Controls.Add(_ax);
                    ((System.ComponentModel.ISupportInitialize)_ax).EndInit();
                    _ax.TransitionToRunning();           // site definido, sem ativação in-place
                    if (!InitControl(_ax.GetOcx())) { DisposeAx(); return false; }
                    _ax.Visible = true;
                    _ax.CreateControl();                 // ativação in-place após o Init
                }
                else
                {
                    parent.Controls.Add(_ax);   // site OLE + ativação in-place
                    ((System.ComponentModel.ISupportInitialize)_ax).EndInit();
                    _ax.CreateControl();
                }
            }
            catch (Exception ex)
            {
                LastError = "Falha ao instanciar/embutir o controle " + ControlClsid + ": " + ex.Message;
                DisposeAx();
                return false;
            }

            if (Order == ActiveXInitOrder.EmbedThenInit && !InitControl(_ax.GetOcx())) { DisposeAx(); return false; }
            DtmSession.Trace?.Invoke($"ActiveX {ControlClsid} invoke={InvokeId} Init={Initialized} ({sw.ElapsedMilliseconds} ms)");
            return Initialized;
        }

        /// <summary>
        /// Controle ActiveX escrito em .NET (InprocServer32 = mscoree.dll). Neste processo o CLR entrega o próprio
        /// UserControl, não um RCW; o AxHost não serve (ao liberar, chama ReleaseComObject nele). O controle já é
        /// WinForms: entra direto na janela, e o protocolo FDT (Init/PrepareToRelease) é o mesmo.
        /// </summary>
        private bool OpenManaged(Control parent, Stopwatch sw)
        {
            object instance;
            try { instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid(ControlClsid.Trim('{', '}')), true)); }
            catch (Exception ex) { LastError = "CoCreateInstance do controle .NET falhou: " + ex.Message; return false; }
            if (!(instance is Control c)) { LastError = "Servidor .NET não é um controle WinForms: " + instance.GetType().FullName; return false; }
            _managed = c;
            c.Dock = DockStyle.Fill;
            parent.Controls.Add(c);
            c.CreateControl();
            if (!InitControl(c)) { DisposeAx(); return false; }
            DtmSession.Trace?.Invoke($"ActiveX .NET {ControlClsid} invoke={InvokeId} Init={Initialized} ({sw.ElapsedMilliseconds} ms)");
            return Initialized;
        }

        private static bool IsManagedServer(string clsid)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"CLSID\{" + clsid.Trim('{', '}') + @"}\InprocServer32"))
                    return (k?.GetValue(null) as string)?.EndsWith("mscoree.dll", StringComparison.OrdinalIgnoreCase) == true;
            }
            catch { return false; }
        }

        private bool InitControl(object ocx)
        {
            if (Channel != null) return InitChannelControl(ocx);
            if (!(ocx is IDtmActiveXControl ctl))
            {
                LastError = "O controle não implementa IDtmActiveXControl (obrigatório, §4.10.1).";
                return false;
            }
            try
            {
                Initialized = ctl.Init(InvokeId, FunctionCall, Session.Dtm);
                if (!Initialized) LastError = "IDtmActiveXControl::Init retornou FALSE"; // §4.4.1.1: Frame libera o controle
            }
            catch (Exception ex) { LastError = "IDtmActiveXControl::Init lançou " + ex.Message; }
            return Initialized;
        }

        private bool InitChannelControl(object ocx)
        {
            try
            {
                if (ocx is IFdtChannelActiveXControl2 c2) Initialized = c2.Init2(InvokeId, FunctionCall, Channel.Channel);       // §6.24.1
                else if (ocx is IFdtChannelActiveXControl c1) Initialized = c1.Init(InvokeId, Channel.Channel);                 // §6.24.2
                else { LastError = "O controle não implementa IFdtChannelActiveXControl(2)."; return false; }
                if (!Initialized) LastError = "IFdtChannelActiveXControl::Init retornou FALSE";
            }
            catch (Exception ex) { LastError = "IFdtChannelActiveXControl::Init lançou " + ex.Message; }
            return Initialized;
        }

        /// <summary>
        /// §6.13.4. Retorna true se o DTM confirmou com OnApplicationClosed dentro do timeout.
        /// Se o controle recusar (FALSE), mantém aberto, como determina §4.4.1.2.
        /// </summary>
        public bool Close(TimeSpan timeout)
        {
            if (Closed || (_ax == null && _managed == null)) return true;
            bool accepted;
            try
            {
                object ocx = _managed ?? _ax.GetOcx();
                accepted = Channel != null ? ((IFdtChannelActiveXControl)ocx).PrepareToRelease() : ((IDtmActiveXControl)ocx).PrepareToRelease();
            }
            catch (Exception ex) { LastError = "PrepareToRelease lançou " + ex.Message; accepted = true; }
            if (!accepted) { LastError = "O controle recusou PrepareToRelease (mantido aberto)."; return false; }

            bool notified = DtmSession.PumpUntil(() => Session.Events.Exists(e => e.Method == "OnApplicationClosed" && string.Equals(e.InvokeId, InvokeId, StringComparison.OrdinalIgnoreCase)), timeout);
            DtmSession.Trace?.Invoke($"ActiveX invoke={InvokeId} PrepareToRelease aceito; OnApplicationClosed={(notified ? "recebido" : "NÃO recebido")}");
            DisposeAx();
            Closed = true;
            return notified;
        }

        private void DisposeAx()
        {
            if (_managed != null)
            {
                _managed.Parent?.Controls.Remove(_managed);
                _managed.Dispose();
                _managed = null;
            }
            if (_ax == null) return;
            _ax.Parent?.Controls.Remove(_ax);
            _ax.Dispose(); // libera a referência OLE ao controle
            _ax = null;
        }

        public void Dispose() { if (!Closed) Close(TimeSpan.FromSeconds(5)); }
    }
}
