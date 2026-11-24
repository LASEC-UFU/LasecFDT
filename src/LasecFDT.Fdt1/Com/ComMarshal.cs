using System;
using System.Runtime.InteropServices;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace LasecFDT.Fdt1.Com
{
    /// <summary>
    /// Passagem de ponteiros de interface entre processos pelo próprio COM (CoMarshalInterface/CoUnmarshalInterface).
    /// As interfaces FDT são duais/oleautomation e estão registradas com o marshaler universal (PSOAInterface,
    /// type library FDT100.dll) — o proxy no outro processo é um proxy COM real, e os callbacks
    /// (ex.: IFdtCommunicationEvents) voltam automaticamente pelo mesmo mecanismo.
    /// </summary>
    public static class ComMarshal
    {
        private const uint MSHCTX_LOCAL = 0;      // outro processo na mesma máquina
        private const uint MSHLFLAGS_NORMAL = 0;  // um único unmarshal

        /// <summary>Serializa a interface <paramref name="iid"/> do objeto para bytes (OBJREF).</summary>
        public static byte[] Marshal(object obj, Guid iid)
        {
            Check(CreateStreamOnHGlobal(IntPtr.Zero, true, out var stream));
            IntPtr unk = System.Runtime.InteropServices.Marshal.GetIUnknownForObject(obj);
            try
            {
                Check(CoMarshalInterface(stream, ref iid, unk, MSHCTX_LOCAL, IntPtr.Zero, MSHLFLAGS_NORMAL));
                Check(GetHGlobalFromStream(stream, out IntPtr hg));
                stream.Stat(out var st, 1 /*STATFLAG_NONAME*/);
                int size = (int)st.cbSize;
                IntPtr p = GlobalLock(hg);
                try
                {
                    var bytes = new byte[size];
                    System.Runtime.InteropServices.Marshal.Copy(p, bytes, 0, size);
                    return bytes;
                }
                finally { GlobalUnlock(hg); }
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.Release(unk);
                System.Runtime.InteropServices.Marshal.ReleaseComObject(stream);
            }
        }

        /// <summary>Reconstrói o proxy a partir dos bytes gerados por <see cref="Marshal"/> (em outro processo).</summary>
        public static object Unmarshal(byte[] objref, Guid iid)
        {
            IntPtr hg = GlobalAlloc(0x0002 /*GMEM_MOVEABLE*/, (UIntPtr)objref.Length);
            IntPtr p = GlobalLock(hg);
            System.Runtime.InteropServices.Marshal.Copy(objref, 0, p, objref.Length);
            GlobalUnlock(hg);
            Check(CreateStreamOnHGlobal(hg, true, out var stream));
            try
            {
                Check(CoUnmarshalInterface(stream, ref iid, out object result));
                return result;
            }
            finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(stream); }
        }

        private static void Check(int hr) { if (hr < 0) System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(hr); }

        [DllImport("ole32.dll")] private static extern int CreateStreamOnHGlobal(IntPtr hGlobal, bool deleteOnRelease, out ComTypes.IStream stream);
        [DllImport("ole32.dll")] private static extern int GetHGlobalFromStream(ComTypes.IStream stream, out IntPtr hGlobal);
        [DllImport("ole32.dll")] private static extern int CoMarshalInterface(ComTypes.IStream stream, ref Guid iid, IntPtr unk, uint destContext, IntPtr reserved, uint flags);
        [DllImport("ole32.dll")] private static extern int CoUnmarshalInterface(ComTypes.IStream stream, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr h);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr h);
    }
}
