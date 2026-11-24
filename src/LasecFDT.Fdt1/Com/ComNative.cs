using System;
using System.IO;
using System.Runtime.InteropServices;
using STATSTG = System.Runtime.InteropServices.ComTypes.STATSTG;
using IStream = System.Runtime.InteropServices.ComTypes.IStream;

namespace LasecFDT.Fdt1.Com
{
    // Declarações de interfaces COM padrão da Microsoft (objidl.h / ocidl.h / comcat.h).
    // Os IIDs de persistência coincidem com os declarados em Jigfdt.COMInterop do interop oficial FDT-JIG.

    /// <summary>ICatInformation (comcat.h) — enumeração de classes por categoria (FDT 1.2.1 §7.1.5).</summary>
    [ComImport, Guid("0002E013-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICatInformation
    {
        void EnumCategories(int lcid, out IntPtr ppenumCategoryInfo);
        void GetCategoryDesc(ref Guid rcatid, int lcid, [MarshalAs(UnmanagedType.LPWStr)] out string pszDesc);
        void EnumClassesOfCategories(int cImplemented, [MarshalAs(UnmanagedType.LPArray)] Guid[] rgcatidImpl,
                                     int cRequired, [MarshalAs(UnmanagedType.LPArray)] Guid[] rgcatidReq,
                                     out IEnumGUID ppenumClsid);
    }

    [ComImport, Guid("0002E000-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IEnumGUID
    {
        [PreserveSig] int Next(int celt, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] Guid[] rgelt, out int pceltFetched);
        [PreserveSig] int Skip(int celt);
        [PreserveSig] int Reset();
        void Clone(out IEnumGUID ppenum);
    }

    internal static class ComCategories
    {
        public static readonly Guid ClsidStdComponentCategoriesMgr = new Guid("0002E005-0000-0000-C000-000000000046");
    }

    /// <summary>IPersistStreamInit (ocidl.h). Uma das duas interfaces de persistência exigidas de um DTM (§2.7.1).</summary>
    [ComImport, Guid("7FD52380-4E07-101B-AE2D-08002B2EC713"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPersistStreamInit
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([In, MarshalAs(UnmanagedType.Interface)] IStream pStm);
        void Save([In, MarshalAs(UnmanagedType.Interface)] IStream pStm, [MarshalAs(UnmanagedType.Bool)] bool fClearDirty);
        void GetSizeMax(out long pCbSize);
        void InitNew();
    }

    /// <summary>IPersistPropertyBag (ocidl.h). A outra interface de persistência de DTM (§2.7.1).</summary>
    [ComImport, Guid("37D84F60-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPersistPropertyBag
    {
        void GetClassID(out Guid pClassID);
        void InitNew();
        void Load([In, MarshalAs(UnmanagedType.Interface)] IPropertyBag pPropBag, [In, MarshalAs(UnmanagedType.Interface)] IErrorLog pErrorLog);
        void Save([In, MarshalAs(UnmanagedType.Interface)] IPropertyBag pPropBag,
                  [MarshalAs(UnmanagedType.Bool)] bool fClearDirty,
                  [MarshalAs(UnmanagedType.Bool)] bool fSaveAllProperties);
    }

    /// <summary>IPropertyBag (ocidl.h) — implementado pelo Frame como armazenamento.</summary>
    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyBag
    {
        [PreserveSig] int Read([In, MarshalAs(UnmanagedType.LPWStr)] string pszPropName, [In, Out] ref object pVar, [In, MarshalAs(UnmanagedType.Interface)] IErrorLog pErrorLog);
        [PreserveSig] int Write([In, MarshalAs(UnmanagedType.LPWStr)] string pszPropName, [In] ref object pVar);
    }

    [ComImport, Guid("3127CA40-446E-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IErrorLog
    {
        void AddError([In, MarshalAs(UnmanagedType.LPWStr)] string pszPropName, [In] IntPtr pExcepInfo);
    }

    /// <summary>
    /// IStream sobre um MemoryStream, para IPersistStreamInit::Save/Load e IDtmImportExport.
    /// </summary>
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class ManagedIStream : IStream
    {
        private readonly MemoryStream _ms;
        public ManagedIStream() : this(new MemoryStream()) { }
        public ManagedIStream(byte[] data) : this(new MemoryStream()) { _ms.Write(data, 0, data.Length); _ms.Position = 0; }
        private ManagedIStream(MemoryStream ms) { _ms = ms; }
        public byte[] ToArray() => _ms.ToArray();

        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            int n = _ms.Read(pv, 0, cb);
            if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, n);
        }
        public void Write(byte[] pv, int cb, IntPtr pcbWritten)
        {
            _ms.Write(pv, 0, cb);
            if (pcbWritten != IntPtr.Zero) Marshal.WriteInt32(pcbWritten, cb);
        }
        public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
        {
            long pos = _ms.Seek(dlibMove, (SeekOrigin)dwOrigin);
            if (plibNewPosition != IntPtr.Zero) Marshal.WriteInt64(plibNewPosition, pos);
        }
        public void SetSize(long libNewSize) => _ms.SetLength(libNewSize);
        public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten)
        {
            var buf = new byte[Math.Min(cb, _ms.Length - _ms.Position)];
            int n = _ms.Read(buf, 0, buf.Length);
            pstm.Write(buf, n, IntPtr.Zero);
            if (pcbRead != IntPtr.Zero) Marshal.WriteInt64(pcbRead, n);
            if (pcbWritten != IntPtr.Zero) Marshal.WriteInt64(pcbWritten, n);
        }
        public void Commit(int grfCommitFlags) { }
        public void Revert() { }
        public void LockRegion(long libOffset, long cb, int dwLockType) { }
        public void UnlockRegion(long libOffset, long cb, int dwLockType) { }
        public void Stat(out STATSTG pstatstg, int grfStatFlag)
        {
            pstatstg = new STATSTG { type = 2 /*STGTY_STREAM*/, cbSize = _ms.Length, grfMode = 2 /*STGM_READWRITE*/ };
        }
        public void Clone(out IStream ppstm)
        {
            var copy = new ManagedIStream(_ms.ToArray());
            copy._ms.Position = _ms.Position;
            ppstm = copy;
        }
    }
}
