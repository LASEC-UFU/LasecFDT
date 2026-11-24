using System;
using System.IO;
using System.IO.Pipes;
using System.Windows.Forms;
using LasecFDT.Fdt1.Ui;

namespace LasecFDT.Worker.Fdt1
{
    internal static class Program
    {
        /// <summary>
        /// LasecFDT.Worker.Fdt1.exe --pipe NOME [--log arquivo]
        /// Conecta-se ao named pipe criado pelo supervisor e atende JSON-RPC (WorkerMethods). Uma instância de DTM.
        /// </summary>
        [STAThread]
        private static int Main(string[] args)
        {
            string pipe = Arg(args, "--pipe");
            string log = Arg(args, "--log");
            if (pipe == null) { Console.Error.WriteLine("uso: LasecFDT.Worker.Fdt1.exe --pipe NOME [--log arquivo]"); return 2; }

            string dep = LegacyActiveXProcess.EnableDepWithAtlThunkEmulation();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // Worker não tem usuário: nenhuma exceção pode abrir diálogo modal.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                WorkerHost.WriteLog(log, "exceção sem tratamento: " + e.ExceptionObject);
                LegacyActiveXProcess.TerminateNow(7);
            };

            var stream = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { stream.Connect(10000); }
            catch (Exception ex) { Console.Error.WriteLine("não foi possível conectar ao supervisor: " + ex.Message); return 3; }

            var host = new WorkerHost(stream, dep, log);
            Application.Run(host);
            host.FlushOutgoing();
            // DLLs legadas falham na própria descarga (doc 08 §5): encerrar sem DLL_PROCESS_DETACH.
            LegacyActiveXProcess.TerminateNow(host.ExitCode);
            return host.ExitCode;
        }

        private static string Arg(string[] a, string name)
        {
            int i = Array.IndexOf(a, name);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
        }
    }
}
