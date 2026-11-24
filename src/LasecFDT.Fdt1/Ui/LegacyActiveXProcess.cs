using System;
using System.Runtime.InteropServices;

namespace LasecFDT.Fdt1.Ui
{
    /// <summary>
    /// Configuração de processo para hospedar controles ActiveX legados (ATL 3.0/VC6, VB6).
    /// O executável é gerado sem NXCOMPAT (Directory.Build.targets, LasecLegacyActiveXHost=true) e, ao iniciar,
    /// religa o DEP com a emulação de thunks ATL — sem o flag PROCESS_DEP_DISABLE_ATL_THUNK_EMULATION.
    /// </summary>
    public static class LegacyActiveXProcess
    {
        private const uint PROCESS_DEP_ENABLE = 0x1;

        /// <summary>Retorna uma descrição do estado de DEP do processo após a configuração.</summary>
        public static string EnableDepWithAtlThunkEmulation()
        {
            if (IntPtr.Size != 4) return "processo 64 bits: DEP sempre ativo (sem emulação de thunks)";
            bool set = SetProcessDEPPolicy(PROCESS_DEP_ENABLE);
            int err = set ? 0 : Marshal.GetLastWin32Error();
            return Describe() + (set ? "" : $" (SetProcessDEPPolicy falhou: erro {err})");
        }

        public static string Describe()
        {
            if (!GetProcessDEPPolicy(GetCurrentProcess(), out uint flags, out bool permanent)) return "DEP: desconhecido";
            bool dep = (flags & 1) != 0;
            bool noThunk = (flags & 2) != 0;
            return $"DEP={(dep ? "ativo" : "inativo")}, emulação de thunks ATL={(dep && !noThunk ? "ativa" : dep ? "DESATIVADA" : "n/a")}, permanente={permanent}";
        }

        /// <summary>
        /// Limpeza COM ainda na thread STA, com o laço de mensagens vivo: libera RCWs pendentes (finalizadores)
        /// e descarrega DLLs COM sem uso. Evita que objetos de apartamento sejam liberados tarde, por outra thread.
        /// </summary>
        public static void CleanupComOnStaThread()
        {
            for (int i = 0; i < 2; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
            Marshal.CleanupUnusedObjectsInCurrentContext();
            System.Windows.Forms.Application.DoEvents();
            CoFreeUnusedLibrariesEx(0, 0);
        }

        /// <summary>
        /// Encerra o processo sem executar a descarga das DLLs (DLL_PROCESS_DETACH). Último recurso para DTMs
        /// legados que falham na descarga — aceitável porque cada worker hospeda uma única instância e todo o
        /// estado já foi persistido/relatado antes.
        /// </summary>
        public static void TerminateNow(int exitCode)
        {
            Console.Out.Flush(); Console.Error.Flush();
            TerminateProcess(GetCurrentProcess(), (uint)exitCode);
        }

        [DllImport("ole32.dll")] private static extern void CoFreeUnusedLibrariesEx(uint delayMs, uint reserved);
        [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessDEPPolicy(uint flags);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessDEPPolicy(IntPtr process, out uint flags, out bool permanent);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    }
}
