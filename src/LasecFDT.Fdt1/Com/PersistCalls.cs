using System;
using System.Runtime.InteropServices;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace LasecFDT.Fdt1.Com
{
    /// <summary>
    /// IPersistPropertyBag / IPersistStreamInit do DTM, para DTMs nativos e .NET.
    ///
    /// Um DTM .NET registrado como servidor COM in-process chega a este processo como o próprio objeto gerenciado
    /// (o CLR desembrulha o CCW), e não como RCW. O cast para as interfaces OLE declaradas aqui falha, porque o DTM
    /// implementa a sua própria declaração (mesmo GUID, outro tipo .NET). Nesse caso a chamada vai pela vtable do CCW
    /// — exatamente a ABI que um Frame nativo usa —, com o property bag e o stream passados como ponteiros COM.
    /// </summary>
    public static class PersistCalls
    {
        // vtables (ocidl.h), depois de IUnknown (0–2):
        //   IPersistPropertyBag: GetClassID 3, InitNew 4, Load 5, Save 6
        //   IPersistStreamInit:  GetClassID 3, IsDirty 4, Load 5, Save 6, GetSizeMax 7, InitNew 8
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Fn0(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Fn1(IntPtr self, IntPtr a);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Fn2(IntPtr self, IntPtr a, IntPtr b);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FnPB(IntPtr self, IntPtr a, int b);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FnPBB(IntPtr self, IntPtr a, int b, int c);

        public static bool IsManaged(object dtm) => dtm != null && !Marshal.IsComObject(dtm);

        public static void PropertyBagInitNew(object dtm)
        {
            if (!IsManaged(dtm)) { ((IPersistPropertyBag)dtm).InitNew(); return; }
            Call(dtm, typeof(IPersistPropertyBag).GUID, 4, p => Slot<Fn0>(p, 4)(p));
        }

        public static void PropertyBagLoad(object dtm, IPropertyBag bag)
        {
            if (!IsManaged(dtm)) { ((IPersistPropertyBag)dtm).Load(bag, null); return; }
            WithItf(bag, typeof(IPropertyBag), pb => Call(dtm, typeof(IPersistPropertyBag).GUID, 5, p => Slot<Fn2>(p, 5)(p, pb, IntPtr.Zero)));
        }

        public static void PropertyBagSave(object dtm, IPropertyBag bag, bool clearDirty, bool saveAll)
        {
            if (!IsManaged(dtm)) { ((IPersistPropertyBag)dtm).Save(bag, clearDirty, saveAll); return; }
            WithItf(bag, typeof(IPropertyBag), pb => Call(dtm, typeof(IPersistPropertyBag).GUID, 6, p => Slot<FnPBB>(p, 6)(p, pb, clearDirty ? 1 : 0, saveAll ? 1 : 0)));
        }

        public static void StreamInitNew(object dtm)
        {
            if (!IsManaged(dtm)) { ((IPersistStreamInit)dtm).InitNew(); return; }
            Call(dtm, typeof(IPersistStreamInit).GUID, 8, p => Slot<Fn0>(p, 8)(p));
        }

        public static void StreamLoad(object dtm, ComTypes.IStream stream)
        {
            if (!IsManaged(dtm)) { ((IPersistStreamInit)dtm).Load(stream); return; }
            WithItf(stream, typeof(ComTypes.IStream), ps => Call(dtm, typeof(IPersistStreamInit).GUID, 5, p => Slot<Fn1>(p, 5)(p, ps)));
        }

        public static void StreamSave(object dtm, ComTypes.IStream stream, bool clearDirty)
        {
            if (!IsManaged(dtm)) { ((IPersistStreamInit)dtm).Save(stream, clearDirty); return; }
            WithItf(stream, typeof(ComTypes.IStream), ps => Call(dtm, typeof(IPersistStreamInit).GUID, 6, p => Slot<FnPB>(p, 6)(p, ps, clearDirty ? 1 : 0)));
        }

        private static T Slot<T>(IntPtr itf, int slot) where T : class =>
            (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(itf), slot * IntPtr.Size), typeof(T));

        /// <summary>QueryInterface no CCW do DTM e chamada pela vtable; HRESULT de falha vira exceção.</summary>
        private static void Call(object dtm, Guid iid, int slot, Func<IntPtr, int> call)
        {
            IntPtr unk = Marshal.GetIUnknownForObject(dtm);
            try
            {
                int hr = Marshal.QueryInterface(unk, ref iid, out IntPtr itf);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                try
                {
                    hr = call(itf);
                    if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                }
                finally { Marshal.Release(itf); }
            }
            finally { Marshal.Release(unk); }
        }

        private static void WithItf(object o, Type itfType, Action<IntPtr> use)
        {
            IntPtr p = Marshal.GetComInterfaceForObject(o, itfType);
            try { use(p); }
            finally { Marshal.Release(p); }
        }
    }
}
