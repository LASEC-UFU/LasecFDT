using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LasecFDT.Core.Rpc;
using LasecFDT.Fdt1.Comm;
using LasecFDT.Host;
using Xunit;

namespace LasecFDT.Tests
{
    /// <summary>
    /// LasecFDT.Host — backend de uma estação num servidor de terminal (várias estações na mesma máquina e na mesma
    /// pasta de planta). Comunicação só contra instrumento simulado (par virtual com0com).
    /// </summary>
    public class HostTests : IDisposable
    {
        private readonly string _plant = Path.Combine(Path.GetTempPath(), "LasecFDT-host-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("LASECFDT_STATION", null);
            try { Directory.Delete(_plant, true); } catch { }
        }

        private static StationInfo St(string id) => new StationInfo { Id = id, User = "teste\\" + id, SessionId = 1, Machine = Environment.MachineName };

        [Fact]
        public void Leases_between_stations_report_owner_and_free_on_release()
        {
            var warnings = new List<string>();
            var plant = Plant.Open(_plant, warnings);
            Assert.True(plant.Shared);
            using (var a = new LeaseManager(plant, St("TC-01")))
            using (var b = new LeaseManager(plant, St("TC-02")))
            {
                Assert.Null(a.TryAcquire("port:SRV:COM9", "modem"));
                var owner = b.TryAcquire("port:SRV:COM9");
                Assert.NotNull(owner);
                Assert.Equal("TC-01", owner.Station);
                Assert.Equal("modem", owner.Detail);
                Assert.Equal("TC-01", b.Owner("port:SRV:COM9").Station);
                Assert.Contains(b.List(), l => l.Key == "port:SRV:COM9" && l.Station == "TC-01" && !l.Mine);
                Assert.Contains(a.List(), l => l.Key == "port:SRV:COM9" && l.Mine);

                a.Release("port:SRV:COM9");
                Assert.Null(b.Owner("port:SRV:COM9"));
                Assert.Null(b.TryAcquire("port:SRV:COM9"));
                Assert.Equal("TC-02", a.TryAcquire("port:SRV:COM9").Station);
            }
            // Dono encerrado (Dispose = handles fechados, como numa queda do processo): nada fica preso.
            Assert.Empty(Directory.GetFiles(plant.Locks, "*.lease"));
        }

        [Fact]
        public void Port_key_is_per_machine_for_local_ports()
        {
            var st = St("TC-07");
            // Porta inexistente/local: chave da máquina (redirecionadas por RDP — \Device\RdpDr… — seriam da estação).
            Assert.Equal($"port:{Environment.MachineName}:COM250", Workspace.PortKey("COM250", st));
        }

        private static HostService NewStation(string id, string plant)
        {
            Environment.SetEnvironmentVariable("LASECFDT_STATION", id);
            try { return new HostService(null, plant); }
            finally { Environment.SetEnvironmentVariable("LASECFDT_STATION", null); }
        }

        private static async Task<T> Call<T>(HostService h, string method, object p = null) =>
            JsonLineChannel.FromNode<T>(await h.DispatchAsync(method, p == null ? null : JsonLineChannel.ToNode(p)));

        private static async Task<string> Refused(HostService h, string method, object p)
        {
            var ex = await Assert.ThrowsAsync<RpcException>(() => h.DispatchAsync(method, JsonLineChannel.ToNode(p)));
            return ex.Message;
        }

        [Fact]
        public void Scan_xml_is_parsed_and_matched_to_dtms()
        {
            const string xml = "<?xml version=\"1.0\"?><TopologyScan xmlns=\"x-schema:DTMTopologyScanSchema.xml\" xmlns:fdthart=\"x-schema:FDTHARTCommunicationSchema.xml\" " +
                "xmlns:fdthartdevice=\"x-schema:DTMHARTDeviceSchema.xml\" xmlns:fdt=\"x-schema:FDTDataTypesSchema.xml\">" +
                "<fdthartdevice:HARTDevice fdthart:manufacturerId=\"62\" fdthart:deviceTypeId=\"2\" fdthart:shortAddress=\"0\" fdt:tag=\"TT-201\">" +
                "<fdthart:LongAddress manufacturerId=\"62\" deviceTypeId=\"2\" address1=\"0\" address2=\"0\" address3=\"1\"/></fdthartdevice:HARTDevice>" +
                "<fdthartdevice:HARTDevice fdthart:manufacturerId=\"38\" fdthart:deviceTypeId=\"17\" fdthart:shortAddress=\"3\" fdt:tag=\"\"/></TopologyScan>";
            var devs = Workspace.ParseScan(xml);
            Assert.Equal(2, devs.Count);
            Assert.Equal("TT-201", devs[0].DeviceTag);
            Assert.Equal("3E02000001", devs[0].LongAddress);
            Assert.Equal("HART-3", devs[1].DeviceTag);

            DeviceTypeDto T(int i, string n, int? m, int? t, string v = "Smar") => new DeviceTypeDto { Index = i, Name = n, Vendor = v, ManufacturerId = m, DeviceTypeId = t, BusCategories = { "HART" } };
            var cat = new List<CatalogEntryDto>
            {
                new CatalogEntryDto { Clsid = "A", Name = "TT301 DTM", Vendor = "Smar", Role = "Device", Hostable = true, Fieldbuses = { "HART" }, DeviceTypes = new List<DeviceTypeDto> { T(0, "TT301", 62, 2) } },
                new CatalogEntryDto { Clsid = "B", Name = "LD301 DTM", Vendor = "Smar", Role = "Device", Hostable = true, DeviceTypes = new List<DeviceTypeDto> { T(0, "LD301", null, null) } },
                new CatalogEntryDto { Clsid = "C", Name = "Outro", Vendor = "X", Role = "Device", Hostable = true, Fieldbuses = { "HART" }, DeviceTypes = new List<DeviceTypeDto> { T(0, "Y", null, null, "X") } },
                new CatalogEntryDto { Clsid = "D", Name = "Modem", Vendor = "X", Role = "Communication", Hostable = true, DeviceTypes = new List<DeviceTypeDto> { T(0, "M", null, null, "X") } },
            };
            Workspace.MatchDtms(devs[0], cat);
            Assert.Equal(new[] { "A", "B", "C" }, devs[0].Candidates.Select(c => c.Clsid));
            Assert.Equal(new[] { "exact", "vendor", "protocol" }, devs[0].Candidates.Select(c => c.Match));
            Assert.Equal("Smar", devs[0].ManufacturerName);
            Workspace.MatchDtms(devs[1], cat);
            Assert.DoesNotContain(devs[1].Candidates, c => c.Match == "exact");
        }

        /// <summary>
        /// Busca de instrumentos pelo LasecHartCommDTM (porta virtual) → instrumento simulado identificado →
        /// DTM associado pela identidade declarada (TT301: fabricante 62, tipo 2) → adicionado e conectado.
        /// </summary>
        [VirtualPortPairFact]
        public async Task Scan_finds_instrument_and_suggests_matching_dtm()
        {
            using (var wire = new HartWireSimulator(VirtualPortPairFactAttribute.Slave, new HartDevice { ManufacturerId = 62, DeviceTypeId = 2, Tag = "TT-201" }))
            {
                wire.Start();
                var a = NewStation("TC-SCAN", _plant);
                try
                {
                    var cat = await Call<CatalogDto>(a, HostMethods.CatalogList, new CatalogParams { Refresh = true });
                    var comm = cat.Dtms.Single(d => d.ProgId == LasecHartCommDtmTests.ProgId);
                    await Call<ProjectDto>(a, HostMethods.ProjectNew, new ProjectRef { Name = "Busca" });
                    var c = await Call<NodeDto>(a, HostMethods.NodeAdd, new NodeAddParams
                    {
                        Clsid = comm.Clsid,
                        Properties = new Dictionary<string, string> { ["Port"] = VirtualPortPairFactAttribute.Master, ["ScanStop"] = "2", ["ResponseTimeoutMs"] = "500" },
                    });
                    var scan = await Call<ScanResultDto>(a, HostMethods.NodeScan, new NodeScanParams { Tag = c.Tag });
                    var d = Assert.Single(scan.Devices);
                    Assert.Equal("TT-201", d.DeviceTag);
                    Assert.Equal(0, d.ShortAddress);
                    Assert.Equal(62, d.ManufacturerId);
                    var best = d.Candidates.First();
                    Assert.Equal("exact", best.Match);
                    Assert.Contains("TT301", best.Name, StringComparison.OrdinalIgnoreCase);
                    Assert.Empty(await Call<List<LeaseInfo>>(a, HostMethods.Leases).ContinueWith(t => t.Result.Where(l => l.Key.StartsWith("port:")).ToList())); // porta solta após a busca

                    var dev = await Call<NodeDto>(a, HostMethods.NodeAdd, new NodeAddParams { Clsid = best.Clsid, DeviceTypeIndex = best.DeviceTypeIndex, ParentTag = c.Tag, Name = d.DeviceTag });
                    var on = await Call<NodeDto>(a, HostMethods.NodeConnect, new NodeConnectParams { Tag = dev.Tag, Policy = "ReadOnly" });
                    Assert.Equal("online", on.State);

                    var again = await Call<ScanResultDto>(a, HostMethods.NodeScan, new NodeScanParams { Tag = c.Tag });
                    Assert.Equal(dev.Tag, Assert.Single(again.Devices).ExistingNodeTag); // já está no projeto
                    await a.DispatchAsync(HostMethods.ProjectClose, null);
                }
                finally { a.Dispose(); }
            }
        }

        /// <summary>
        /// Botão Parar: busca real pela porta virtual sem instrumento (varre todos os endereços, ~80 s) e uma operação
        /// esperando na fila do projeto. O Parar encerra as duas na hora, solta a porta, o nó volta a "offline" sem erro
        /// e a estação continua usável (o DTM carrega de novo no próximo uso).
        /// </summary>
        [VirtualPortPairFact]
        public async Task Stop_interrupts_running_and_queued_operations_immediately_and_nothing_stays_locked()
        {
            var a = NewStation("TC-STOP", _plant);
            try
            {
                var cat = await Call<CatalogDto>(a, HostMethods.CatalogList, new CatalogParams { Refresh = true });
                var comm = cat.Dtms.Single(d => d.ProgId == LasecHartCommDtmTests.ProgId);
                await Call<ProjectDto>(a, HostMethods.ProjectNew, new ProjectRef { Name = "Parar" });
                var c = await Call<NodeDto>(a, HostMethods.NodeAdd, new NodeAddParams
                { Clsid = comm.Clsid, Properties = new Dictionary<string, string> { ["Port"] = VirtualPortPairFactAttribute.Master } });

                async Task<bool> PortHeld() => (await Call<List<LeaseInfo>>(a, HostMethods.Leases)).Any(l => l.Key.StartsWith("port:"));
                var scan = a.DispatchAsync(HostMethods.NodeScan, JsonLineChannel.ToNode(new NodeScanParams { Tag = c.Tag }));
                var started = Stopwatch.StartNew();
                while (!await PortHeld() && started.Elapsed < TimeSpan.FromSeconds(60)) await Task.Delay(200);
                Assert.True(await PortHeld(), "a busca não começou");
                await Task.Delay(2000);
                Assert.False(scan.IsCompleted);
                var queued = a.DispatchAsync(HostMethods.NodeFunctions, JsonLineChannel.ToNode(new NodeRef { Tag = c.Tag })); // espera a busca

                var sw = Stopwatch.StartNew();
                var r = await Call<CancelResultDto>(a, HostMethods.OperationCancel);
                Assert.Equal(RpcException.Canceled, (await Assert.ThrowsAsync<RpcException>(() => scan)).Code);
                Assert.Equal(RpcException.Canceled, (await Assert.ThrowsAsync<RpcException>(() => queued)).Code);
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"parar levou {sw.Elapsed.TotalSeconds:0.0} s");
                Assert.Contains(c.Name, r.StoppedNodes);
                Assert.False(await PortHeld()); // porta solta

                var node = (await Call<ProjectDto>(a, HostMethods.ProjectCurrent)).Nodes.Single(n => n.Tag == c.Tag);
                Assert.Equal("offline", node.State);
                Assert.Null(node.Error);

                // Nada ficou preso: a fila do projeto anda e o DTM carrega de novo.
                var f = await Call<NodeFunctionsDto>(a, HostMethods.NodeFunctions, new NodeRef { Tag = c.Tag });
                Assert.NotNull(f.Functions);
                await a.DispatchAsync(HostMethods.ProjectClose, null);
            }
            finally { a.Dispose(); }
            var audit = string.Join("\n", Directory.GetFiles(Path.Combine(_plant, "Audit")).Select(File.ReadAllText));
            Assert.Contains("operation.cancel", audit);
        }

        /// <summary>
        /// Parâmetros comandados pelo Frame (IDtmParameter / IDtmOnlineParameter) com o TT301 e instrumento simulado:
        /// lista, comparação (o DTM lê do instrumento; o projeto é restaurado), e gravação só com política Full + confirmação.
        /// </summary>
        [VirtualPortPairFact]
        public async Task Parameters_compare_and_download_are_driven_by_the_frame()
        {
            var sim = new HartDevice { ManufacturerId = 62, DeviceTypeId = 2, Tag = "TT-777", Descriptor = "CALDEIRA 2", Message = "SIMULADO" };
            using (var wire = new HartWireSimulator(VirtualPortPairFactAttribute.Slave, sim))
            {
                wire.Start();
                var a = NewStation("TC-PAR", _plant);
                try
                {
                    var cat = await Call<CatalogDto>(a, HostMethods.CatalogList, new CatalogParams { Refresh = true });
                    var comm = cat.Dtms.Single(d => d.ProgId == LasecHartCommDtmTests.ProgId);
                    var tt = cat.Dtms.Single(d => d.ProgId == "Smar.TT301DTM.1");
                    await Call<ProjectDto>(a, HostMethods.ProjectNew, new ProjectRef { Name = "Parametros" });
                    var c = await Call<NodeDto>(a, HostMethods.NodeAdd, new NodeAddParams { Clsid = comm.Clsid, Properties = new Dictionary<string, string> { ["Port"] = VirtualPortPairFactAttribute.Master } });
                    var dev = await Call<NodeDto>(a, HostMethods.NodeAdd, new NodeAddParams { Clsid = tt.Clsid, ParentTag = c.Tag, Name = "TT-1" });

                    var ps = await Call<ParameterSetDto>(a, HostMethods.NodeParameters, new NodeRef { Tag = dev.Tag });
                    Assert.True(ps.Items.Count > 10);
                    Assert.Contains(ps.Items, i => i.Name == "Endereço (slaveAddress)" && i.Value == "0");
                    string Tag(ParameterSetDto p) => p.Items.Single(i => i.Name == "OnLine Tag").Value;
                    Assert.Equal("", Tag(ps));

                    Assert.Contains("Conecte", await Refused(a, HostMethods.NodeCompare, new NodeRef { Tag = dev.Tag }));
                    await Call<NodeDto>(a, HostMethods.NodeConnect, new NodeConnectParams { Tag = dev.Tag, Policy = "ReadOnly" });

                    // Comparar: o DTM lê do instrumento; as diferenças aparecem; o projeto volta como estava e reconectado.
                    var cmp = await Call<CompareResultDto>(a, HostMethods.NodeCompare, new NodeRef { Tag = dev.Tag });
                    Assert.True(cmp.Upload.Accepted && cmp.Upload.Completed);
                    Assert.False(cmp.KeptDeviceValues);
                    var tagDiff = cmp.Items.Single(i => i.Name == "OnLine Tag");
                    Assert.True(tagDiff.Different);
                    Assert.Equal("TT-777", tagDiff.DeviceValue);
                    Assert.Contains(cmp.Items, i => i.Name == "OnLine Descriptor" && i.DeviceValue == "CALDEIRA 2");
                    Assert.True(cmp.RefusedCommands == 0 || cmp.RefusedNotes.Count > 0, "recusa sem motivo informado");
                    var after = await Call<ParameterSetDto>(a, HostMethods.NodeParameters, new NodeRef { Tag = dev.Tag });
                    Assert.Equal("", Tag(after)); // projeto restaurado
                    var cur = (await Call<ProjectDto>(a, HostMethods.ProjectCurrent)).Nodes.Single(n => n.Tag == dev.Tag);
                    Assert.Equal("online", cur.State);
                    Assert.Equal("ReadOnly", cur.Policy);

                    // Gravar: recusado em ReadOnly e sem confirmação; com Full + confirmação, chega ao instrumento.
                    Assert.Contains("modo completo", await Refused(a, HostMethods.NodeDownload, new NodeDownloadParams { Tag = dev.Tag, Confirm = true }));
                    await Call<NodeDto>(a, HostMethods.NodeDisconnect, new NodeRef { Tag = dev.Tag });
                    await Call<NodeDto>(a, HostMethods.NodeConnect, new NodeConnectParams { Tag = dev.Tag, Policy = "Full", ConfirmFull = true });
                    Assert.Contains("confirmação", await Refused(a, HostMethods.NodeDownload, new NodeDownloadParams { Tag = dev.Tag }));
                    var dl = await Call<OnlineParameterResult>(a, HostMethods.NodeDownload, new NodeDownloadParams { Tag = dev.Tag, Confirm = true });
                    Assert.True(dl.Accepted && dl.Completed);
                    Assert.Contains(18, sim.Writes); // tag/descritor/data gravados pelo DTM no instrumento
                    await a.DispatchAsync(HostMethods.ProjectClose, null);
                }
                finally { a.Dispose(); }
                var audit = string.Join("\n", Directory.GetFiles(Path.Combine(_plant, "Audit")).Select(File.ReadAllText));
                Assert.Contains("node.compare", audit);
                Assert.Contains("node.download", audit);
            }
        }

        /// <summary>
        /// Duas estações na mesma planta, cada uma com o seu Host e os seus workers:
        /// A edita o projeto e conecta o LD301 pela porta virtual; B vê o projeto somente leitura, reaproveita o
        /// catálogo calculado por A, e é impedida de usar a mesma porta. Depois de A fechar, B edita e conecta.
        /// </summary>
        [VirtualPortPairFact]
        public async Task Two_stations_share_plant_without_mixing()
        {
            using (var wire = new HartWireSimulator(VirtualPortPairFactAttribute.Slave, new HartDevice { ManufacturerId = 62, DeviceTypeId = 2 }))
            {
                wire.Start();
                var a = NewStation("TC-A", _plant);
                var b = NewStation("TC-B", _plant);
                try
                {
                    Assert.Equal("TC-A", a.Info.Station.Id);
                    Assert.Equal("TC-B", b.Info.Station.Id);

                    var catA = await Call<CatalogDto>(a, HostMethods.CatalogList, new CatalogParams { Refresh = true });
                    var comm = catA.Dtms.Single(d => d.ProgId == LasecHartCommDtmTests.ProgId);
                    var ld = catA.Dtms.Single(d => d.ProgId == "SmarLD301DTM.DTMCore.1");
                    Assert.Equal("Communication", comm.Role);
                    Assert.Equal("Device", ld.Role);

                    // ---- estação A: projeto, topologia e conexão somente leitura
                    await Call<ProjectDto>(a, HostMethods.ProjectNew, new ProjectRef { Name = "Caldeira" });
                    var c = await Call<NodeDto>(a, HostMethods.NodeAdd, new NodeAddParams { Clsid = comm.Clsid, Properties = new Dictionary<string, string> { ["Port"] = VirtualPortPairFactAttribute.Master } });
                    var dev = await Call<NodeDto>(a, HostMethods.NodeAdd, new NodeAddParams { Clsid = ld.Clsid, ParentTag = c.Tag, Name = "PT-101" });
                    Assert.Equal(c.Tag, dev.ParentTag);
                    Assert.EndsWith("/HART", dev.ChannelPath);

                    Assert.Contains("confirmação", await Refused(a, HostMethods.NodeConnect, new NodeConnectParams { Tag = dev.Tag, Policy = "Full" }));
                    var on = await Call<NodeDto>(a, HostMethods.NodeConnect, new NodeConnectParams { Tag = dev.Tag, Policy = "ReadOnly" });
                    Assert.Equal("online", on.State);
                    Assert.Equal("ReadOnly", on.Policy);
                    await Call<ProjectDto>(a, HostMethods.ProjectSave);

                    // ---- estação B: catálogo compartilhado, projeto somente leitura, porta em uso
                    var catB = await Call<CatalogDto>(b, HostMethods.CatalogList, new CatalogParams());
                    Assert.True(catB.FromCache);
                    Assert.Equal("TC-A", catB.GeneratedBy);

                    var list = await Call<List<ProjectFileDto>>(b, HostMethods.ProjectList);
                    Assert.Equal("TC-A", list.Single(p => p.Name == "Caldeira").LockedBy);
                    var ro = await Call<ProjectDto>(b, HostMethods.ProjectOpen, new ProjectRef { Name = "Caldeira" });
                    Assert.True(ro.ReadOnly);
                    Assert.Equal("TC-A", ro.LockedBy);
                    Assert.Contains("somente leitura", await Refused(b, HostMethods.NodeRemove, new NodeRef { Tag = dev.Tag }));
                    Assert.Contains("somente leitura", (await Assert.ThrowsAsync<RpcException>(() => b.DispatchAsync(HostMethods.ProjectSave, null))).Message);
                    await b.DispatchAsync(HostMethods.ProjectClose, null);

                    await Call<ProjectDto>(b, HostMethods.ProjectNew, new ProjectRef { Name = "Linha 3" });
                    var c2 = await Call<NodeDto>(b, HostMethods.NodeAdd, new NodeAddParams { Clsid = comm.Clsid, Properties = new Dictionary<string, string> { ["Port"] = VirtualPortPairFactAttribute.Master } });
                    var dev2 = await Call<NodeDto>(b, HostMethods.NodeAdd, new NodeAddParams { Clsid = ld.Clsid, ParentTag = c2.Tag, Name = "FT-301" });
                    var msg = await Refused(b, HostMethods.NodeConnect, new NodeConnectParams { Tag = dev2.Tag, Policy = "ReadOnly" });
                    Assert.Contains($"porta {VirtualPortPairFactAttribute.Master} está em uso pela estação TC-A", msg);

                    var leases = await Call<List<LeaseInfo>>(b, HostMethods.Leases);
                    Assert.Contains(leases, l => l.Key.StartsWith("port:") && l.Station == "TC-A" && !l.Mine);
                    Assert.Contains(leases, l => l.Key == "station:TC-A");
                    Assert.Contains(leases, l => l.Key == "station:TC-B" && l.Mine);

                    // ---- A desconecta e fecha: a porta e o projeto ficam livres para B
                    await Call<NodeDto>(a, HostMethods.NodeDisconnect, new NodeRef { Tag = dev.Tag });
                    await a.DispatchAsync(HostMethods.ProjectClose, null);
                    var on2 = await Call<NodeDto>(b, HostMethods.NodeConnect, new NodeConnectParams { Tag = dev2.Tag, Policy = "ReadOnly" });
                    Assert.Equal("online", on2.State);
                    await Call<NodeDto>(b, HostMethods.NodeDisconnect, new NodeRef { Tag = dev2.Tag });
                    await b.DispatchAsync(HostMethods.ProjectClose, null);

                    // ---- B reabre o projeto de A (agora livre): mesmo systemTag, data set carregado, conecta de novo
                    var rw = await Call<ProjectDto>(b, HostMethods.ProjectOpen, new ProjectRef { Name = "Caldeira" });
                    Assert.False(rw.ReadOnly);
                    Assert.Equal(new[] { c.Tag, dev.Tag }.OrderBy(x => x), rw.Nodes.Select(n => n.Tag).OrderBy(x => x));
                    Assert.All(rw.Nodes, n => Assert.Equal("offline", n.State)); // nada carregado até ser usado
                    var on3 = await Call<NodeDto>(b, HostMethods.NodeConnect, new NodeConnectParams { Tag = dev.Tag, Policy = "ReadOnly" });
                    Assert.Equal("online", on3.State);
                    var log = await Call<List<CommRecordDto>>(b, HostMethods.NodeCommLog, new NodeRef { Tag = dev.Tag });
                    Assert.Contains(log, r => r.Method == "ConnectRequest" && r.Forwarded);
                    await b.DispatchAsync(HostMethods.ProjectClose, null);
                    Assert.True(wire.FramesAnswered > 0);
                }
                finally
                {
                    a.Dispose();
                    b.Dispose();
                }
                // Audit: ações das duas estações na trilha comum da planta.
                var audit = string.Join("\n", Directory.GetFiles(Path.Combine(_plant, "Audit")).Select(File.ReadAllText));
                Assert.Contains("\"station\": \"TC-A\"", audit);
                Assert.Contains("\"station\": \"TC-B\"", audit);
                Assert.Contains("node.connect", audit);
                Assert.Empty(Directory.GetFiles(Path.Combine(_plant, "Locks"), "*.lease"));
            }
        }
    }
}
