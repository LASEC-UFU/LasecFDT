using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LasecFDT.Core.TypeLib;
using LasecFDT.Fdt1.Com;

namespace LasecFDT.Fdt1
{
    /// <summary>
    /// Tabela IID → nome de todas as interfaces FDT 1.x, lida da type library oficial FDT100.dll.
    /// Evita qualquer GUID digitado à mão.
    /// </summary>
    public sealed class FdtInterfaceTable
    {
        private readonly Dictionary<Guid, TypeModel> _byIid;
        public TypeLibModel TypeLib { get; }

        private FdtInterfaceTable(TypeLibModel tl)
        {
            TypeLib = tl;
            _byIid = tl.Types.Where(t => t.Kind == "DISPATCH" || t.Kind == "INTERFACE")
                             .Where(t => t.Name != "IStream" && t.Name != "ISequentialStream")
                             .ToDictionary(t => new Guid(t.Guid));
        }

        public IEnumerable<TypeModel> Interfaces => _byIid.Values.OrderBy(t => t.Name);
        public bool IsFdt(Guid iid) => _byIid.ContainsKey(iid);
        public string NameOf(Guid iid) => _byIid.TryGetValue(iid, out var t) ? t.Name : null;
        public Guid IidOf(string name) => new Guid(_byIid.Values.First(t => t.Name == name).Guid);

        private static FdtInterfaceTable _default;

        /// <summary>
        /// Carrega a FDT100.dll distribuída junto da aplicação (lib/fdt-jig). Fallback: type library registrada.
        /// </summary>
        public static FdtInterfaceTable Default
        {
            get
            {
                if (_default != null) return _default;
                string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FDT100.dll");
                var tl = File.Exists(local)
                    ? TypeLibInspector.Load(local)
                    : TypeLibInspector.LoadRegistered(Core.FdtIds.LibIdFdt100, 1, 0);
                return _default = new FdtInterfaceTable(tl);
            }
        }
    }
}
