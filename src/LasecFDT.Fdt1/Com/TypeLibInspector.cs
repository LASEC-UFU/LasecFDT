using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using TYPEDESC = System.Runtime.InteropServices.ComTypes.TYPEDESC;
using TYPEATTR = System.Runtime.InteropServices.ComTypes.TYPEATTR;
using FUNCDESC = System.Runtime.InteropServices.ComTypes.FUNCDESC;
using ELEMDESC = System.Runtime.InteropServices.ComTypes.ELEMDESC;
using VARDESC = System.Runtime.InteropServices.ComTypes.VARDESC;
using TYPELIBATTR = System.Runtime.InteropServices.ComTypes.TYPELIBATTR;
using TYPEKIND = System.Runtime.InteropServices.ComTypes.TYPEKIND;
using TYPEFLAGS = System.Runtime.InteropServices.ComTypes.TYPEFLAGS;
using IMPLTYPEFLAGS = System.Runtime.InteropServices.ComTypes.IMPLTYPEFLAGS;
using PARAMFLAG = System.Runtime.InteropServices.ComTypes.PARAMFLAG;
using VARKIND = System.Runtime.InteropServices.ComTypes.VARKIND;
using LasecFDT.Core.TypeLib;

namespace LasecFDT.Fdt1.Com
{
    /// <summary>
    /// Lê uma type library COM (arquivo .tlb/.dll/.ocx) e produz um modelo descritivo
    /// (interfaces, IIDs, métodos, parâmetros, coclasses). Usado para:
    ///  1) gerar o catálogo autoritativo das interfaces FDT a partir do FDT100.dll oficial;
    ///  2) verificar estaticamente quais interfaces FDT uma coclass de DTM declara.
    /// A leitura usa LoadTypeLibEx(REGKIND_NONE): não registra nada e funciona com
    /// arquivos de outra arquitetura (o recurso TYPELIB é lido como dado).
    /// </summary>
    public static class TypeLibInspector
    {
        private enum RegKind { Default = 0, Register = 1, None = 2 }

        [DllImport("oleaut32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void LoadTypeLibEx(string szFile, RegKind regkind, out ITypeLib pptlib);

        [DllImport("oleaut32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void LoadRegTypeLib(ref Guid rguid, ushort wVerMajor, ushort wVerMinor, int lcid, out ITypeLib pptlib);

        public static TypeLibModel Load(string path)
        {
            LoadTypeLibEx(path, RegKind.None, out ITypeLib tl);
            try { return Describe(tl, path); }
            finally { Marshal.ReleaseComObject(tl); }
        }

        public static TypeLibModel LoadRegistered(Guid libId, ushort major, ushort minor)
        {
            LoadRegTypeLib(ref libId, major, minor, 0, out ITypeLib tl);
            try { return Describe(tl, null); }
            finally { Marshal.ReleaseComObject(tl); }
        }

        private static TypeLibModel Describe(ITypeLib tl, string path)
        {
            var model = new TypeLibModel { SourcePath = path };
            tl.GetDocumentation(-1, out string libName, out string libDoc, out _, out _);
            tl.GetLibAttr(out IntPtr pLibAttr);
            try
            {
                var la = Marshal.PtrToStructure<TYPELIBATTR>(pLibAttr);
                model.Name = libName;
                model.Doc = libDoc;
                model.LibId = la.guid.ToString("D").ToUpperInvariant();
                model.Version = la.wMajorVerNum + "." + la.wMinorVerNum;
            }
            finally { tl.ReleaseTLibAttr(pLibAttr); }

            int count = tl.GetTypeInfoCount();
            for (int i = 0; i < count; i++)
            {
                tl.GetTypeInfo(i, out ITypeInfo ti);
                try { model.Types.Add(DescribeType(ti)); }
                finally { Marshal.ReleaseComObject(ti); }
            }
            return model;
        }

        private static TypeModel DescribeType(ITypeInfo ti)
        {
            ti.GetDocumentation(-1, out string name, out string doc, out _, out _);
            ti.GetTypeAttr(out IntPtr pAttr);
            TYPEATTR ta;
            try { ta = Marshal.PtrToStructure<TYPEATTR>(pAttr); }
            finally { ti.ReleaseTypeAttr(pAttr); }

            var tm = new TypeModel
            {
                Name = name,
                Doc = doc,
                Guid = ta.guid.ToString("D").ToUpperInvariant(),
                Kind = ta.typekind.ToString().Replace("TKIND_", ""),
                Flags = ((int)ta.wTypeFlags),
                IsDual = (ta.wTypeFlags & TYPEFLAGS.TYPEFLAG_FDUAL) != 0,
                IsHidden = (ta.wTypeFlags & TYPEFLAGS.TYPEFLAG_FHIDDEN) != 0,
            };

            switch (ta.typekind)
            {
                case TYPEKIND.TKIND_DISPATCH:
                case TYPEKIND.TKIND_INTERFACE:
                {
                    ITypeInfo vtbl = ti;
                    bool release = false;
                    // Para interfaces duais, descreve o lado vtable (assinaturas HRESULT reais).
                    if (ta.typekind == TYPEKIND.TKIND_DISPATCH && tm.IsDual)
                    {
                        try
                        {
                            ti.GetRefTypeOfImplType(-1, out int href);
                            ti.GetRefTypeInfo(href, out vtbl);
                            release = true;
                        }
                        catch (COMException) { vtbl = ti; }
                    }
                    try { DescribeInterface(vtbl, tm); }
                    finally { if (release) Marshal.ReleaseComObject(vtbl); }
                    break;
                }
                case TYPEKIND.TKIND_COCLASS:
                    for (int i = 0; i < ta.cImplTypes; i++)
                    {
                        ti.GetRefTypeOfImplType(i, out int href);
                        ti.GetRefTypeInfo(href, out ITypeInfo rti);
                        try
                        {
                            ti.GetImplTypeFlags(i, out IMPLTYPEFLAGS f);
                            rti.GetDocumentation(-1, out string iname, out _, out _, out _);
                            tm.Implements.Add(new ImplementedInterface
                            {
                                Name = iname,
                                Iid = GetGuid(rti),
                                IsDefault = (f & IMPLTYPEFLAGS.IMPLTYPEFLAG_FDEFAULT) != 0,
                                IsSource = (f & IMPLTYPEFLAGS.IMPLTYPEFLAG_FSOURCE) != 0,
                            });
                        }
                        finally { Marshal.ReleaseComObject(rti); }
                    }
                    break;
                case TYPEKIND.TKIND_ALIAS:
                    tm.AliasOf = TypeDescToString(ta.tdescAlias, ti);
                    break;
                case TYPEKIND.TKIND_ENUM:
                case TYPEKIND.TKIND_MODULE:
                case TYPEKIND.TKIND_RECORD:
                    for (int i = 0; i < ta.cVars; i++)
                    {
                        ti.GetVarDesc(i, out IntPtr pVar);
                        try
                        {
                            var vd = Marshal.PtrToStructure<VARDESC>(pVar);
                            var names = new string[1];
                            ti.GetNames(vd.memid, names, 1, out _);
                            string value = null;
                            if (vd.varkind == VARKIND.VAR_CONST && vd.desc.lpvarValue != IntPtr.Zero)
                                value = Convert.ToString(Marshal.GetObjectForNativeVariant(vd.desc.lpvarValue));
                            tm.Members.Add(new MemberModel
                            {
                                Name = names[0],
                                Type = TypeDescToString(vd.elemdescVar.tdesc, ti),
                                Value = value,
                            });
                        }
                        finally { ti.ReleaseVarDesc(pVar); }
                    }
                    break;
            }
            return tm;
        }

        private static void DescribeInterface(ITypeInfo ti, TypeModel tm)
        {
            ti.GetTypeAttr(out IntPtr pAttr);
            TYPEATTR ta;
            try { ta = Marshal.PtrToStructure<TYPEATTR>(pAttr); }
            finally { ti.ReleaseTypeAttr(pAttr); }

            if (ta.cImplTypes > 0)
            {
                ti.GetRefTypeOfImplType(0, out int href);
                ti.GetRefTypeInfo(href, out ITypeInfo baseTi);
                try { baseTi.GetDocumentation(-1, out string bn, out _, out _, out _); tm.BaseInterface = bn; }
                finally { Marshal.ReleaseComObject(baseTi); }
            }

            int elemSize = Marshal.SizeOf<ELEMDESC>();
            for (int i = 0; i < ta.cFuncs; i++)
            {
                ti.GetFuncDesc(i, out IntPtr pFunc);
                try
                {
                    var fd = Marshal.PtrToStructure<FUNCDESC>(pFunc);
                    var names = new string[fd.cParams + 1];
                    ti.GetNames(fd.memid, names, names.Length, out int got);
                    var m = new MethodModel
                    {
                        Name = names[0],
                        DispId = fd.memid,
                        InvokeKind = fd.invkind.ToString().Replace("INVOKE_", ""),
                        ReturnType = TypeDescToString(fd.elemdescFunc.tdesc, ti),
                        VtableOffset = fd.oVft,
                    };
                    for (int p = 0; p < fd.cParams; p++)
                    {
                        var ed = Marshal.PtrToStructure<ELEMDESC>(fd.lprgelemdescParam + p * elemSize);
                        var flags = ed.desc.paramdesc.wParamFlags;
                        m.Parameters.Add(new ParameterModel
                        {
                            Name = (p + 1 < got) ? names[p + 1] : ("arg" + p),
                            Type = TypeDescToString(ed.tdesc, ti),
                            In = (flags & PARAMFLAG.PARAMFLAG_FIN) != 0,
                            Out = (flags & PARAMFLAG.PARAMFLAG_FOUT) != 0,
                            RetVal = (flags & PARAMFLAG.PARAMFLAG_FRETVAL) != 0,
                        });
                    }
                    tm.Methods.Add(m);
                }
                finally { ti.ReleaseFuncDesc(pFunc); }
            }
        }

        private static string GetGuid(ITypeInfo ti)
        {
            ti.GetTypeAttr(out IntPtr p);
            try { return Marshal.PtrToStructure<TYPEATTR>(p).guid.ToString("D").ToUpperInvariant(); }
            finally { ti.ReleaseTypeAttr(p); }
        }

        private static string TypeDescToString(TYPEDESC td, ITypeInfo ctx)
        {
            var vt = (VarEnum)td.vt;
            switch (vt)
            {
                case VarEnum.VT_PTR:
                    return TypeDescToString(Marshal.PtrToStructure<TYPEDESC>(td.lpValue), ctx) + "*";
                case VarEnum.VT_SAFEARRAY:
                    return "SAFEARRAY(" + TypeDescToString(Marshal.PtrToStructure<TYPEDESC>(td.lpValue), ctx) + ")";
                case VarEnum.VT_USERDEFINED:
                    try
                    {
                        ctx.GetRefTypeInfo(unchecked((int)td.lpValue.ToInt64()), out ITypeInfo r);
                        try { r.GetDocumentation(-1, out string n, out _, out _, out _); return n; }
                        finally { Marshal.ReleaseComObject(r); }
                    }
                    catch (COMException) { return "<userdefined>"; }
                default:
                    return VtName(vt);
            }
        }

        private static string VtName(VarEnum vt)
        {
            switch (vt)
            {
                case VarEnum.VT_BSTR: return "BSTR";
                case VarEnum.VT_BOOL: return "VARIANT_BOOL";
                case VarEnum.VT_I4: return "long";
                case VarEnum.VT_INT: return "int";
                case VarEnum.VT_UI4: return "unsigned long";
                case VarEnum.VT_I2: return "short";
                case VarEnum.VT_UI1: return "unsigned char";
                case VarEnum.VT_HRESULT: return "HRESULT";
                case VarEnum.VT_VOID: return "void";
                case VarEnum.VT_VARIANT: return "VARIANT";
                case VarEnum.VT_DISPATCH: return "IDispatch*";
                case VarEnum.VT_UNKNOWN: return "IUnknown*";
                case VarEnum.VT_LPWSTR: return "LPWSTR";
                case VarEnum.VT_LPSTR: return "LPSTR";
                default: return vt.ToString().Replace("VT_", "");
            }
        }
    }
}
