using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jigfdt.Fdt100;
using LasecFDT.Fdt1.Comm;
using LasecFDT.Fdt1.Frame;
using Xunit;

namespace LasecFDT.Tests
{
    /// <summary>
    /// Lasec HART Communication DTM (Comm DTM .NET do usuário, C:\Source Local\LasecHartCommDTM) hospedado pelo
    /// LasecFDT. Sempre contra instrumentos SIMULADOS: porta virtual com0com ou servidor HART-IP local (127.0.0.1).
    /// </summary>
    public class LasecHartCommDtmTests
    {
        public const string ProgId = "JosueLab.LasecHartCommDTM.CommDtm";
        private const string TT301 = "Smar.TT301DTM.1";
        private static readonly string Schemas = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FDTSchemas");
        private static readonly TimeSpan T = TimeSpan.FromSeconds(10);
        private const string Hart = HartSimulatedChannel.HartProtocolId;
        private const string Ns = "xmlns=\"x-schema:FDTHARTCommunicationSchema.xml\" xmlns:fdt=\"x-schema:FDTDataTypesSchema.xml\"";

        private static string Connect(string tag, byte polling, HartDevice longAddr = null) =>
            $"<?xml version=\"1.0\"?><FDT {Ns}><ConnectRequest fdt:tag=\"{tag}\">" +
            (longAddr == null ? "" : $"<LongAddress manufacturerId=\"{longAddr.ManufacturerId & 0x3F}\" deviceTypeId=\"{longAddr.DeviceTypeId}\" address1=\"{(longAddr.DeviceId >> 16) & 0xFF}\" address2=\"{(longAddr.DeviceId >> 8) & 0xFF}\" address3=\"{longAddr.DeviceId & 0xFF}\"/>") +
            $"<ShortAddress shortAddress=\"{polling}\"/></ConnectRequest></FDT>";

        private static string Exchange(int cmd, string commRef) =>
            $"<?xml version=\"1.0\"?><FDT {Ns}><DataExchangeRequest commandNumber=\"{cmd}\" communicationReference=\"{commRef}\"/></FDT>";

        private static string Disconnect(string commRef) =>
            $"<?xml version=\"1.0\"?><FDT {Ns}><DisconnectRequest communicationReference=\"{commRef}\"/></FDT>";

        private static string Wait(FakeClient c, string invokeId, int seconds = 15)
        {
            Assert.True(DtmSession.PumpUntil(() => c.Received.Any(r => r.InvokeId == invokeId && r.Method != "parentInformation"), TimeSpan.FromSeconds(seconds)),
                "sem resposta para " + invokeId);
            return c.Received.First(r => r.InvokeId == invokeId && r.Method != "parentInformation").Xml;
        }

        private static string CommRef(string connectResponse) =>
            Regex.Match(connectResponse, "communicationReference=\"([^\"]+)\"").Groups[1].Value;

        [DtmFact(ProgId)]
        public void DocumentosDoDtm_SaoValidosContraOsSchemasXdr_EPersistenciaFazIdaEVolta() => Sta.Run(() =>
        {
            var frame = new FrameContainer(Schemas);
            var v = new XdrValidator(Schemas);
            var s = DtmLifecycle.CreateWithProperties(frame, DtmLifecycle.ResolveClsid(ProgId),
                new Dictionary<string, string> { ["Port"] = "COM77", ["PreambleCount"] = "7", ["Interface"] = "HartIpTcp" });
            try
            {
                Assert.True(s.Supports("IPersistPropertyBag") && s.Supports("IPersistStreamInit") && s.Supports("IDtmChannel") && s.Supports("IDtm2"));
                var docs = new Dictionary<string, string>
                {
                    ["GetInformation"] = ((IDtmInformation)s.ComObject).GetInformation(),
                    ["GetFunctions"] = s.Dtm.GetFunctions(FdtXml.OperationPhase()),
                    ["GetParameters"] = ((IDtmParameter)s.ComObject).GetParameters(""),
                };
                var ch = s.GetChannels(Hart).Single();
                Assert.True(ch.IsCommunicationChannel && ch.HasSubTopology);
                Assert.Equal(s.SystemTag + "/HART", ch.Path);                     // §4.5.1.2
                docs["GetChannelParameters"] = ch.ParametersXml;
                docs["GetSupportedProtocols"] = ch.SupportedProtocolsXml;
                foreach (var d in docs) Assert.True(v.Validate(d.Value) == null, d.Key + ": " + v.Validate(d.Value) + "\n" + d.Value);

                // Data set alterado por propriedades e devolvido igual (IPersistPropertyBag pela vtable do CCW).
                var ds = s.PersistSave();
                Assert.Equal("COM77", ds.Properties.Single(p => p.Name == "Port").Value);
                Assert.Equal("7", ds.Properties.Single(p => p.Name == "PreambleCount").Value);
                Assert.Equal("HartIpTcp", ds.Properties.Single(p => p.Name == "Interface").Value);

                // UI: GUID do controle ActiveX para as funções com tela.
                Assert.Equal("B4B6B3E7-639D-460B-B9A0-6C7F7EB20020", s.GetActiveXGuid(FdtXml.FunctionCall("1")));
                Assert.Null(s.GetActiveXGuid(FdtXml.FunctionCall("999")));
            }
            finally { Assert.True(s.Release(T)); }
        });

        [DtmFact(ProgId)]
        public void HartIp_GatewayWirelessHart_ConectaSubDispositivoComRespostaAtrasada_EVarreduraListaTudo() => Sta.Run(() =>
        {
            var gw = new HartDevice { ManufacturerId = 0x26, DeviceTypeId = 0x99, DeviceId = 0x000100, Tag = "GW-01" };
            var subs = new[]
            {
                new HartDevice { ManufacturerId = 0x26, DeviceTypeId = 0x10, DeviceId = 0x0A0001, Tag = "TT-201", UniversalRevision = 7 },
                new HartDevice { ManufacturerId = 0x26, DeviceTypeId = 0x11, DeviceId = 0x0A0002, Tag = "PT-202", UniversalRevision = 7 },
            };
            using (var hip = new HartIpSimulator(0, false, gw, subs) { DelayedResponses = true })
            {
                hip.Start();
                var frame = new FrameContainer(Schemas);
                var v = new XdrValidator(Schemas);
                var s = DtmLifecycle.CreateWithProperties(frame, DtmLifecycle.ResolveClsid(ProgId), new Dictionary<string, string>
                {
                    ["Interface"] = "HartIpUdp", ["HostAddress"] = "127.0.0.1", ["HostPort"] = hip.Port.ToString(),
                    ["ResponseTimeoutMs"] = "1000", ["ScanStop"] = "2",
                });
                try
                {
                    Assert.True(s.SetCommunicationNull());
                    var ch = s.GetChannels(Hart).Single();
                    var comm = (IFdtCommunication)ch.Channel;
                    var client = new FakeClient();

                    // Sub-dispositivo WirelessHART pelo endereço longo (o gateway responde 33 antes; o DTM reenvia).
                    Assert.True(comm.ConnectRequest(client, "c1", Hart, Connect("TT-201", 0, subs[0])));
                    string cr = Wait(client, "c1");
                    Assert.True(v.Validate(cr) == null, v.Validate(cr) + "\n" + cr);
                    Assert.Contains("<ConnectResponse", cr);
                    Assert.Contains("deviceTypeId=\"16\"", cr);
                    Assert.Contains("address3=\"1\"", cr);
                    Assert.Contains(client.Received, r => r.Method == "parentInformation" && r.Xml.Contains("release="));

                    string commRef = CommRef(cr);
                    Assert.True(comm.TransactionRequest("t1", Exchange(13, commRef)));
                    string tr = Wait(client, "t1");
                    Assert.True(v.Validate(tr) == null, v.Validate(tr) + "\n" + tr);
                    Assert.Contains("<CommandResponse value=\"0\"/>", tr);
                    Assert.Contains("byteArray=", tr);

                    Assert.True(comm.DisconnectRequest("d1", Disconnect(commRef)));
                    Assert.Contains(client.Received, r => r.InvokeId == "d1" && r.Method == "OnDisconnectResponse"); // síncrono, como a CodeWrights

                    // Instrumento que não existe: erro de comunicação no lugar da resposta.
                    Assert.True(comm.ConnectRequest(client, "c2", Hart, Connect("X", 2)));
                    string err = Wait(client, "c2");
                    Assert.True(v.Validate(err) == null, v.Validate(err));
                    Assert.Contains("communicationError=\"noConnection\"", err);

                    // Varredura do Frame (FDT 1.2): gateway no polling 0 + sub-dispositivos (comandos 74 e 84).
                    Assert.True(((IFdtChannelSubTopology)ch.Channel).ScanRequest("scan1"));
                    Assert.True(DtmSession.PumpUntil(() => frame.ScanResponses.ContainsKey("scan1"), TimeSpan.FromSeconds(60)), "sem OnScanResponse");
                    string scan = frame.ScanResponses["scan1"];
                    Assert.True(v.Validate(scan) == null, v.Validate(scan) + "\n" + scan);
                    Assert.Equal(3, Regex.Matches(scan, "<fdthartdevice:HARTDevice").Count);
                    Assert.Contains("fdthart:shortAddress=\"0\" fdt:tag=\"GW-01\"", scan);
                    Assert.Contains("fdt:tag=\"TT-201\"", scan);
                    Assert.Contains("fdt:tag=\"PT-202\"", scan);
                    Assert.Contains(s.Events, e => e.Method == "OnProgress");
                    Assert.Contains(s.Events, e => e.Method == "OnOnlineStateChanged");
                    Assert.True(hip.SessionsOpened >= 1);
                }
                finally { Assert.True(s.Release(T)); }
            }
        });

        [CommChainFact(ProgId, TT301)]
        public void CadeiaReal_ModemSerialNaPortaVirtual_TT301ConversaPeloDtmDoUsuario() => Sta.Run(() =>
        {
            var frame = new FrameContainer(Schemas);
            var v = new XdrValidator(Schemas);
            DtmSession child = null, parent = null;
            try
            {
                child = DtmLifecycle.CreateConfigured(frame, DtmLifecycle.ResolveClsid(TT301));
                using (var wire = new HartWireSimulator(VirtualPortPairFactAttribute.Slave, HartDevice.FromDeviceType(child.DeviceTypeXml)))
                {
                    wire.Start();
                    parent = DtmLifecycle.CreateWithProperties(frame, DtmLifecycle.ResolveClsid(ProgId),
                        new Dictionary<string, string> { ["Port"] = VirtualPortPairFactAttribute.Master });
                    Assert.True(parent.SetCommunicationNull());
                    var ch = parent.GetChannels(Hart).Single(c => c.IsCommunicationChannel);
                    var link = frame.ConnectToChannel(parent, ch, child, CommPolicy.ReadOnly);
                    Assert.True(link.CommunicationAccepted);
                    Assert.True(DtmSession.PumpUntil(() => child.Events.Any(e => e.Method == "OnOnlineStateChanged" && e.Detail == "online=True"), T),
                        "TT301 não ficou online: " + string.Join(" || ", link.Guard.Log.Select(r => r.Method)));
                    Assert.True(wire.FramesAnswered >= 1);
                    foreach (var r in link.Guard.Log.Where(r => r.Direction == "canal→DTM"))
                        Assert.True(v.Validate(r.Xml) == null, r.Method + ": " + v.Validate(r.Xml));

                    Assert.True(child.Release(T));     // DisconnectRequest → OnDisconnectResponse → OnRemoveChild
                    Assert.Contains(parent.Steps, x => x.Step.StartsWith("IFdtChannelSubTopology::OnRemoveChild") && x.Ok);
                    Assert.True(parent.Release(T));
                }
            }
            finally
            {
                if (child?.ComObject != null) child.Release(T);
                if (parent?.ComObject != null) parent.Release(T);
            }
        });
    }
}
