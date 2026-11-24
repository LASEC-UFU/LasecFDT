using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using LasecFDT.Core;
using LasecFDT.Core.Catalog;
using LasecFDT.Fdt1.Discovery;
using LasecFDT.Fdt1.Frame;
using LasecFDT.Fdt1.Ui;

namespace LasecFDT.TestHost
{
    /// <summary>
    /// Janela mínima de testes: catálogo de DTMs à esquerda, UIs dos DTMs em abas à direita, log de eventos embaixo.
    /// Implementa IFrameUi para atender pedidos do DTM (abrir controle, diálogos).
    /// </summary>
    internal sealed class MainForm : Form, IFrameUi
    {
        private readonly AutoOptions _auto;
        private readonly FrameContainer _frame = new FrameContainer { ProjectName = "LasecFDT-TestHost" };
        private readonly ListView _list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false };
        private readonly TextBox _filter = new TextBox { Dock = DockStyle.Top };
        private readonly TabControl _tabs = new TabControl { Dock = DockStyle.Fill };
        private readonly TextBox _log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, ReadOnly = true, WordWrap = false, Font = new Font("Consolas", 8.5f) };
        private readonly List<OpenDtm> _open = new List<OpenDtm>();
        private List<DtmCatalogEntry> _catalog = new List<DtmCatalogEntry>();
        public int ExitCode { get; private set; }
        public string DepStatus { get; set; }

        private sealed class OpenDtm
        {
            public DtmSession Session;
            public DtmSession Parent;      // DTM de comunicação (pai na cadeia), se houver
            public TopologyLink Link;
            public DtmActiveXHost Host;
            public TabPage Tab;
        }

        public MainForm(AutoOptions auto)
        {
            _auto = auto;
            Text = "LasecFDT — Host de testes FDT 1.2 (x86, offline)";
            Width = 1400; Height = 900;
            StartPosition = FormStartPosition.CenterScreen;
            _frame.Ui = this;
            _frame.EventRaised += e => Log("[evento] " + e);
            DtmSession.Trace = m => Log("[trace] " + m);
            LasecFDT.Fdt1.Comm.CommunicationGuard.Trace = m => Log(m);
            LasecFDT.Fdt1.Comm.HartWireSimulator.Trace = m => Log(m);
            if (auto?.TrapNative == true)
            {
                LasecFDT.Fdt1.Diagnostics.NativeExceptionTrap.OnEntry = e => Log("[nativo] " + e);
                LasecFDT.Fdt1.Diagnostics.NativeExceptionTrap.Install();
            }

            _list.Columns.Add("DTM", 230); _list.Columns.Add("Papéis", 120); _list.Columns.Add("Fabricante", 150); _list.Columns.Add("Versão", 70);
            _filter.TextChanged += (s, e) => FillList();
            _list.DoubleClick += (s, e) => OpenSelected();

            var btnOpen = new Button { Text = "Abrir UI offline", Dock = DockStyle.Top, Height = 30 };
            btnOpen.Click += (s, e) => OpenSelected();
            var btnClose = new Button { Text = "Fechar aba (PrepareToRelease)", Dock = DockStyle.Top, Height = 30 };
            btnClose.Click += (s, e) => { if (_tabs.SelectedTab?.Tag is OpenDtm o) CloseDtm(o); };

            var left = new Panel { Dock = DockStyle.Fill };
            left.Controls.Add(_list); left.Controls.Add(btnClose); left.Controls.Add(btnOpen); left.Controls.Add(_filter);
            left.Controls.Add(new Label { Text = "Filtrar DTMs:", Dock = DockStyle.Top, Height = 18 });

            var rightSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            rightSplit.Panel1.Controls.Add(_tabs);
            rightSplit.Panel2.Controls.Add(_log);
            var mainSplit = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
            mainSplit.Panel1.Controls.Add(left);
            mainSplit.Panel2.Controls.Add(rightSplit);
            Controls.Add(mainSplit);

            Load += (s, e) =>
            {
                // Distâncias aplicadas após o dimensionamento final da janela (DPI).
                mainSplit.SplitterDistance = Math.Min(460, ClientSize.Width / 3);
                rightSplit.SplitterDistance = (int)(rightSplit.Height * 0.72);
                Log("Processo: " + DepStatus);
                LoadCatalog(); if (_auto != null) BeginInvoke(new Action(RunAuto)); };
            FormClosing += (s, e) => { foreach (var o in _open.ToList()) CloseDtm(o); _sim?.Dispose(); };
        }

        private void LoadCatalog()
        {
            _catalog = DtmDiscovery.Scan(readTypeLibs: false).Dtms
                .Where(d => d.RequiredHostArch == (Environment.Is64BitProcess ? "x64" : "x86")).ToList();
            FillList();
            Log($"{_catalog.Count} DTM(s) hospedáveis neste processo ({(Environment.Is64BitProcess ? "x64" : "x86")}).");
        }

        private void FillList()
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            string f = _filter.Text.Trim();
            foreach (var d in _catalog.Where(d => f.Length == 0 || (d.ProgId + d.Name + d.Server?.CompanyName).IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0))
                _list.Items.Add(new ListViewItem(new[] { d.ProgId ?? d.Clsid, string.Join("+", d.Roles), d.Server?.CompanyName, d.Server?.FileVersion }) { Tag = d });
            _list.EndUpdate();
        }

        private void OpenSelected()
        {
            if (_list.SelectedItems.Count == 0) return;
            var d = (DtmCatalogEntry)_list.SelectedItems[0].Tag;
            OpenDtmUi(new Guid(d.Clsid), d.ProgId ?? d.Clsid, null);
        }

        /// <summary>Instancia o DTM (sequência §6.13.1 + Config) e abre a UI ActiveX da função escolhida (§6.13.3).</summary>
        private OpenDtm OpenDtmUi(Guid clsid, string label, string functionId)
        {
            Cursor = Cursors.WaitCursor;
            DtmSession s = null, parent = null;
            TopologyLink link = null;
            try
            {
                s = InitDtm(clsid, label);
                var info = s.Information;
                if (_auto?.SetCommNull == true) s.SetCommunicationNull();

                // Comunicação aninhada (§6.2): canal simulado ou canal de um Comm DTM real (raiz da cadeia).
                string parentSpec = _auto?.Parent;
                if (parentSpec != null)
                {
                    var policy = _auto.Policy;
                    if (parentSpec.Equals("sim", StringComparison.OrdinalIgnoreCase))
                    {
                        var sim = new LasecFDT.Fdt1.Comm.HartSimulatedChannel("SIM-HART", LasecFDT.Fdt1.Comm.HartDevice.FromDeviceType(info.DeviceTypes[0].OuterXml));
                        link = _frame.ConnectToSimulator(s, sim, policy);
                    }
                    else
                    {
                        if (_auto.HartSimPort != null && _sim == null)
                        {
                            var sim = new LasecFDT.Fdt1.Comm.HartWireSimulator(_auto.HartSimPort, LasecFDT.Fdt1.Comm.HartDevice.FromDeviceType(info.DeviceTypes[0].OuterXml));
                            sim.Start();
                            _sim = sim;
                        }
                        parent = _auto.ParentProps.Count > 0
                            ? InitDtmWithProperties(ResolveClsid(parentSpec), parentSpec, _auto.ParentProps)
                            : InitDtm(ResolveClsid(parentSpec), parentSpec);
                        if (!parent.SetCommunicationNull()) throw Fail(parent, "SetCommunication(NULL) no DTM raiz");
                        var ch = parent.GetChannels(LasecFDT.Fdt1.Comm.HartSimulatedChannel.HartProtocolId).FirstOrDefault(c => c.IsCommunicationChannel)
                                 ?? throw new InvalidOperationException("O DTM pai não expõe canal de comunicação.");
                        link = _frame.ConnectToChannel(parent, ch, s, policy) ?? throw Fail(parent, "ValidateAddChild/OnAddChild");
                    }
                    Log($"Ligação: {link.ParentTag} [{link.ChannelPath}] → {link.ChildTag}; SetCommunication aceito={link.CommunicationAccepted}; política={policy}");
                    if (!link.CommunicationAccepted) throw Fail(s, "SetCommunication");
                    // Alguns DTMs conectam sozinhos dentro de SetCommunication; dá tempo à conexão assíncrona antes da UI.
                    bool online = DtmSession.PumpUntil(() => s.Events.Any(e => e.Method == "OnOnlineStateChanged" && e.Detail == "online=True"), TimeSpan.FromSeconds(5));
                    Log($"DTM online antes de abrir a UI: {online}");
                }
                var funcs = s.GetFunctions("notSupported", out var def);

                // Escolha da função: a pedida, a default do DTM, ou a primeira com UI ActiveX.
                var candidates = funcs.Where(f => f.Enabled && !f.Hidden).ToList();
                var ordered = candidates.Where(f => f.FunctionId == functionId)
                    .Concat(candidates.Where(f => f.FunctionId == def))
                    .Concat(candidates.Where(f => f.ApplicationId == "fdtOfflineParameterize" || f.ApplicationId == "fdtConfiguration"))
                    .Concat(candidates).Distinct().ToList();

                var tab = new TabPage(label) { };
                _tabs.TabPages.Add(tab);
                _tabs.SelectedTab = tab;
                foreach (var f in ordered)
                {
                    var host = new DtmActiveXHost(s, FdtXml.FunctionCall(f.FunctionId, f.ApplicationId), _auto?.Order ?? ActiveXInitOrder.EmbedThenInit);
                    if (host.Open(tab))
                    {
                        tab.Text = $"{label} — {f.Name}";
                        var o = new OpenDtm { Session = s, Parent = parent, Link = link, Host = host, Tab = tab };
                        tab.Tag = o;
                        _open.Add(o);
                        Log($"UI aberta: função {f.FunctionId} '{f.Name}' controle {host.ControlClsid}");
                        return o;
                    }
                    Log($"Função {f.FunctionId} '{f.Name}': {host.LastError}");
                }
                _tabs.TabPages.Remove(tab);
                throw new InvalidOperationException("Nenhuma função com UI ActiveX pôde ser aberta.");
            }
            catch (Exception ex)
            {
                Log("ERRO: " + ex.Message);
                LastLink = link;
                s?.Release(TimeSpan.FromSeconds(10));
                parent?.Release(TimeSpan.FromSeconds(10));
                if (_auto == null) MessageBox.Show(this, ex.Message, "LasecFDT", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return null;
            }
            finally { Cursor = Cursors.Default; }
        }

        private TopologyLink LastLink;
        private LasecFDT.Fdt1.Comm.HartWireSimulator _sim;

        private static Exception Fail(DtmSession s, string what) => DtmLifecycle.Fail(s, what);
        private static Guid ResolveClsid(string spec) => DtmLifecycle.ResolveClsid(spec);
        private DtmSession InitDtm(Guid clsid, string label) => DtmLifecycle.CreateConfigured(_frame, clsid, label);
        private DtmSession InitDtmWithProperties(Guid clsid, string label, System.Collections.Generic.Dictionary<string, string> props) =>
            DtmLifecycle.CreateWithProperties(_frame, clsid, props, Log);

        /// <summary>§6.13.4 (controle) seguido de §4.3.1.7 (DTM).</summary>
        private bool CloseDtm(OpenDtm o)
        {
            bool uiClosed = o.Host.Close(TimeSpan.FromSeconds(10));
            bool released = o.Session.Release(TimeSpan.FromSeconds(10)); // desfaz a ligação de comunicação antes
            if (o.Parent != null) released &= o.Parent.Release(TimeSpan.FromSeconds(10));
            if (o.Tab != null && _tabs.TabPages.Contains(o.Tab)) _tabs.TabPages.Remove(o.Tab);
            o.Tab?.Dispose();
            _open.Remove(o);
            Log($"Fechado: OnApplicationClosed={uiClosed} OnPreparedToRelease={released}");
            return uiClosed && released;
        }

        // ================================================================ IFrameUi

        public bool OpenActiveXRequest(string systemTag, string functionCall, bool modal)
        {
            Log($"DTM {systemTag} pediu para abrir controle (modal={modal}) — não suportado nesta versão de testes.");
            return false;
        }

        public bool CloseActiveXRequest(string invokeId)
        {
            var o = _open.FirstOrDefault(x => string.Equals(x.Host.InvokeId, invokeId, StringComparison.OrdinalIgnoreCase));
            if (o == null) return false;
            BeginInvoke(new Action(() => CloseDtm(o))); // fecha fora da chamada do DTM
            return true;
        }

        public string UserDialog(string systemTag, string userMessage)
        {
            if (_auto != null) return null; // modo automático: resposta padrão do Frame
            try
            {
                var doc = FdtXml.Load(userMessage);
                var msg = doc.GetElementsByTagName("*").OfType<System.Xml.XmlElement>().First(e => e.LocalName == "FDTUserMessage");
                string text = string.Join(Environment.NewLine, doc.GetElementsByTagName("*").OfType<System.Xml.XmlElement>()
                    .Where(e => e.LocalName == "TextLine").Select(e => e.Attributes.OfType<System.Xml.XmlAttribute>().FirstOrDefault(a => a.LocalName == "string")?.Value));
                var buttons = MapButtons(msg.GetAttribute("messageButtons"));
                var r = MessageBox.Show(this, text, msg.GetAttribute("title"), buttons);
                return FrameContainer.DefaultDialogAnswer(userMessage, "ok", "button" + r);
            }
            catch { return null; }
        }

        private static MessageBoxButtons MapButtons(string b)
        {
            switch (b)
            {
                case "buttonsAbortRetryIgnore": return MessageBoxButtons.AbortRetryIgnore;
                case "buttonsOkCancel": return MessageBoxButtons.OKCancel;
                case "buttonsRetryCancel": return MessageBoxButtons.RetryCancel;
                case "buttonsYesNo": return MessageBoxButtons.YesNo;
                case "buttonsYesNoCancel": return MessageBoxButtons.YesNoCancel;
                default: return MessageBoxButtons.OK;
            }
        }

        // ================================================================ modo automático

        private void RunAuto()
        {
            var result = new Dictionary<string, object> { ["dtm"] = _auto.Dtm };
            try
            {
                Guid clsid = ResolveClsid(_auto.Dtm);
                WindowState = FormWindowState.Normal; Activate(); BringToFront();
                var o = OpenDtmUi(clsid, _auto.Dtm, _auto.FunctionId);
                result["uiOpened"] = o != null;
                result["order"] = _auto.Order.ToString();
                result["dep"] = DepStatus;
                result["parent"] = _auto.Parent;
                result["policy"] = _auto.Policy.ToString();
                var lk = o?.Link ?? LastLink;
                if (lk != null)
                {
                    result["link"] = new { lk.ParentTag, lk.ChannelPath, lk.ChildTag, lk.CommunicationAccepted };
                    result["commTraffic"] = lk.Guard?.Log.Select(r => r.ToString()).ToList();
                }
                if (_auto.TrapNative) result["nativeExceptions"] = LasecFDT.Fdt1.Diagnostics.NativeExceptionTrap.Entries.Select(x => x.ToString()).ToList();
                if (_sim != null) result["hartSimulator"] = new { _sim.PortName, _sim.FramesReceived, _sim.FramesAnswered, commands = _sim.Device.Log };
                if (o != null)
                {
                    result["controlClsid"] = o.Host.ControlClsid;
                    result["functionCall"] = o.Host.FunctionCall;
                    DtmSession.PumpUntil(() => false, TimeSpan.FromSeconds(_auto.HoldSeconds)); // deixa a UI renderizar
                    var area = o.Host.Control.RectangleToScreen(o.Host.Control.ClientRectangle);
                    result["controlSize"] = $"{area.Width}x{area.Height}";
                    result["distinctColorsInControl"] = CaptureDistinctColors(area, _auto.Screenshot);
                    result["commTraffic"] = o.Link?.Guard?.Log.Select(r => r.ToString()).ToList();
                    result["closedCleanly"] = CloseDtm(o);
                }
                ExitCode = o != null && (bool)result["closedCleanly"] ? 0 : 5;
            }
            catch (Exception ex) { result["error"] = ex.ToString(); ExitCode = 6; }
            result["log"] = _log.Lines;
            try { if (_auto.Report != null) File.WriteAllText(_auto.Report, Json.Serialize(result)); }
            catch (Exception ex) { Console.Error.WriteLine("Falha ao gravar relatório: " + ex.Message); ExitCode = 8; }
            Log("[saída] relatório gravado; limpeza COM na thread STA");
            LasecFDT.Fdt1.Ui.LegacyActiveXProcess.CleanupComOnStaThread();
            DtmSession.PumpUntil(() => false, TimeSpan.FromSeconds(1));
            Log("[saída] Close()");
            Close();
        }

        /// <summary>Captura a tela da janela; retorna o nº de cores distintas na área do controle (≈1 = área vazia).</summary>
        private int CaptureDistinctColors(Rectangle controlArea, string path)
        {
            var win = RectangleToScreen(ClientRectangle);
            using (var bmp = new Bitmap(win.Width, win.Height))
            {
                using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(win.Location, Point.Empty, win.Size);
                if (path != null) bmp.Save(path, ImageFormat.Png);
                var colors = new HashSet<int>();
                var rel = new Rectangle(controlArea.X - win.X, controlArea.Y - win.Y, controlArea.Width, controlArea.Height);
                rel.Intersect(new Rectangle(Point.Empty, bmp.Size));
                for (int y = rel.Top; y < rel.Bottom; y += 3)
                    for (int x = rel.Left; x < rel.Right; x += 3)
                        colors.Add(bmp.GetPixel(x, y).ToArgb());
                return colors.Count;
            }
        }

        private void Log(string m)
        {
            // Log contínuo em arquivo (modo automático): sobrevive a travamento/encerramento forçado do processo.
            if (_auto?.Report != null)
                lock (_auto) try { File.AppendAllText(_auto.Report + ".live.log", $"{DateTime.Now:HH:mm:ss.fff} [t{System.Threading.Thread.CurrentThread.ManagedThreadId}] {m}{Environment.NewLine}"); } catch { }
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(() => AppendLog(m))); return; }
            AppendLog(m);
        }

        private void AppendLog(string m)
        {
            if (IsDisposed) return;
            _log.AppendText($"{DateTime.Now:HH:mm:ss.fff} {m}{Environment.NewLine}");
        }
    }
}
