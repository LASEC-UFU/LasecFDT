using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using Jigfdt.Fdt100;
using LasecFDT.Core;
using LasecFDT.Fdt1.Frame;

namespace LasecFDT.Fdt1.Comm
{
    /// <summary>
    /// Canal de comunicação HART simulado, fornecido pelo próprio Frame (não é um DTM). Permite exercitar
    /// a comunicação aninhada sem hardware: ConnectRequest/TransactionRequest/DisconnectRequest com respostas
    /// assíncronas no formato FDTHARTCommunicationSchema (FDT 1.2).
    ///
    /// Os comandos são executados por <see cref="HartDevice"/> (o mesmo usado pelo simulador de fio).
    /// Nada aqui toca porta serial ou hardware.
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class HartSimulatedChannel : StandardOleMarshalObject, IFdtCommunication, IFdtChannel
    {
        public const string HartProtocolId = "036D1498-387B-11D4-86E1-00E0987270B9"; // CATID_FDT_BUS_HART (FDT 1.2.1 IDL)
        private readonly SynchronizationContext _ctx;
        private readonly Dictionary<string, IFdtCommunicationEvents> _connections = new Dictionary<string, IFdtCommunicationEvents>(StringComparer.OrdinalIgnoreCase);

        public HartSimulatedChannel(string ownerTag, HartDevice device)
        {
            OwnerTag = ownerTag;
            Device = device ?? new HartDevice();
            // As respostas são entregues depois que a chamada retorna (assíncrono, §4.5.3.2), na mesma thread STA.
            _ctx = SynchronizationContext.Current as WindowsFormsSynchronizationContext ?? new WindowsFormsSynchronizationContext();
        }

        public string OwnerTag { get; }
        public HartDevice Device { get; }
        public string ChannelPath => OwnerTag + "/HART0";
        public int ResponseDelayMs { get; set; } = 20;
        public List<string> Requests { get; } = new List<string>();

        // ---------------------------------------------------------------- IFdtChannel

        public string GetChannelPath() => ChannelPath;

        /// <summary>Canal puro de comunicação → FDTBasicChannelParameterSchema (recomendação de §4.5.1.1).</summary>
        public string GetChannelParameters(string parameterPath, string protocolId) =>
            "<?xml version=\"1.0\"?><FDT xmlns=\"x-schema:FDTBasicChannelParameterSchema.xml\" xmlns:fdt=\"x-schema:FDTDataTypesSchema.xml\">" +
            $"<FDTChannelType gatewayBusCategory=\"{HartProtocolId}\"><fdt:VersionInformation name=\"LasecFDT HART simulado\" vendor=\"LasecFDT\" version=\"0.1\"/></FDTChannelType>" +
            "<FDTBasicChannel fdt:tag=\"HART0\" fdt:id=\"HART0\"/></FDT>";

        public bool SetChannelParameters(string parameterPath, string protocolId, string xmlDocument) => false;

        // ---------------------------------------------------------------- IFdtCommunication

        /// <summary>DTMProtocolsSchema: um BusCategory HART.</summary>
        public string GetSupportedProtocols() =>
            "<?xml version=\"1.0\"?><FDT xmlns=\"x-schema:DTMProtocolsSchema.xml\" xmlns:fdt=\"x-schema:FDTDataTypesSchema.xml\">" +
            $"<fdt:BusCategories><fdt:BusCategory busCategory=\"{HartProtocolId}\" busCategoryName=\"HART\"/></fdt:BusCategories></FDT>";

        public bool ConnectRequest(IFdtCommunicationEvents callBack, string invokeId, string protocolId, string fieldbusFrame)
        {
            Requests.Add("Connect " + fieldbusFrame);
            if (callBack == null || !string.Equals(Guidish(protocolId), HartProtocolId, StringComparison.OrdinalIgnoreCase)) return false;
            XmlElement req;
            try { req = Find(fieldbusFrame, "ConnectRequest"); } catch { return false; }
            if (req == null) return false;

            string tag = req.GetAttribute("tag", "x-schema:FDTDataTypesSchema.xml"); // atributo fdt:tag
            var sa = req.GetElementsByTagName("*").OfType<XmlElement>().FirstOrDefault(e => e.LocalName == "ShortAddress");
            byte shortAddr = sa != null && byte.TryParse(sa.GetAttribute("shortAddress"), out var b) ? b : (byte)0;
            string commRef = Guid.NewGuid().ToString("D").ToUpperInvariant();

            string response;
            if (shortAddr != Device.PollingAddress)
                response = Error(tag, "noConnection", $"Nenhum instrumento simulado no endereço {shortAddr}");
            else
            {
                _connections[commRef] = callBack;
                response = Doc(
                    $"<ConnectResponse fdt:tag=\"{Esc(tag)}\" preambleCount=\"{Device.PreambleCount}\" primaryMaster=\"1\" communicationReference=\"{commRef}\">" +
                    $"<LongAddress manufacturerId=\"{Device.ManufacturerId}\" deviceTypeId=\"{Device.DeviceTypeId}\" address1=\"{(Device.DeviceId >> 16) & 0xFF}\" address2=\"{(Device.DeviceId >> 8) & 0xFF}\" address3=\"{Device.DeviceId & 0xFF}\"/>" +
                    $"<ShortAddress shortAddress=\"{shortAddr}\"/></ConnectResponse>");
            }
            Post(() =>
            {
                if (callBack is IFdtCommunicationEvents2 c2)
                    c2.OnConnectResponse2(invokeId, ParentInformation(), response);
                else
                    callBack.OnConnectResponse(invokeId, response);
            });
            return true;
        }

        public bool DisconnectRequest(string invokeId, string fieldbusFrame)
        {
            Requests.Add("Disconnect " + fieldbusFrame);
            var req = Find(fieldbusFrame, "DisconnectRequest");
            string commRef = req?.GetAttribute("communicationReference");
            if (commRef == null || !_connections.TryGetValue(commRef, out var cb)) return false;
            _connections.Remove(commRef);
            Post(() => cb.OnDisconnectResponse(invokeId, Doc($"<DisconnectResponse communicationReference=\"{commRef}\"/>")));
            return true;
        }

        public bool TransactionRequest(string invokeId, string fieldbusFrame)
        {
            Requests.Add("Transaction " + fieldbusFrame);
            var req = Find(fieldbusFrame, "DataExchangeRequest");
            string commRef = req?.GetAttribute("communicationReference");
            if (req == null || commRef == null || !_connections.TryGetValue(commRef, out var cb)) return false;
            int cmd = int.Parse(req.GetAttribute("commandNumber"), CultureInfo.InvariantCulture);

            var cd = req.GetElementsByTagName("*").OfType<XmlElement>().FirstOrDefault(e => e.LocalName == "CommunicationData");
            var (rc, devStatus, bytes) = Device.Execute(cmd, HartDevice.FromHex(cd?.GetAttribute("byteArray")));
            string data = bytes.Length > 0 ? HartDevice.ToHex(bytes) : null;
            string response = Doc(
                $"<DataExchangeResponse commandNumber=\"{cmd}\" communicationReference=\"{commRef}\">" +
                (data != null ? $"<fdt:CommunicationData byteArray=\"{data}\"/>" : "") +
                $"<Status deviceStatus=\"{devStatus}\"><CommandResponse value=\"{rc}\"/></Status></DataExchangeResponse>");
            Post(() => cb.OnTransactionResponse(invokeId, response));
            return true;
        }

        public void Abort(string fieldbusFrame)
        {
            Requests.Add("Abort " + fieldbusFrame);
            _connections.Clear();
        }

        public bool SequenceBegin(string fieldbusFrame) => true;
        public bool SequenceStart(string fieldbusFrame) => true;
        public bool SequenceEnd(string fieldbusFrame) => true;

        // ---------------------------------------------------------------- utilidades

        private void Post(Action a) => _ctx.Post(_ =>
        {
            Thread.Sleep(ResponseDelayMs);
            try { a(); } catch (Exception ex) { Requests.Add("Erro ao notificar DTM: " + ex.Message); }
        }, null);

        /// <summary>FDTConnectResponseSchema: versão FDT do componente pai.</summary>
        private static string ParentInformation() =>
            "<?xml version=\"1.0\"?><FDT xmlns=\"x-schema:FDTConnectResponseSchema.xml\" xmlns:fdtvers=\"x-schema:DTMInformationSchema.xml\">" +
            "<ParentVersion><fdtvers:FDTVersion major=\"1\" minor=\"2\" release=\"1\" build=\"0\"/></ParentVersion></FDT>";

        private static string Doc(string inner) =>
            "<?xml version=\"1.0\"?><FDT xmlns=\"x-schema:FDTHARTCommunicationSchema.xml\" xmlns:fdt=\"x-schema:FDTDataTypesSchema.xml\">" + inner + "</FDT>";

        private static string Error(string tag, string kind, string text) =>
            Doc($"<fdt:CommunicationError communicationError=\"{kind}\" tag=\"{Esc(tag)}\" descriptor=\"{Esc(text)}\"/>");

        private static XmlElement Find(string xml, string localName) =>
            FdtXml.Load(xml).GetElementsByTagName("*").OfType<XmlElement>().FirstOrDefault(e => e.LocalName == localName);

        private static string Guidish(string s) => (s ?? "").Trim().Trim('{', '}');
        private static string Esc(string s) => System.Security.SecurityElement.Escape(s ?? "");
    }
}
