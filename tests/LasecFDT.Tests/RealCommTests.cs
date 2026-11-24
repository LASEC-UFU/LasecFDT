using System;
using System.IO;
using System.Linq;
using LasecFDT.Fdt1.Comm;
using LasecFDT.Fdt1.Frame;
using Xunit;

namespace LasecFDT.Tests
{
    /// <summary>Roda só se os DTMs informados estiverem instalados e o par virtual com0com existir.</summary>
    public sealed class CommChainFactAttribute : FactAttribute
    {
        public CommChainFactAttribute(params string[] progIds)
        {
            foreach (var p in progIds)
                if (Type.GetTypeFromProgID(p, false) == null) { Skip = $"DTM '{p}' não instalado."; return; }
            var ports = new VirtualPortPairFactAttribute();
            if (ports.Skip != null) Skip = ports.Skip;
        }
    }

    /// <summary>
    /// Comunicação aninhada com DTMs reais — sempre contra instrumento SIMULADO (canal do Frame ou porta virtual).
    /// Nenhum teste aqui acessa hardware ou instrumento real.
    /// </summary>
    public class RealCommTests
    {
        private static readonly string Schemas = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FDTSchemas");
        private static readonly TimeSpan T = TimeSpan.FromSeconds(10);
        private const string TT301 = "Smar.TT301DTM.1", CW = "CWHartFdt.clsDTM";

        private static bool WentOnline(DtmSession s) =>
            DtmSession.PumpUntil(() => s.Events.Any(e => e.Method == "OnOnlineStateChanged" && e.Detail == "online=True"), TimeSpan.FromSeconds(10));

        [DtmFact(TT301)]
        public void TT301_CanalSimuladoDoFrame_ConectaComXmlValidoELiberaNaOrdemDaSpec() => Sta.Run(() =>
        {
            var frame = new FrameContainer(Schemas);
            var v = new XdrValidator(Schemas);
            var s = DtmLifecycle.CreateConfigured(frame, DtmLifecycle.ResolveClsid(TT301));
            var sim = new HartSimulatedChannel("SIM-HART", HartDevice.FromDeviceType(s.DeviceTypeXml));
            Assert.Equal(62, sim.Device.ManufacturerId); // do DtmDeviceType do TT301

            var link = frame.ConnectToSimulator(s, sim, CommPolicy.Full);
            Assert.True(link.CommunicationAccepted);
            Assert.Equal(DtmState.CommunicationSet, s.State);
            Assert.True(WentOnline(s), "TT301 não ficou online");
            Assert.Contains("systemTag=\"SIM-HART\"", frame.GetParentNodes(s.SystemTag));
            Assert.Contains(link.Guard.Log, r => r.Method == "ConnectRequest" && r.Forwarded);
            foreach (var r in link.Guard.Log.Where(r => r.Direction == "canal→DTM"))
                Assert.True(v.Validate(r.Xml) == null, r.Method + ": " + v.Validate(r.Xml));
            Assert.DoesNotContain(s.Events, e => e.Method == "OnErrorMessage");

            Assert.True(frame.Disconnect(s.SystemTag, T));          // PrepareToReleaseCommunication → evento → ReleaseCommunication
            Assert.Equal(DtmState.Configured, s.State);
            Assert.Contains(s.Events, e => e.Method == "OnPreparedToReleaseCommunication");
            Assert.True(s.Release(T));
        });

        [DtmFact(TT301)]
        public void TT301_PoliticaBlocked_NadaChegaAoCanal() => Sta.Run(() =>
        {
            var frame = new FrameContainer(Schemas);
            var s = DtmLifecycle.CreateConfigured(frame, DtmLifecycle.ResolveClsid(TT301));
            var sim = new HartSimulatedChannel("SIM-HART", HartDevice.FromDeviceType(s.DeviceTypeXml));
            var link = frame.ConnectToSimulator(s, sim, CommPolicy.Blocked);
            Assert.True(link.CommunicationAccepted);
            DtmSession.PumpUntil(() => false, TimeSpan.FromSeconds(2));
            Assert.Contains(link.Guard.Log, r => r.Method == "ConnectRequest" && !r.Forwarded); // o DTM tentou
            Assert.Empty(sim.Requests);                                                         // e nada chegou
            Assert.True(s.Release(T)); // Release desfaz a ligação antes (ReleaseCommunication)
            Assert.Empty(frame.Links);
        });

        [CommChainFact(TT301, CW)]
        public void CadeiaReal_CodeWrightsNaPortaVirtual_TT301ConversaComInstrumentoSimulado() => Sta.Run(() =>
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
                    parent = DtmLifecycle.CreateWithProperties(frame, DtmLifecycle.ResolveClsid(CW),
                        new System.Collections.Generic.Dictionary<string, string> { ["Port"] = VirtualPortPairFactAttribute.Master });
                    Assert.True(parent.SetCommunicationNull(), "Comm DTM raiz recusou SetCommunication(NULL): " +
                        string.Join(" | ", parent.Events.Where(e => e.Method == "OnErrorMessage").Select(e => e.Detail)) +
                        " | data set Port=" + parent.Steps.FirstOrDefault(x => x.Step.Contains("Load"))?.Result);

                    var ch = parent.GetChannels(HartSimulatedChannel.HartProtocolId).Single(c => c.IsCommunicationChannel);
                    Assert.StartsWith(parent.SystemTag + "/", ch.Path); // §4.5.1.2: <systemTag>/<id>
                    var link = frame.ConnectToChannel(parent, ch, child, CommPolicy.ReadOnly);
                    Assert.NotNull(link);
                    Assert.True(link.CommunicationAccepted);
                    Assert.Contains($"systemTag=\"{child.SystemTag}\"", frame.GetChildNodes(parent.SystemTag, ch.Path));

                    Assert.True(WentOnline(child), "TT301 não ficou online pela cadeia real. Guarda: " +
                        string.Join(" || ", link.Guard.Log.Select(r => r.Method + (r.Forwarded ? "" : " [recusado]"))) +
                        $" | Simulador: recebidos={wire.FramesReceived} respondidos={wire.FramesAnswered} " + string.Join(" | ", wire.Device.Log) +
                        " | Eventos filho: " + string.Join(" | ", child.Events.Select(e => e.Method + " " + e.Detail)) +
                        " | Eventos pai: " + string.Join(" | ", parent.Events.Select(e => e.Method + " " + e.Detail)));
                    Assert.True(wire.FramesAnswered >= 1, "o simulador de fio não recebeu quadros");
                    Assert.Equal(0, wire.Device.Log.Count(l => !l.StartsWith("cmd 0 ") && !HartCommands.IsKnownRead(int.Parse(l.Split(' ')[1]))));
                    foreach (var r in link.Guard.Log.Where(r => r.Direction == "canal→DTM"))
                        Assert.True(v.Validate(r.Xml) == null, r.Method + ": " + v.Validate(r.Xml));

                    Assert.True(child.Release(T));   // desliga do canal (ReleaseCommunication + OnRemoveChild) e libera
                    Assert.Empty(frame.Links);
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
