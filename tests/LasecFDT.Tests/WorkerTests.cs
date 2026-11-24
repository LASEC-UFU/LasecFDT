using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LasecFDT.Core.Rpc;
using LasecFDT.Fdt1.Comm;
using LasecFDT.Supervisor;
using Xunit;

namespace LasecFDT.Tests
{
    /// <summary>
    /// Worker FDT 1.x (um processo por instância) + supervisor, com DTMs reais. Comunicação somente contra
    /// instrumento simulado (par virtual com0com).
    /// </summary>
    /// <summary>Janelas de outro processo (para fechar diálogos próprios de DTMs nos testes).</summary>
    internal static class Win
    {
        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [System.Runtime.InteropServices.DllImport("user32")] private static extern bool EnumWindows(EnumProc p, IntPtr l);
        [System.Runtime.InteropServices.DllImport("user32")] private static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
        [System.Runtime.InteropServices.DllImport("user32")] private static extern bool IsWindowVisible(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
        [System.Runtime.InteropServices.DllImport("user32")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

        public static IntPtr FindVisibleTopLevel(int pid)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out int p);
                if (p == pid && IsWindowVisible(h) && Title(h).Length > 0) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        public static string Title(IntPtr h) { var sb = new System.Text.StringBuilder(256); GetWindowText(h, sb, 256); return sb.ToString(); }
        public static void Close(IntPtr h) => PostMessage(h, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero);
    }

    public class WorkerTests
    {
        internal static string WorkerExe
        {
            get
            {
                var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                string config = dir.FullName.Contains(@"\Release\") ? "Release" : "Debug";
                while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LasecFDT.sln"))) dir = dir.Parent;
                return Path.Combine(dir.FullName, "src", "LasecFDT.Worker.Fdt1", "bin", config, "net48", "LasecFDT.Worker.Fdt1.exe");
            }
        }

        private static Supervisor.Supervisor NewSupervisor(ConcurrentQueue<SupervisorEvent> events = null)
        {
            var s = new Supervisor.Supervisor(WorkerExe, new SupervisorOptions
            {
                CallTimeout = TimeSpan.FromSeconds(60),
                LogDirectory = Path.Combine(Path.GetTempPath(), "LasecFDT-test-logs"),
            });
            if (events != null) s.EventRaised += e => events.Enqueue(e);
            return s;
        }

        [Fact]
        public void Worker_ExecutavelSemNxCompat()
        {
            var b = File.ReadAllBytes(WorkerExe);
            int pe = BitConverter.ToInt32(b, 0x3C);
            ushort dc = BitConverter.ToUInt16(b, pe + 24 + 70);
            Assert.Equal(0, dc & 0x0100); // NXCOMPAT removido (emulação de thunks ATL)
        }

        [DtmFact("SmarLD301DTM.DTMCore.1")]
        public async Task Worker_ProcessoSeparadoComDepEEmulacaoDeThunks_CicloCompletoEPersistenciaEntreWorkers()
        {
            using (var sup = NewSupervisor())
            {
                var a = await sup.CreateAsync(new CreateDtmParams { Dtm = "SmarLD301DTM.DTMCore.1" });
                Assert.NotEqual(Process.GetCurrentProcess().Id, a.Worker.ProcessId);
                Assert.Equal("x86", a.Worker.Info.Architecture);
                Assert.Contains("DEP=ativo", a.Worker.Info.Dep);
                Assert.Contains("emulação de thunks ATL=ativa", a.Worker.Info.Dep);
                Assert.Equal("Configured", a.Summary.State);
                Assert.NotEmpty(await sup.GetFunctionsAsync(a));
                var ds = await sup.SaveAsync(a);
                Assert.Equal("StreamInit", ds.Kind);

                // Recarga em OUTRO worker (outro processo) e comparação do data set
                var b = await sup.LoadAsync(new LoadParams { Dtm = "SmarLD301DTM.DTMCore.1", DataSet = ds });
                Assert.NotEqual(a.Worker.ProcessId, b.Worker.ProcessId);
                var ds2 = await sup.SaveAsync(b);
                Assert.Equal(ds.StreamBase64, ds2.StreamBase64);

                int pa = a.Worker.ProcessId;
                Assert.True(await sup.ReleaseAsync(a));
                Assert.True(await sup.ReleaseAsync(b));
                Assert.True(a.Worker.HasExited);
                Assert.False(a.Worker.Crashed);
                Assert.Equal(0, a.Worker.Process.ExitCode);
            }
        }

        [DtmFact("Smar.TT301DTM.1")]
        public async Task Worker_UiDaFamiliaSmarDtm1_AbreRenderizaEFechaPeloProtocolo()
        {
            using (var sup = NewSupervisor())
            {
                var i = await sup.CreateAsync(new CreateDtmParams { Dtm = "Smar.TT301DTM.1" });
                var ui = await sup.OpenUiAsync(i);
                Assert.True(ui.Opened, ui.Error);
                Assert.Equal("D7E09017-9DC7-4989-901C-3C99BC91976E", ui.ControlClsid.Trim('{', '}').ToUpperInvariant());
                Assert.True(await sup.ScreenshotAsync(i, null) > 100, "a área do controle está vazia");
                Assert.True(await sup.CloseUiAsync(i));   // PrepareToRelease → OnApplicationClosed
                Assert.True(await sup.ReleaseAsync(i));
                Assert.Equal(0, i.Worker.Process.ExitCode); // sem a falha de descarga das DLLs legadas
            }
        }

        [DtmFact("CWHartFdt.clsDTM")]
        public async Task Worker_UiPorProgId_ConfiguracaoDoCommDtmCodeWrights()
        {
            using (var sup = NewSupervisor())
            {
                var i = await sup.CreateAsync(new CreateDtmParams { Dtm = "CWHartFdt.clsDTM" });
                var config = (await sup.GetFunctionsAsync(i)).Single(f => f.ApplicationId == "fdtConfiguration");
                // GetActiveXGuid vem vazio; GetActiveXProgId devolve "CWHartFdt.ctrlConfiguration" (§4.3.3.2)
                var ui = await sup.OpenUiAsync(i, new OpenUiParams { FunctionId = config.FunctionId });
                Assert.True(ui.Opened, ui.Error);
                Assert.Equal("7183F1C4-48D2-44A1-9DF5-24CC6626741B", ui.ControlClsid);
                Assert.True(await sup.ScreenshotAsync(i, null) > 20, "a área do controle está vazia");
                Assert.True(await sup.CloseUiAsync(i));
                Assert.True(await sup.ReleaseAsync(i));
                Assert.Equal(0, i.Worker.Process.ExitCode);
            }
        }

        [DtmFact("CWHartFdt.clsDTM")]
        public async Task Worker_InvokeFunctionRequest_ResultadoDoDtmChegaComoEvento()
        {
            var events = new ConcurrentQueue<SupervisorEvent>();
            using (var sup = NewSupervisor(events))
            {
                var i = await sup.CreateAsync(new CreateDtmParams { Dtm = "CWHartFdt.clsDTM" });
                var r = await sup.InvokeFunctionAsync(i, "100");   // mnAbout
                Assert.True(r.Started);
                // A CodeWrights recusa (FALSE): suas funções têm controle ActiveX. O Frame registra e avisa por evento.
                Assert.True(await WaitUntil(() => events.Any(e => e.WorkerEvent?.InvokeId == r.InvokeId &&
                    (e.WorkerEvent.Method == "functionRefused" || e.WorkerEvent.Method == "functionStarted")), TimeSpan.FromSeconds(15)));
                Assert.NotNull(await sup.SaveAsync(i));             // o worker continua atendendo
                Assert.True(await sup.ReleaseAsync(i));
            }
        }

        [DtmFact("SmarLD301DTM.DTMCore.1")]
        public async Task Supervisor_QuedaDoWorker_IsoladaEDetectada()
        {
            var events = new ConcurrentQueue<SupervisorEvent>();
            using (var sup = NewSupervisor(events))
            {
                var victim = await sup.CreateAsync(new CreateDtmParams { Dtm = "SmarLD301DTM.DTMCore.1" });
                var other = await sup.CreateAsync(new CreateDtmParams { Dtm = "SmarLD301DTM.DTMCore.1" });
                victim.Worker.Kill(); // simula um DTM derrubando o processo
                Assert.True(await victim.Worker.WaitForExitAsync(TimeSpan.FromSeconds(10)));
                await Task.Delay(300);
                Assert.True(victim.Worker.Crashed);
                Assert.Contains(events, e => e.Kind == "died" && e.Instance == victim);
                var ex = await Assert.ThrowsAsync<RpcException>(() => sup.SaveAsync(victim));
                Assert.Equal(RpcException.WorkerDied, ex.Code);
                // o outro worker continua íntegro
                Assert.NotNull(await sup.SaveAsync(other));
                Assert.True(await sup.ReleaseAsync(other));
            }
        }

        [CommChainFact("Smar.TT301DTM.1", "CWHartFdt.clsDTM")]
        public async Task CadeiaEntreProcessos_CodeWrightsNumWorker_TT301EmOutro_ProxyComDoCanal()
        {
            using (var wire = new HartWireSimulator(VirtualPortPairFactAttribute.Slave, new HartDevice { ManufacturerId = 62, DeviceTypeId = 2 }))
            using (var sup = NewSupervisor())
            {
                wire.Start();
                var parent = await sup.CreateAsync(new CreateDtmParams { Dtm = "CWHartFdt.clsDTM", Properties = new Dictionary<string, string> { ["Port"] = VirtualPortPairFactAttribute.Master } });
                var child = await sup.CreateAsync(new CreateDtmParams { Dtm = "Smar.TT301DTM.1" });
                Assert.NotEqual(parent.Worker.ProcessId, child.Worker.ProcessId);

                var link = await sup.ConnectAsync(parent, child, "ReadOnly");
                Assert.StartsWith(parent.SystemTag + "/", link.ChannelPath);
                Assert.True(await sup.WaitOnlineAsync(child), "TT301 não ficou online pelo canal remoto. " + await Diag(sup, child, wire));
                Assert.True(wire.FramesAnswered >= 1);

                var log = await sup.GetCommLogAsync(child);
                Assert.Contains(log, r => r.Method == "ConnectRequest" && r.Forwarded);
                Assert.Contains(log, r => r.Method.StartsWith("OnConnectResponse") && r.Direction == "canal→DTM"); // callback voltou entre processos

                // UI do filho com canal remoto
                var ui = await sup.OpenUiAsync(child);
                Assert.True(ui.Opened, ui.Error);
                Assert.True(await sup.CloseUiAsync(child));

                Assert.True(await sup.DisconnectAsync(link));
                Assert.True(await sup.ReleaseAsync(child));
                Assert.True(await sup.ReleaseAsync(parent));
            }
            Assert.True(await WaitNoProcess("CWFdtHARTPort", TimeSpan.FromSeconds(10)), "servidor de porta da CodeWrights ficou rodando");
        }

        [CommChainFact("Smar.TT301DTM.1", "CWHartFdt.clsDTM")]
        public async Task CadeiaEntreProcessos_QuedaDoWorkerDoCommDtm_FilhoSobreviveEPortaELiberada()
        {
            var events = new ConcurrentQueue<SupervisorEvent>();
            using (var wire = new HartWireSimulator(VirtualPortPairFactAttribute.Slave, new HartDevice { ManufacturerId = 62, DeviceTypeId = 2 }))
            using (var sup = NewSupervisor(events))
            {
                wire.Start();
                var parent = await sup.CreateAsync(new CreateDtmParams { Dtm = "CWHartFdt.clsDTM", Properties = new Dictionary<string, string> { ["Port"] = VirtualPortPairFactAttribute.Master } });
                var child = await sup.CreateAsync(new CreateDtmParams { Dtm = "Smar.TT301DTM.1" });
                await sup.ConnectAsync(parent, child, "ReadOnly");
                Assert.True(await sup.WaitOnlineAsync(child), await Diag(sup, child, wire));

                parent.Worker.Kill(); // o worker do Comm DTM cai com a porta aberta
                Assert.True(await parent.Worker.WaitForExitAsync(TimeSpan.FromSeconds(10)));
                Assert.True(await WaitUntil(() => events.Any(e => e.Kind == "linkBroken"), TimeSpan.FromSeconds(10)));
                Assert.True(await WaitUntil(() => sup.Links.Count == 0, TimeSpan.FromSeconds(30)), "o supervisor não desfez a ligação");
                // o servidor de porta não pode ficar segurando a COM
                Assert.True(await WaitNoProcess("CWFdtHARTPort", TimeSpan.FromSeconds(15)), "servidor de porta órfão não foi limpo");

                // o filho continua vivo, voltou a "configured" e libera normalmente
                Assert.True(child.Alive);
                Assert.NotNull(await sup.SaveAsync(child));
                Assert.True(await sup.ReleaseAsync(child));
                await sup.ReleaseAsync(parent);
            }
        }

        private static async Task<string> Diag(Supervisor.Supervisor sup, DtmInstance child, HartWireSimulator wire)
        {
            string comm;
            try { comm = string.Join(" || ", (await sup.GetCommLogAsync(child)).Select(r => $"{r.Direction} {r.Method} fwd={r.Forwarded} {r.Note}")); }
            catch (Exception ex) { comm = "(log indisponível: " + ex.Message + ")"; }
            return $"sim: recebidos={wire.FramesReceived} respondidos={wire.FramesAnswered} erros=[{string.Join("; ", wire.Errors)}] cmds=[{string.Join("; ", wire.Device.Log)}] | comm: {comm}";
        }

        private static async Task<bool> WaitUntil(Func<bool> cond, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (!cond()) { if (sw.Elapsed > timeout) return false; await Task.Delay(100); }
            return true;
        }

        private static Task<bool> WaitNoProcess(string name, TimeSpan timeout) => WaitUntil(() => Process.GetProcessesByName(name).Length == 0, timeout);
    }
}
