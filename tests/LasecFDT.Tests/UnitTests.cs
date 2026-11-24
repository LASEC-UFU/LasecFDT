using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using LasecFDT.Core;
using LasecFDT.Core.Project;
using LasecFDT.Fdt1;
using LasecFDT.Fdt1.Com;
using LasecFDT.Fdt1.Discovery;
using LasecFDT.Fdt1.Frame;
using Xunit;

namespace LasecFDT.Tests
{
    /// <summary>Testes que não dependem de DTMs instalados.</summary>
    public class TypeLibTests
    {
        [Fact]
        public void Fdt100TypeLib_TemLibIdEVersaoDa121()
        {
            var t = FdtInterfaceTable.Default.TypeLib;
            Assert.Equal(FdtIds.ToFdtUuid(FdtIds.LibIdFdt100), t.LibId);
            Assert.Equal("1.20100", t.Version); // §3.4: FDT 1.2.1.0 → 1.20100
        }

        [Theory]
        // IIDs conferidos com o IDL impresso no Cap. 11 da spec 1.2.1
        [InlineData("IDtm", "036D1481-387B-11D4-86E1-00E0987270B9")]
        [InlineData("IFdtContainer", "036D1487-387B-11D4-86E1-00E0987270B9")]
        [InlineData("IDtmActiveXControl", "036D1486-387B-11D4-86E1-00E0987270B9")]
        [InlineData("IFdtCommunication", "039ECFC4-9CA8-44E6-944D-B37F288A34D8")]
        [InlineData("IFdtChannelActiveXInformation", "F2BD2970-13FA-470C-A28C-6A5969A04037")]
        public void IidsDaTypeLib_ConferemComOIdlDaSpec(string name, string iid)
        {
            Assert.Equal(new Guid(iid), FdtInterfaceTable.Default.IidOf(name));
        }

        [Fact]
        public void InteropAssembly_UsaOsMesmosIidsDaTypeLib()
        {
            var asm = typeof(Jigfdt.Fdt100.IDtm).Assembly;
            foreach (var itf in FdtInterfaceTable.Default.Interfaces)
            {
                var t = asm.GetType("Jigfdt.Fdt100." + itf.Name);
                Assert.NotNull(t);
                Assert.Equal(new Guid(itf.Guid), t.GUID);
            }
        }

        [Fact]
        public void FrameContainer_ImplementaTodasAsInterfacesObrigatoriasDoFrame()
        {
            // §4.10.1 — interfaces "Mandatory" do Frame Application
            string[] mandatory = { "IFdtContainer", "IDtmEvents", "IDtmEvents2", "IDtmAuditTrailEvents", "IFdtDialog",
                                   "IFdtTopology", "IFdtBtmTopology", "IDtmSingleDeviceDataAccessEvents", "IDtmSingleInstanceDataAccessEvents" };
            var implemented = typeof(FrameContainer).GetInterfaces().Select(i => i.Name).ToList();
            foreach (var m in mandatory) Assert.Contains(m, implemented);
        }
    }

    public class FdtXmlTests
    {
        private static XmlDocument Parse(string xml) => FdtXml.Load(xml);

        [Fact]
        public void DocumentosDoFrame_SaoXmlBemFormadoComRaizFDT()
        {
            foreach (var x in new[] { FdtXml.FrameInfo(), FdtXml.UserInfo("P", "U", "maintenance"), FdtXml.OperationPhase(),
                                      FdtXml.FunctionCall("2", "fdtOfflineParameterize"), FdtXml.SystemGuiLabel("a<b&c") })
                Assert.Equal("FDT", Parse(x).DocumentElement.LocalName);
        }

        [Fact]
        public void FrameInfo_Declara121()
        {
            var v = Parse(FdtXml.FrameInfo()).GetElementsByTagName("*").OfType<XmlElement>().First(e => e.LocalName == "FDTVersion");
            Assert.Equal("1", v.GetAttribute("major")); Assert.Equal("2", v.GetAttribute("minor")); Assert.Equal("1", v.GetAttribute("release"));
        }

        [Fact]
        public void ParseInformation_LeExemploOficial()
        {
            string path = Path.Combine(TestContextPaths.Root, @"specs\FDT-1.2.1\spec\XML\Examples\DTMInformationInstance.xml");
            var info = FdtXml.ParseInformation(File.ReadAllText(path));
            Assert.Equal("1.2.1.1", info.FdtVersion);
            Assert.Single(info.DeviceTypes);
            Assert.Equal(4, info.DeviceTypes[0].Variants.Count);
            Assert.Contains("HART", info.DeviceTypes[0].BusCategories);
            // O deviceType extraído deve gerar um DTMInit bem-formado
            Assert.Equal("FDT", Parse(FdtXml.InitDeviceType(info.DeviceTypes[0].OuterXml)).DocumentElement.LocalName);
        }

        [Fact]
        public void ParseFunctions_LeExemploOficial()
        {
            string path = Path.Combine(TestContextPaths.Root, @"specs\FDT-1.2.1\spec\XML\Examples\DTMFunctionsInstance1.xml");
            var f = FdtXml.ParseFunctions(File.ReadAllText(path), out var def);
            Assert.Equal("1", def);
            Assert.Equal(3, f.Count);
            Assert.Equal("fdtConfiguration", f[1].ApplicationId); // o exemplo oficial tem espaço final: deve ser aparado
        }

        [Fact]
        public void DialogoPadrao_DevolveBotaoDefault()
        {
            string path = Path.Combine(TestContextPaths.Root, @"specs\FDT-1.2.1\spec\XML\Examples\FDTUserMessageInstance.xml");
            var answer = Parse(FrameContainer.DefaultDialogAnswer(File.ReadAllText(path), "systemResponse"));
            var msg = answer.GetElementsByTagName("*").OfType<XmlElement>().First(e => e.LocalName == "FDTUserMessage");
            Assert.Equal("buttonCancel", msg.GetAttribute("resultMessage"));
            Assert.Equal("systemResponse", msg.GetAttribute("resultStatus"));
        }
    }

    public class PersistenceStorageTests
    {
        [Fact]
        public void PropertyBag_RoundTripDeTiposSuportados()
        {
            var bag = new PropertyBagStore();
            object[] values = { "texto", 42, (short)7, true, 3.5, 2.25f, (byte)9, new byte[] { 1, 2, 3 }, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), 12.5m };
            for (int i = 0; i < values.Length; i++) { object v = values[i]; Assert.Equal(0, bag.Write("p" + i, ref v)); }

            var json = Json.Serialize(bag.Export());
            var restored = PropertyBagStore.Import(Json.Deserialize<System.Collections.Generic.List<PersistedProperty>>(json));
            for (int i = 0; i < values.Length; i++)
            {
                object v = null;
                Assert.Equal(0, restored.Read("p" + i, ref v, null));
                Assert.Equal(values[i], v);
            }
        }

        [Fact]
        public void PropertyBag_PropriedadeInexistente_RetornaEInvalidArg()
        {
            object v = null;
            Assert.Equal(unchecked((int)0x80070057), new PropertyBagStore().Read("nada", ref v, null));
        }

        [Fact]
        public void ManagedIStream_EscreveLeEPosiciona()
        {
            var s = new ManagedIStream();
            var data = Encoding.ASCII.GetBytes("FDT-1.2.1");
            s.Write(data, data.Length, IntPtr.Zero);
            s.Seek(0, 0, IntPtr.Zero);
            var buf = new byte[data.Length];
            s.Read(buf, buf.Length, IntPtr.Zero);
            Assert.Equal(data, buf);
            Assert.Equal(data, s.ToArray());
        }

        [Fact]
        public void Projeto_SalvaECarregaArquivo()
        {
            string f = Path.Combine(Path.GetTempPath(), "lasecfdt-test-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var p = new LasecProject { Name = "T" };
                p.Devices.Add(new DeviceInstance { SystemTag = "A", DtmClsid = Guid.Empty.ToString(), DataSet = new PersistedDataSet { Kind = "StreamInit", StreamBase64 = "AQID" } });
                ProjectService.SaveFile(p, f);
                ProjectService.SaveFile(p, f); // sobrescrita atômica
                var q = ProjectService.LoadFile(f);
                Assert.Equal("AQID", q.Devices.Single().DataSet.StreamBase64);
            }
            finally { File.Delete(f); }
        }
    }

    public class DiscoveryTests
    {
        [Fact]
        public void PeMachine_ReconheceX86()
        {
            Assert.Equal("x86", DtmDiscovery.ReadPeMachine(Path.Combine(TestContextPaths.Root, @"lib\fdt-jig\FDT100.dll")));
        }

        [Fact]
        public void Scan_SoIncluiClassesDaCategoriaFdtDtmOuBtm_EConfereComCategoryManager()
        {
            var c = DtmDiscovery.Scan(readTypeLibs: false);
            Assert.All(c.Dtms, d => Assert.True(d.Roles.Contains("Dtm") || d.Roles.Contains("Btm")));
            Assert.Null(c.CategoryManagerCrossCheck.Error);
            Assert.Empty(c.CategoryManagerCrossCheck.MissingFromRegistryScan);
            Assert.Empty(c.CategoryManagerCrossCheck.MissingFromCategoryManager);
        }
    }

    internal static class TestContextPaths
    {
        /// <summary>Raiz do repositório (procura a pasta que contém LasecFDT.sln).</summary>
        public static string Root
        {
            get
            {
                var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                while (d != null && !File.Exists(Path.Combine(d.FullName, "LasecFDT.sln"))) d = d.Parent;
                return d?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
            }
        }
    }
}
