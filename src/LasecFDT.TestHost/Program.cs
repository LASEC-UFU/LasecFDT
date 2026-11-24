using System;
using System.Windows.Forms;

namespace LasecFDT.TestHost
{
    internal static class Program
    {
        /// <summary>
        /// Uso interativo: LasecFDT.TestHost.exe
        /// Uso automatizado (validação): LasecFDT.TestHost.exe --auto &lt;progid|clsid&gt; [--function ID] [--hold s] [--screenshot arq.png] [--report arq.json]
        /// </summary>
        [STAThread] // thread de UI = apartamento STA dono dos DTMs e dos controles ActiveX
        private static int Main(string[] args)
        {
            // Controles ATL 3.0/VB6 precisam de emulação de thunks ATL (executável sem NXCOMPAT + DEP religado).
            string dep = LasecFDT.Fdt1.Ui.LegacyActiveXProcess.EnableDepWithAtlThunkEmulation();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var auto = AutoOptions.Parse(args);
            if (auto != null)
            {
                // Modo automático: nunca exibir diálogos modais de erro (bloqueariam a execução sem usuário).
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    Console.Error.WriteLine("Exceção sem tratamento: " + e.ExceptionObject);
                    Environment.Exit(7);
                };
            }
            var form = new MainForm(auto) { DepStatus = dep };
            Application.Run(form);
            if (auto?.Report != null) System.IO.File.AppendAllText(auto.Report + ".live.log", "[saída] Application.Run terminou; código " + form.ExitCode + Environment.NewLine);
            // DLLs legadas (ex.: Smar.*DTM.1, ATL 3.0 + VB6) falham na própria descarga (DLL_PROCESS_DETACH):
            // medido — ExitProcess/OleUninitialize → 0xC0000005, TerminateProcess → 0. Todo o estado já foi
            // liberado pelo protocolo FDT e gravado, então o host encerra sem executar a descarga das DLLs.
            LasecFDT.Fdt1.Ui.LegacyActiveXProcess.TerminateNow(form.ExitCode);
            return form.ExitCode;
        }
    }

    internal sealed class AutoOptions
    {
        public string Dtm;
        public string FunctionId;
        public double HoldSeconds = 4;
        public string Screenshot;
        public string Report;
        public bool SetCommNull;
        /// <summary>--parent sim | progid/clsid do Comm DTM: liga o DTM a um canal de comunicação antes de abrir a UI.</summary>
        public string Parent;
        /// <summary>--parent-prop Nome=Valor (repetível): propriedades do data set PropertyBag do DTM pai (ex.: Port=COM20).</summary>
        public System.Collections.Generic.Dictionary<string, string> ParentProps = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>--hart-sim PORTA: inicia um instrumento HART simulado na porta (ponta oposta de um par virtual com0com).</summary>
        public string HartSimPort;
        /// <summary>--trap-native: registra exceções nativas de primeira chance (diagnóstico de falhas em controles ActiveX).</summary>
        public bool TrapNative;
        /// <summary>--policy blocked (padrão) | readonly | full. "full" pode escrever no instrumento: só com autorização.</summary>
        public LasecFDT.Fdt1.Comm.CommPolicy Policy = LasecFDT.Fdt1.Comm.CommPolicy.Blocked;
        public LasecFDT.Fdt1.Ui.ActiveXInitOrder Order = LasecFDT.Fdt1.Ui.ActiveXInitOrder.EmbedThenInit;

        public static AutoOptions Parse(string[] a)
        {
            int i = Array.IndexOf(a, "--auto");
            if (i < 0 || i + 1 >= a.Length) return null;
            string Opt(string n) { int k = Array.IndexOf(a, n); return k >= 0 && k + 1 < a.Length ? a[k + 1] : null; }
            var o = new AutoOptions
            {
                Dtm = a[i + 1],
                FunctionId = Opt("--function"),
                HoldSeconds = double.TryParse(Opt("--hold"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var h) ? h : 4,
                Screenshot = Opt("--screenshot"),
                Report = Opt("--report"),
                SetCommNull = Array.IndexOf(a, "--setcomm-null") >= 0,
                Parent = Opt("--parent"),
                HartSimPort = Opt("--hart-sim"),
                TrapNative = Array.IndexOf(a, "--trap-native") >= 0,
                Policy = Opt("--policy") == "full" ? LasecFDT.Fdt1.Comm.CommPolicy.Full : Opt("--policy") == "readonly" ? LasecFDT.Fdt1.Comm.CommPolicy.ReadOnly : LasecFDT.Fdt1.Comm.CommPolicy.Blocked,
                Order = Opt("--order") == "init" ? LasecFDT.Fdt1.Ui.ActiveXInitOrder.InitBeforeEmbed : Opt("--order") == "site" ? LasecFDT.Fdt1.Ui.ActiveXInitOrder.SiteThenInit : LasecFDT.Fdt1.Ui.ActiveXInitOrder.EmbedThenInit,
            };
            for (int k = 0; k + 1 < a.Length; k++)
                if (a[k] == "--parent-prop" && a[k + 1].Contains("="))
                    o.ParentProps[a[k + 1].Substring(0, a[k + 1].IndexOf('='))] = a[k + 1].Substring(a[k + 1].IndexOf('=') + 1);
            return o;
        }
    }
}
