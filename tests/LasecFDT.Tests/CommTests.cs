using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jigfdt.Fdt100;
using LasecFDT.Fdt1.Comm;
using LasecFDT.Fdt1.Frame;
using Xunit;

namespace LasecFDT.Tests
{
    /// <summary>Cliente de comunicação falso: guarda as respostas recebidas.</summary>
    internal sealed class FakeClient : IFdtCommunicationEvents, IFdtCommunicationEvents2
    {
        public readonly List<(string Method, string InvokeId, string Xml)> Received = new List<(string, string, string)>();
        public void OnAbort(string r) => Received.Add(("OnAbort", null, r));
        public void OnConnectResponse(string i, string x) => Received.Add(("OnConnectResponse", i, x));
        public void OnDisconnectResponse(string i, string x) => Received.Add(("OnDisconnectResponse", i, x));
        public void OnTransactionResponse(string i, string x) => Received.Add(("OnTransactionResponse", i, x));
        public void OnConnectResponse2(string i, string p, string x) { Received.Add(("OnConnectResponse2", i, x)); Received.Add(("parentInformation", i, p)); }
    }

    /// <summary>Canal falso que só conta o que chegou até ele (para testar o guarda).</summary>
    internal sealed class CountingChannel : IFdtCommunication
    {
        public readonly List<string> Calls = new List<string>();
        public void Abort(string f) => Calls.Add("Abort");
        public bool ConnectRequest(IFdtCommunicationEvents cb, string i, string p, string f) { Calls.Add("Connect"); return true; }
        public bool DisconnectRequest(string i, string f) { Calls.Add("Disconnect"); return true; }
        public bool TransactionRequest(string i, string f) { Calls.Add("Transaction " + HartCommands.CommandNumberOf(f)); return true; }
        public string GetSupportedProtocols() => "<FDT/>";
        public bool SequenceBegin(string f) => true;
        public bool SequenceStart(string f) => true;
        public bool SequenceEnd(string f) => true;
    }

    /// <summary>Fato que só roda se as duas pontas do par virtual com0com existirem.</summary>
    public sealed class VirtualPortPairFactAttribute : FactAttribute
    {
        /// <summary>
        /// Par com0com dos testes: COM20 ↔ CNCB0 por padrão. Num servidor em que esse par está em uso (bancadas),
        /// escolha outro com LASECFDT_TEST_PORTS=COMx,COMy (ponta do Frame, ponta do simulador).
        /// </summary>
        public static readonly string Master = Port(0, "COM20"), Slave = Port(1, "CNCB0");
        private static string Port(int index, string fallback)
        {
            var parts = (Environment.GetEnvironmentVariable("LASECFDT_TEST_PORTS") ?? "").Split(',');
            return parts.Length == 2 && parts[index].Trim().Length > 0 ? parts[index].Trim() : fallback;
        }
        public VirtualPortPairFactAttribute()
        {
            using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM"))
            {
                var names = k?.GetValueNames().Select(n => k.GetValue(n) as string).ToList() ?? new List<string>();
                if (!names.Contains(Master) || !names.Contains(Slave)) Skip = $"Par virtual {Master}↔{Slave} (com0com) ausente.";
            }
        }
    }

    public class CommTests
    {
        private static readonly string Schemas = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FDTSchemas");
        private const string Ns = "xmlns=\"x-schema:FDTHARTCommunicationSchema.xml\" xmlns:fdt=\"x-schema:FDTDataTypesSchema.xml\"";
        private const string ConnectReq = "<?xml version=\"1.0\"?><FDT " + Ns + "><ConnectRequest fdt:tag=\"PT-101\"><ShortAddress shortAddress=\"0\"/></ConnectRequest></FDT>";
        private static string Exchange(int cmd, string commRef) =>
            $"<?xml version=\"1.0\"?><FDT {Ns}><DataExchangeRequest commandNumber=\"{cmd}\" communicationReference=\"{commRef}\"/></FDT>";

        // ------------------------------------------------------------ validação XDR (MSXML)

        [Fact]
        public void XdrValidator_RejeitaAtributoTagSemPrefixo_ErroVistoNoTT301()
        {
            var v = new XdrValidator(Schemas);
            string bad = "<?xml version=\"1.0\"?><FDT " + Ns + "><ConnectResponse tag=\"\" preambleCount=\"5\" primaryMaster=\"1\" communicationReference=\"A6DF2A36-BB91-4A22-835C-4C6D99CC4335\"><ShortAddress shortAddress=\"0\"/></ConnectResponse></FDT>";
            string err = v.Validate(bad);
            Assert.NotNull(err);
            Assert.StartsWith("0xC00CE015", err, StringComparison.OrdinalIgnoreCase); // mesmo código reportado pelo DTM Smar
            Assert.Null(v.Validate(bad.Replace(" tag=", " fdt:tag=")));
        }

        [Fact]
        public void DocumentosDoFrame_SaoValidosContraOsSchemasXdr()
        {
            var v = new XdrValidator(Schemas);
            var frame = new FrameContainer(Schemas);
            var docs = new Dictionary<string, string>
            {
                ["UserInfo"] = FdtXml.UserInfo("P", "u", "maintenance"),
                ["OperationPhase"] = FdtXml.OperationPhase(),
                ["FunctionCall"] = FdtXml.FunctionCall("1", "fdtOfflineParameterize"),
                ["GetParentNodes(vazio)"] = frame.GetParentNodes("X"),
                ["GetChildNodes(vazio)"] = frame.GetChildNodes("X", "X/1"),
            };
            var sim = new HartSimulatedChannel("SIM", new HartDevice { ManufacturerId = 62, DeviceTypeId = 2 });
            docs["GetSupportedProtocols"] = sim.GetSupportedProtocols();
            docs["GetChannelParameters"] = sim.GetChannelParameters("", HartSimulatedChannel.HartProtocolId);
            foreach (var kv in docs) Assert.True(v.Validate(kv.Value) == null, kv.Key + ": " + v.Validate(kv.Value));
        }

        // ------------------------------------------------------------ guarda (política)

        [Fact]
        public void Guarda_Blocked_NadaChegaAoCanal()
        {
            var inner = new CountingChannel();
            var g = new CommunicationGuard(inner, CommPolicy.Blocked, "T");
            Assert.False(g.ConnectRequest(new FakeClient(), "1", HartSimulatedChannel.HartProtocolId, ConnectReq));
            Assert.False(g.TransactionRequest("2", Exchange(0, "R")));
            Assert.False(g.SequenceBegin("<FDT/>"));
            g.Abort("<FDT/>");
            Assert.Empty(inner.Calls);
            Assert.Equal(4, g.Log.Count(r => !r.Forwarded));
        }

        [Fact]
        public void Guarda_ReadOnly_SoRepassaLeiturasConhecidas()
        {
            var inner = new CountingChannel();
            var g = new CommunicationGuard(inner, CommPolicy.ReadOnly, "T");
            Assert.True(g.ConnectRequest(new FakeClient(), "1", HartSimulatedChannel.HartProtocolId, ConnectReq));
            Assert.True(g.TransactionRequest("2", Exchange(0, "R")));    // Read Unique Identifier
            Assert.True(g.TransactionRequest("3", Exchange(3, "R")));    // Read Dynamic Variables
            Assert.False(g.TransactionRequest("4", Exchange(18, "R")));  // Write Tag
            Assert.False(g.TransactionRequest("5", Exchange(43, "R")));  // Set PV Zero
            Assert.False(g.TransactionRequest("6", Exchange(130, "R"))); // específico do fabricante: desconhecido
            Assert.False(g.SetChannelParameters("", "", "<FDT/>"));
            Assert.Equal(new[] { "Connect", "Transaction 0", "Transaction 3" }, inner.Calls);
        }

        // ------------------------------------------------------------ simulador

        [Fact]
        public void Simulador_ConectaTransacionaEDesconecta_ComRespostasValidas() => Sta.Run(() =>
        {
            var v = new XdrValidator(Schemas);
            var sim = new HartSimulatedChannel("SIM", new HartDevice { ManufacturerId = 62, DeviceTypeId = 2, DeviceId = 0x123456 });
            var c = new FakeClient();
            var g = new CommunicationGuard(sim, CommPolicy.Full, "T");

            Assert.True(g.ConnectRequest(c, "c1", HartSimulatedChannel.HartProtocolId, ConnectReq));
            Assert.Empty(c.Received); // resposta é assíncrona (§4.5.3.2)
            Assert.True(DtmSession.PumpUntil(() => c.Received.Count >= 2, TimeSpan.FromSeconds(5)));
            var conn = c.Received.First(r => r.Method == "OnConnectResponse2");
            Assert.Null(v.Validate(conn.Xml));
            Assert.Null(v.Validate(c.Received.First(r => r.Method == "parentInformation").Xml));
            Assert.Contains("fdt:tag=\"PT-101\"", conn.Xml);
            string commRef = FdtXml.Load(conn.Xml).DocumentElement.FirstChild.Attributes["communicationReference"].Value;

            Assert.True(g.TransactionRequest("t0", Exchange(0, commRef)));
            Assert.True(g.TransactionRequest("t1", Exchange(200, commRef)));
            Assert.True(DtmSession.PumpUntil(() => c.Received.Count(r => r.Method == "OnTransactionResponse") == 2, TimeSpan.FromSeconds(5)));
            var r0 = c.Received.First(r => r.InvokeId == "t0").Xml;
            var r1 = c.Received.First(r => r.InvokeId == "t1").Xml;
            Assert.Null(v.Validate(r0));
            Assert.Null(v.Validate(r1));
            Assert.Contains("byteArray=\"FE3E02050501010800123456\"", r0); // 254, fabr. 62, tipo 2, 5 preâmb., rev 5/1/1, hw 1<<3, flags 0, ID 123456
            Assert.Contains("CommandResponse value=\"64\"", r1);

            Assert.False(g.TransactionRequest("tx", Exchange(0, "referencia-inexistente")));
            Assert.True(g.DisconnectRequest("d1", $"<?xml version=\"1.0\"?><FDT {Ns}><DisconnectRequest communicationReference=\"{commRef}\"/></FDT>"));
            Assert.True(DtmSession.PumpUntil(() => c.Received.Any(r => r.Method == "OnDisconnectResponse"), TimeSpan.FromSeconds(5)));
            Assert.Null(v.Validate(c.Received.First(r => r.Method == "OnDisconnectResponse").Xml));
        });

        [Fact]
        public void Simulador_EnderecoSemInstrumento_RespondeCommunicationErrorValido() => Sta.Run(() =>
        {
            var v = new XdrValidator(Schemas);
            var sim = new HartSimulatedChannel("SIM", new HartDevice());
            var c = new FakeClient();
            Assert.True(sim.ConnectRequest(c, "c1", HartSimulatedChannel.HartProtocolId, ConnectReq.Replace("shortAddress=\"0\"", "shortAddress=\"7\"")));
            Assert.True(DtmSession.PumpUntil(() => c.Received.Count >= 1, TimeSpan.FromSeconds(5)));
            var x = c.Received.First(r => r.Method == "OnConnectResponse2").Xml;
            Assert.Contains("noConnection", x);
            Assert.Null(v.Validate(x));
        });

        // ------------------------------------------------------------ topologia

        [Fact]
        public void Topologia_GetParentNodesEGetChildNodes_RefletemAsLigacoes() => Sta.Run(() =>
        {
            var frame = new FrameContainer(Schemas);
            var v = new XdrValidator(Schemas);
            var sim = new HartSimulatedChannel("SIM-HART", new HartDevice());
            frame.RegisterLink(new TopologyLink { ParentTag = "SIM-HART", ChannelPath = sim.ChannelPath, ChildTag = "PT-101" });

            string parents = frame.GetParentNodes("PT-101");
            string kids = frame.GetChildNodes("SIM-HART", sim.ChannelPath);
            Assert.Contains("systemTag=\"SIM-HART\"", parents);
            Assert.Contains("systemTag=\"PT-101\"", kids);
            Assert.DoesNotContain("PT-101", frame.GetChildNodes("SIM-HART", "outro/canal"));
            Assert.Null(v.Validate(parents));
            Assert.Null(v.Validate(kids));
        });
    
        // ------------------------------------------------------------ enlace HART por porta virtual

        private static byte[] HartRequest(bool longFrame, byte[] addr, byte cmd, byte[] data = null)
        {
            data = data ?? new byte[0];
            var body = new List<byte> { (byte)(longFrame ? 0x82 : 0x02) };
            body.AddRange(addr); body.Add(cmd); body.Add((byte)data.Length); body.AddRange(data);
            byte chk = body.Aggregate((byte)0, (a, b) => (byte)(a ^ b));
            return new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }.Concat(body).Concat(new[] { chk }).ToArray();
        }

        private static byte[] ReadFrame(Win32SerialPort p, int expected)
        {
            var got = new List<byte>(); var buf = new byte[256];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (got.Count < expected && sw.ElapsedMilliseconds < 3000) got.AddRange(buf.Take(p.Read(buf)));
            return got.ToArray();
        }

        [VirtualPortPairFact]
        public void SimuladorDeFio_RespondeComando0E13_ComQuadroHartValido()
        {
            var dev = new HartDevice { ManufacturerId = 62, DeviceTypeId = 2, DeviceId = 0xABCDEF, Tag = "PT-101" };
            using (var sim = new HartWireSimulator(VirtualPortPairFactAttribute.Slave, dev))
            using (var master = new Win32SerialPort(VirtualPortPairFactAttribute.Master, 1200, 1, 200))
            {
                sim.Start();
                // cmd 0 em quadro curto, endereço de polling 0, mestre primário (bit 7)
                master.Write(HartRequest(false, new byte[] { 0x80 }, 0));
                var r = ReadFrame(master, 5 + 1 + 1 + 1 + 1 + 2 + 12 + 1);
                int i = Array.FindIndex(r, b => b != 0xFF);
                Assert.Equal(0x06, r[i]);                 // ACK, quadro curto
                Assert.Equal(0x80, r[i + 1]);             // endereço ecoado com bit de mestre
                Assert.Equal(0, r[i + 2]);                // comando 0
                Assert.Equal(14, r[i + 3]);               // 2 status + 12 dados
                Assert.Equal(0, r[i + 4]);                // response code
                Assert.Equal(new byte[] { 254, 62, 2 }, r.Skip(i + 6).Take(3).ToArray());
                Assert.Equal(r.Skip(i).Take(r.Length - i - 1).Aggregate((byte)0, (a, b) => (byte)(a ^ b)), r.Last());

                // cmd 13 em quadro longo, endereço único derivado do cmd 0
                var addr = dev.UniqueAddress(); addr[0] |= 0x80;
                master.Write(HartRequest(true, addr, 13));
                r = ReadFrame(master, 5 + 1 + 5 + 1 + 1 + 2 + 21 + 1);
                i = Array.FindIndex(r, b => b != 0xFF);
                Assert.Equal(0x86, r[i]);
                Assert.Equal(23, r[i + 7]);
                Assert.StartsWith("PT-101", HartDevice.UnpackAscii(r.Skip(i + 10).Take(6).ToArray()));

                // endereço longo de outro instrumento: sem resposta
                master.Write(HartRequest(true, new byte[] { 0x80 | 1, 1, 1, 1, 1 }, 0));
                Assert.Empty(ReadFrame(master, 1));
                Assert.Equal(3, sim.FramesReceived);
                Assert.Equal(2, sim.FramesAnswered);
            }
        }
    }
}
