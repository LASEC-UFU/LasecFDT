using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using LasecFDT.Core.Project;
using LasecFDT.Fdt1.Com;

namespace LasecFDT.Fdt1.Frame
{
    /// <summary>
    /// IPropertyBag implementado pelo Frame: recebe as propriedades gravadas pelo DTM em
    /// IPersistPropertyBag::Save e as devolve em IPersistPropertyBag::Load.
    /// Suporta VARIANTs escalares, strings, datas e arrays de bytes (SAFEARRAY de VT_UI1).
    /// </summary>
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class PropertyBagStore : IPropertyBag
    {
        private const int S_OK = 0;
        private const int E_INVALIDARG = unchecked((int)0x80070057);
        private const int E_FAIL = unchecked((int)0x80004005);

        private readonly Dictionary<string, object> _values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        public List<string> Warnings { get; } = new List<string>();
        public int Count => _values.Count;

        public int Read(string pszPropName, ref object pVar, IErrorLog pErrorLog)
        {
            if (!_values.TryGetValue(pszPropName, out var v)) return E_INVALIDARG; // propriedade inexistente
            pVar = v is byte[] b ? (byte[])b.Clone() : v;
            return S_OK;
        }

        public int Write(string pszPropName, ref object pVar)
        {
            if (pVar == null || pVar is DBNull || IsSupported(pVar))
            {
                _values[pszPropName] = pVar is byte[] b ? (byte[])b.Clone() : pVar;
                return S_OK;
            }
            Warnings.Add($"Propriedade '{pszPropName}' com tipo não suportado: {pVar.GetType().FullName}");
            return E_FAIL;
        }

        private static bool IsSupported(object v) =>
            v is string || v is bool || v is byte || v is sbyte || v is short || v is ushort || v is int || v is uint ||
            v is long || v is ulong || v is float || v is double || v is decimal || v is DateTime || v is byte[];

        public List<PersistedProperty> Export() => _values.Select(kv => new PersistedProperty
        {
            Name = kv.Key,
            Type = kv.Value == null ? "Empty" : kv.Value.GetType().Name,
            Value = kv.Value == null ? null
                  : kv.Value is byte[] b ? Convert.ToBase64String(b)
                  : kv.Value is DateTime d ? d.ToString("o", CultureInfo.InvariantCulture)
                  : Convert.ToString(kv.Value, CultureInfo.InvariantCulture),
        }).ToList();

        public static PropertyBagStore Import(IEnumerable<PersistedProperty> props)
        {
            var bag = new PropertyBagStore();
            foreach (var p in props ?? Enumerable.Empty<PersistedProperty>())
                bag._values[p.Name] = Parse(p);
            return bag;
        }

        private static object Parse(PersistedProperty p)
        {
            var ci = CultureInfo.InvariantCulture;
            switch (p.Type)
            {
                case "Empty": return null;
                case "String": return p.Value;
                case "Boolean": return bool.Parse(p.Value);
                case "Byte": return byte.Parse(p.Value, ci);
                case "SByte": return sbyte.Parse(p.Value, ci);
                case "Int16": return short.Parse(p.Value, ci);
                case "UInt16": return ushort.Parse(p.Value, ci);
                case "Int32": return int.Parse(p.Value, ci);
                case "UInt32": return uint.Parse(p.Value, ci);
                case "Int64": return long.Parse(p.Value, ci);
                case "UInt64": return ulong.Parse(p.Value, ci);
                case "Single": return float.Parse(p.Value, ci);
                case "Double": return double.Parse(p.Value, ci);
                case "Decimal": return decimal.Parse(p.Value, ci);
                case "DateTime": return DateTime.Parse(p.Value, ci, DateTimeStyles.RoundtripKind);
                case "Byte[]": return Convert.FromBase64String(p.Value ?? "");
                default: throw new NotSupportedException("Tipo de propriedade persistida desconhecido: " + p.Type);
            }
        }
    }
}
