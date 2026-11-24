using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using LasecFDT.Core.Rpc;

namespace LasecFDT.Host
{
    /// <summary>
    /// Documento de IDtmParameter::GetParameters (DTMParameterSchema) achatado numa lista de parâmetros, para mostrar e
    /// comparar sem depender do DTM: variáveis (fdt:DtmVariable com fdt:Value) e o endereço no barramento.
    /// </summary>
    public static class ParameterModel
    {
        public static ParameterSetDto Parse(string tag, string xml)
        {
            var r = new ParameterSetDto { Tag = tag, Xml = xml };
            if (string.IsNullOrWhiteSpace(xml)) return r;
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var root = doc.DocumentElement;
            r.StorageState = Attr(root, "storageState");
            r.DataSetState = Attr(root, "dataSetState");
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var sa in root.GetElementsByTagName("*").OfType<XmlElement>().Where(e => e.LocalName == "SlaveAddress"))
                Add(r, seen, "Barramento", "Endereço (slaveAddress)", Attr(sa, "slaveAddress"), true);

            foreach (var v in root.GetElementsByTagName("*").OfType<XmlElement>().Where(e => e.LocalName == "DtmVariable"))
            {
                string name = Attr(v, "name") ?? "?";
                var groups = v.ParentNode is XmlElement ? Ancestors(v).Where(a => a.LocalName == "DtmVariables").Select(a => Attr(a, "name")).Where(n => n != null).Reverse() : Enumerable.Empty<string>();
                var value = v.ChildNodes.OfType<XmlElement>().FirstOrDefault(c => c.LocalName == "Value");
                Add(r, seen, string.Join(" / ", groups), name, ValueText(value), value != null && Attr(value, "writeAccess") == "1");
            }
            return r;
        }

        private static void Add(ParameterSetDto r, HashSet<string> seen, string group, string name, string value, bool writable)
        {
            string path = string.IsNullOrEmpty(group) ? name : group + " / " + name;
            string unique = path;
            for (int i = 2; !seen.Add(unique); i++) unique = $"{path} ({i})";
            r.Items.Add(new ParameterItemDto { Path = unique, Name = name, Group = group, Value = value, Writable = writable });
        }

        /// <summary>Texto do valor: fdt:Display@string, senão o primeiro atributo "value"/"string" de um descendente.</summary>
        private static string ValueText(XmlElement value)
        {
            if (value == null) return null;
            foreach (var e in value.GetElementsByTagName("*").OfType<XmlElement>())
            {
                if (e.LocalName == "Display" && e.HasAttribute("string")) return e.GetAttribute("string");
                foreach (var n in new[] { "value", "string" })
                {
                    var a = e.Attributes.OfType<XmlAttribute>().FirstOrDefault(x => x.LocalName == n);
                    if (a != null) return a.Value;
                }
            }
            return value.InnerText.Trim();
        }

        private static IEnumerable<XmlElement> Ancestors(XmlNode n)
        {
            for (var p = n.ParentNode as XmlElement; p != null; p = p.ParentNode as XmlElement) yield return p;
        }

        private static string Attr(XmlElement e, string localName) =>
            e.Attributes.OfType<XmlAttribute>().FirstOrDefault(a => a.LocalName == localName)?.Value;

        /// <summary>Diferenças entre o que o projeto tinha e o que veio do instrumento (por caminho do parâmetro).</summary>
        public static List<CompareItemDto> Diff(ParameterSetDto project, ParameterSetDto device)
        {
            var p = project.Items.ToDictionary(i => i.Path);
            var d = device.Items.ToDictionary(i => i.Path);
            return p.Keys.Concat(d.Keys.Where(k => !p.ContainsKey(k))).Select(k =>
            {
                p.TryGetValue(k, out var a);
                d.TryGetValue(k, out var b);
                var any = a ?? b;
                return new CompareItemDto
                {
                    Path = k, Name = any.Name, Group = any.Group, ProjectValue = a?.Value, DeviceValue = b?.Value,
                    Different = !string.Equals((a?.Value ?? "").Trim(), (b?.Value ?? "").Trim(), StringComparison.Ordinal),
                    Writable = any.Writable,
                };
            }).ToList();
        }

        /// <summary>statusFlag do documento DTMOnlineCompareSchema (ou "communicationError").</summary>
        public static string CompareVerdict(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return null;
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(xml);
                var cr = doc.GetElementsByTagName("*").OfType<XmlElement>().FirstOrDefault(e => e.LocalName == "CompareResult");
                if (cr != null) return Attr(cr, "statusFlag");
                var ce = doc.GetElementsByTagName("*").OfType<XmlElement>().FirstOrDefault(e => e.LocalName == "CommunicationError");
                return ce != null ? "communicationError:" + Attr(ce, "communicationError") : null;
            }
            catch { return null; }
        }
    }
}
