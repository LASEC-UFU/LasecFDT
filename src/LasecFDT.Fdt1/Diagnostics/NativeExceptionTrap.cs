using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace LasecFDT.Fdt1.Diagnostics
{
    /// <summary>
    /// Diagnóstico: registra exceções nativas de primeira chance (código, endereço, módulo) por meio de um
    /// tratador vetorado (AddVectoredExceptionHandler). Não altera o tratamento — sempre devolve
    /// EXCEPTION_CONTINUE_SEARCH. Para exceções C++ do MSVC (0xE06D7363) decodifica o tipo lançado e, quando é
    /// _com_error (wrappers #import), o HRESULT e a descrição de IErrorInfo — que é onde um OCX VB6 coloca a
    /// mensagem do erro. Somente processos x86 (layout de ThrowInfo com ponteiros absolutos).
    /// </summary>
    public static class NativeExceptionTrap
    {
        public sealed class Entry
        {
            public uint Code;
            public IntPtr Address;
            public string Module;
            public string CppType;
            public int? ComErrorHResult;
            public string ComErrorDescription;
            public string ComErrorSource;
            /// <summary>Violação de acesso: 0 = leitura, 1 = escrita, 8 = execução bloqueada por DEP; e o endereço alvo.</summary>
            public int? AccessKind;
            public IntPtr AccessTarget;
            public override string ToString() =>
                $"0x{Code:X8} em {Module ?? "?"}+0x{Address.ToInt64():X}" +
                (AccessKind.HasValue ? $" acesso={(AccessKind == 0 ? "leitura" : AccessKind == 1 ? "escrita" : AccessKind == 8 ? "EXECUÇÃO (DEP)" : AccessKind.ToString())} alvo=0x{AccessTarget.ToInt64():X8}" : "") +
                (CppType != null ? $" C++ {CppType}" : "") +
                (ComErrorHResult.HasValue ? $" HRESULT=0x{ComErrorHResult.Value:X8}" : "") +
                (ComErrorSource != null ? $" origem='{ComErrorSource}'" : "") +
                (ComErrorDescription != null ? $" descrição='{ComErrorDescription}'" : "");
        }

        private delegate int VehHandler(IntPtr exceptionPointers);
        private static VehHandler _handler; // mantém o delegate vivo
        private static IntPtr _cookie;
        private static readonly object Gate = new object();
        public static List<Entry> Entries { get; } = new List<Entry>();
        public static Action<Entry> OnEntry { get; set; }
        public static int MaxEntries { get; set; } = 200;

        public static void Install()
        {
            if (IntPtr.Size != 4) throw new PlatformNotSupportedException("NativeExceptionTrap só suporta processos x86.");
            if (_cookie != IntPtr.Zero) return;
            _handler = Handle;
            _cookie = AddVectoredExceptionHandler(1, _handler);
        }

        public static void Uninstall()
        {
            if (_cookie == IntPtr.Zero) return;
            RemoveVectoredExceptionHandler(_cookie);
            _cookie = IntPtr.Zero;
        }

        private const uint MsvcCppException = 0xE06D7363;   // 'msc' | 0xE0000000
        private const uint ClrException = 0xE0434352;       // 'CCR'

        private static int Handle(IntPtr ep)
        {
            try
            {
                IntPtr rec = Marshal.ReadIntPtr(ep);                  // EXCEPTION_POINTERS.ExceptionRecord
                uint code = (uint)Marshal.ReadInt32(rec, 0);
                if (code == ClrException) return 0;                    // exceções gerenciadas: irrelevantes aqui
                IntPtr addr = Marshal.ReadIntPtr(rec, 12);
                int nParams = Marshal.ReadInt32(rec, 16);
                var e = new Entry { Code = code, Address = addr, Module = ModuleOf(addr) };
                if (e.Module != null) e.Address = new IntPtr(addr.ToInt64() - ModuleBase(addr).ToInt64());
                if (code == 0xC0000005 && nParams >= 2)
                {
                    e.AccessKind = Marshal.ReadInt32(rec, 20);
                    e.AccessTarget = Marshal.ReadIntPtr(rec, 24);
                }
                if (code == MsvcCppException && nParams >= 3)
                {
                    IntPtr p0 = Marshal.ReadIntPtr(rec, 20);           // magic 0x19930520..22
                    IntPtr obj = Marshal.ReadIntPtr(rec, 24);
                    IntPtr throwInfo = Marshal.ReadIntPtr(rec, 28);
                    if ((p0.ToInt64() & 0xFFFFFFF0) == 0x19930520 && throwInfo != IntPtr.Zero)
                    {
                        e.CppType = CppTypeName(throwInfo);
                        if (e.CppType != null && e.CppType.Contains("_com_error") && obj != IntPtr.Zero)
                        {
                            // class _com_error { vtbl; HRESULT m_hresult; IErrorInfo* m_perrinfo; ... }
                            e.ComErrorHResult = Marshal.ReadInt32(obj, 4);
                            IntPtr perr = Marshal.ReadIntPtr(obj, 8);
                            if (perr != IntPtr.Zero) ReadErrorInfo(perr, e);
                        }
                    }
                }
                lock (Gate)
                {
                    if (Entries.Count < MaxEntries) Entries.Add(e);
                }
                OnEntry?.Invoke(e);
            }
            catch { /* diagnóstico nunca pode interferir */ }
            return 0; // EXCEPTION_CONTINUE_SEARCH
        }

        /// <summary>ThrowInfo → CatchableTypeArray → CatchableType[0] → TypeDescriptor.name (x86).</summary>
        private static string CppTypeName(IntPtr throwInfo)
        {
            IntPtr cta = Marshal.ReadIntPtr(throwInfo, 12);
            if (cta == IntPtr.Zero) return null;
            int n = Marshal.ReadInt32(cta, 0);
            var names = new List<string>();
            for (int i = 0; i < Math.Min(n, 4); i++)
            {
                IntPtr ct = Marshal.ReadIntPtr(cta, 4 + 4 * i);
                IntPtr td = Marshal.ReadIntPtr(ct, 4);
                names.Add(Marshal.PtrToStringAnsi(td + 8));
            }
            return string.Join(" : ", names);
        }

        [ComImport, Guid("1CF2B120-547D-101B-8E65-08002B2BD119"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IErrorInfo
        {
            [PreserveSig] int GetGUID(out Guid guid);
            [PreserveSig] int GetSource([MarshalAs(UnmanagedType.BStr)] out string source);
            [PreserveSig] int GetDescription([MarshalAs(UnmanagedType.BStr)] out string description);
        }

        private static void ReadErrorInfo(IntPtr perr, Entry e)
        {
            var ei = (IErrorInfo)Marshal.GetObjectForIUnknown(perr);
            try
            {
                if (ei.GetDescription(out var d) == 0) e.ComErrorDescription = d;
                if (ei.GetSource(out var s) == 0) e.ComErrorSource = s;
            }
            finally { Marshal.ReleaseComObject(ei); }
        }

        private static IntPtr ModuleBase(IntPtr addr) =>
            GetModuleHandleEx(0x4 | 0x2, addr, out var h) ? h : IntPtr.Zero; // FROM_ADDRESS | UNCHANGED_REFCOUNT

        private static string ModuleOf(IntPtr addr)
        {
            var h = ModuleBase(addr);
            if (h == IntPtr.Zero) return null;
            var sb = new StringBuilder(520);
            return GetModuleFileName(h, sb, sb.Capacity) > 0 ? System.IO.Path.GetFileName(sb.ToString()) : null;
        }

        [DllImport("kernel32.dll")] private static extern IntPtr AddVectoredExceptionHandler(uint first, VehHandler handler);
        [DllImport("kernel32.dll")] private static extern uint RemoveVectoredExceptionHandler(IntPtr cookie);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetModuleHandleEx(uint flags, IntPtr addr, out IntPtr module);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetModuleFileName(IntPtr h, StringBuilder sb, int size);
    }
}
