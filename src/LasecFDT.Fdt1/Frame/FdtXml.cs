using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Xml;

namespace LasecFDT.Fdt1.Frame
{
    /// <summary>
    /// Documentos XML que o Frame envia aos DTMs (FDT 1.2/1.2.1 usa schemas XDR "x-schema:").
    /// Todos seguem os exemplos oficiais em specs/FDT-1.2.1/spec/XML/Examples.
    /// </summary>
    public static class FdtXml
    {
        public const string NsDataTypes = "x-schema:FDTDataTypesSchema.xml";
        public const string NsInformation = "x-schema:DTMInformationSchema.xml";

        /// <summary>frameInfo de IDtm2::Environment2 (DTMEnvironmentSchema, exemplo DTMEnvironmentInstance.xml).</summary>
        public static string FrameInfo(int major = 1, int minor = 2, int release = 1, int build = 0) =>
            "<?xml version=\"1.0\"?>" +
            "<FDT xmlns=\"x-schema:DTMEnvironmentSchema.xml\" xmlns:fdtvers=\"x-schema:DTMInformationSchema.xml\" " +
            "xmlns:fdt=\"x-schema:FDTDataTypesSchema.xml\" xmlns:fdtappid=\"x-schema:FDTApplicationIdSchema.xml\">" +
            $"<FrameVersion><fdtvers:FDTVersion major=\"{major}\" minor=\"{minor}\" release=\"{release}\" build=\"{build}\"/></FrameVersion></FDT>";

        /// <summary>userInfo de IDtm::Config (FDTUserInformationSchema). userLevel: observer|operator|maintenance|planningEngineer.</summary>
        public static string UserInfo(string projectName, string userName, string userLevel, bool administrator = false, bool oemService = false) =>
            "<?xml version=\"1.0\"?>" +
            "<FDT xmlns=\"x-schema:FDTUserInformationSchema.xml\" xmlns:fdt=\"x-schema:FDTDataTypesSchema.xml\">" +
            $"<FDTUserInformation projectName=\"{E(projectName)}\" userName=\"{E(userName)}\" userLevel=\"{E(userLevel)}\" " +
            $"administrator=\"{(administrator ? 1 : 0)}\" oemService=\"{(oemService ? 1 : 0)}\" " +
            $"loginTime=\"{DateTime.Now:yyyy-MM-ddTHH:mm:ss}\" loginLocation=\"{E(Environment.MachineName)}\"/></FDT>";

        /// <summary>operationState de IDtm::GetFunctions (FDTOperationPhaseSchema).</summary>
        public static string OperationPhase(string phase = "notSupported") =>
            $"<?xml version=\"1.0\"?><FDT xmlns=\"x-schema:FDTOperationPhaseSchema.xml\" operationPhase=\"{E(phase)}\"/>";

        /// <summary>functionCall (DTMFunctionCallSchema, exemplo DTMFunctionCallInstance.xml).</summary>
        public static string FunctionCall(string functionId, string applicationId = null, string operationPhase = null)
        {
            string ops = operationPhase != null ? $" ops:operationPhase=\"{E(operationPhase)}\"" : "";
            string app = applicationId != null ? $"<appId:ApplicationId applicationId=\"{E(applicationId)}\"/>" : "";
            return "<?xml version=\"1.0\"?>" +
                   "<FDT xmlns=\"x-schema:DTMFunctionCallSchema.xml\" xmlns:func=\"x-schema:DTMFunctionsSchema.xml\" " +
                   "xmlns:appId=\"x-schema:FDTApplicationIdSchema.xml\" xmlns:ops=\"x-schema:FDTOperationPhaseSchema.xml\">" +
                   $"<FDTFunctionCall func:functionId=\"{E(functionId)}\"{ops}>{app}</FDTFunctionCall></FDT>";
        }

        /// <summary>systemGuiLabel de IDtm2::SetSystemGuiLabel (DTMSystemGuiLabelSchema).</summary>
        public static string SystemGuiLabel(string label) =>
            "<?xml version=\"1.0\"?><FDT xmlns=\"x-schema:DTMSystemGuiLabelSchema.xml\" xmlns:fdt=\"x-schema:FDTDataTypesSchema.xml\">" +
            $"<SystemGuiLabel fdt:systemGuiLabel=\"{E(label)}\"/></FDT>";

        /// <summary>
        /// deviceType de IDtm::InitNew (DTMInitSchema): envolve um elemento fdt:DtmDeviceType retornado
        /// por IDtmInformation::GetInformation, preservando-o integralmente.
        /// </summary>
        public static string InitDeviceType(string dtmDeviceTypeOuterXml) =>
            "<?xml version=\"1.0\"?>" +
            "<FDT xmlns=\"x-schema:DTMInitSchema.xml\" xmlns:fdt=\"x-schema:FDTDataTypesSchema.xml\" xmlns:dtminfo=\"x-schema:DTMInformationSchema.xml\">" +
            dtmDeviceTypeOuterXml + "</FDT>";

        private static string E(string s) => SecurityElement.Escape(s ?? "");

        // ------------------------------------------------------------ parsing

        public sealed class DeviceTypeInfo
        {
            public int Index { get; set; }
            public string Name { get; set; }
            public string Vendor { get; set; }
            public string Version { get; set; }
            public string Date { get; set; }
            public List<string> BusCategories { get; set; } = new List<string>();
            public List<string> Variants { get; set; } = new List<string>();
            /// <summary>Identidade do instrumento declarada pelo DTM (atributos manufacturerId/deviceTypeId do
            /// DtmDeviceType ou de um descendente, FDTDataTypesSchema). Ausente em DTMs que não a declaram.</summary>
            public int? ManufacturerId { get; set; }
            public int? DeviceTypeId { get; set; }
            /// <summary>XML do elemento fdt:DtmDeviceType, pronto para InitDeviceType().</summary>
            public string OuterXml { get; set; }
        }

        public sealed class DtmInfo
        {
            public string FdtVersion { get; set; }
            public string Name { get; set; }
            public string Vendor { get; set; }
            public string Version { get; set; }
            public string Date { get; set; }
            public List<DeviceTypeInfo> DeviceTypes { get; set; } = new List<DeviceTypeInfo>();
        }

        /// <summary>Interpreta o documento de IDtmInformation::GetInformation (DTMInformationSchema).</summary>
        public static DtmInfo ParseInformation(string xml)
        {
            var doc = Load(xml);
            var info = new DtmInfo();
            var dtmInfo = doc.DocumentElement?.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.LocalName == "DtmInfo");
            if (dtmInfo == null) return info;
            var v = dtmInfo.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.LocalName == "FDTVersion");
            if (v != null)
                info.FdtVersion = string.Join(".", new[] { "major", "minor", "release", "build" }
                    .Select(a => v.GetAttribute(a)).TakeWhile(x => !string.IsNullOrEmpty(x)));
            var vi = dtmInfo.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.LocalName == "VersionInformation");
            if (vi != null) { info.Name = Attr(vi, "name"); info.Vendor = Attr(vi, "vendor"); info.Version = Attr(vi, "version"); info.Date = Attr(vi, "date"); }

            int idx = 0;
            foreach (XmlElement dt in doc.GetElementsByTagName("*").OfType<XmlElement>().Where(e => e.LocalName == "DtmDeviceType"))
            {
                var d = new DeviceTypeInfo { Index = idx++, OuterXml = dt.OuterXml };
                var dvi = dt.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.LocalName == "VersionInformation");
                if (dvi != null) { d.Name = Attr(dvi, "name"); d.Vendor = Attr(dvi, "vendor"); d.Version = Attr(dvi, "version"); d.Date = Attr(dvi, "date"); }
                foreach (var bc in dt.GetElementsByTagName("*").OfType<XmlElement>().Where(e => e.LocalName == "BusCategory"))
                    d.BusCategories.Add(Attr(bc, "busCategoryName") ?? Attr(bc, "busCategory"));
                foreach (var tv in dt.GetElementsByTagName("*").OfType<XmlElement>().Where(e => e.LocalName == "DeviceTypeVariant"))
                    d.Variants.Add(Attr(tv, "deviceTypeVariant"));
                foreach (var el in new[] { dt }.Concat(dt.GetElementsByTagName("*").OfType<XmlElement>()))
                {
                    if (d.ManufacturerId == null) d.ManufacturerId = IntAttr(el, "manufacturerId");
                    if (d.DeviceTypeId == null) d.DeviceTypeId = IntAttr(el, "deviceTypeId");
                }
                info.DeviceTypes.Add(d);
            }
            return info;
        }

        /// <summary>Atributo inteiro pelo nome local, sem diferenciar maiúsculas (aceita prefixo de namespace e hexadecimal 0x..).</summary>
        public static int? IntAttr(XmlElement e, string localName)
        {
            foreach (XmlAttribute a in e.Attributes)
            {
                if (!string.Equals(a.LocalName, localName, StringComparison.OrdinalIgnoreCase)) continue;
                string v = a.Value.Trim();
                if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && int.TryParse(v.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out int h)) return h;
                if (int.TryParse(v, out int n)) return n;
            }
            return null;
        }

        public sealed class FunctionInfo
        {
            public string FunctionId { get; set; }
            public string Name { get; set; }
            public string ApplicationId { get; set; }
            public bool IsStandard { get; set; }
            public bool Enabled { get; set; } = true;
            public bool Hidden { get; set; }
            public string Path { get; set; }
        }

        /// <summary>Interpreta o documento de IDtm::GetFunctions (DTMFunctionsSchema).</summary>
        public static List<FunctionInfo> ParseFunctions(string xml, out string defaultFunctionId)
        {
            var list = new List<FunctionInfo>();
            var doc = Load(xml);
            defaultFunctionId = doc.DocumentElement?.GetAttribute("defaultFunctionId");
            if (string.IsNullOrEmpty(defaultFunctionId)) defaultFunctionId = null;
            foreach (var e in doc.GetElementsByTagName("*").OfType<XmlElement>()
                                 .Where(x => x.LocalName == "StandardFunction" || x.LocalName == "Function"))
            {
                string fid = Attr(e, "functionId");
                if (fid == null) continue;
                var status = e.ChildNodes.OfType<XmlElement>().FirstOrDefault(c => c.LocalName == "Status");
                var app = e.GetElementsByTagName("*").OfType<XmlElement>().FirstOrDefault(c => c.LocalName == "ApplicationId");
                list.Add(new FunctionInfo
                {
                    FunctionId = fid,
                    Name = Attr(e, "name") ?? Attr(e, "label"),
                    ApplicationId = app != null ? Attr(app, "applicationId")?.Trim() : null,
                    IsStandard = e.LocalName == "StandardFunction",
                    Enabled = status == null || Attr(status, "enabled") != "0",
                    Hidden = status != null && Attr(status, "hidden") == "1",
                    Path = PathOf(e),
                });
            }
            return list;
        }

        /// <summary>Interpreta FDTUserMessage (IFdtDialog::UserDialog) e gera a resposta.</summary>
        public static XmlDocument Load(string xml)
        {
            var doc = new XmlDocument { XmlResolver = null }; // não tentar resolver x-schema:
            doc.LoadXml(xml);
            return doc;
        }

        private static string Attr(XmlElement e, string localName)
        {
            foreach (XmlAttribute a in e.Attributes)
                if (a.LocalName == localName) return a.Value;
            return null;
        }

        private static string PathOf(XmlElement e)
        {
            var parts = new List<string>();
            for (var p = e.ParentNode as XmlElement; p != null && p.LocalName != "FDT"; p = p.ParentNode as XmlElement)
                parts.Insert(0, Attr(p, "label") ?? Attr(p, "name") ?? p.LocalName);
            return string.Join(" / ", parts);
        }
    }
}
