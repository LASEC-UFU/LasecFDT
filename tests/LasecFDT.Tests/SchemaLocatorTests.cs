using System;
using System.IO;
using LasecFDT.Fdt1.Frame;
using Xunit;

namespace LasecFDT.Tests
{
    /// <summary>Pasta de schemas da instalação: merge module 1.2.1 + subpastas de protocolo de outros Frames da máquina.</summary>
    public class SchemaLocatorTests
    {
        [Fact]
        public void Composes_installed_schemas_with_protocol_folders_once()
        {
            string root = Path.Combine(Path.GetTempPath(), "LasecFDT-schemas-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                string installed = Path.Combine(root, "engine", "FDT XML Schemas");
                Directory.CreateDirectory(installed);
                File.WriteAllText(Path.Combine(installed, "DTMInformationSchema.xml"), "<Schema/>");
                string other = Path.Combine(root, "PACTware", "FDT XML Schemas", "036D1498-387B-11D4-86E1-00E0987270B9");
                Directory.CreateDirectory(other);
                File.WriteAllText(Path.Combine(other, "FDTHARTCommunicationSchema.xml"), "<Schema/>");
                Assert.True(FdtSchemaLocator.IsProtocolFolder(other));
                Assert.False(FdtSchemaLocator.IsProtocolFolder(installed));

                string target = Path.Combine(root, "plant");
                string a = FdtSchemaLocator.Compose(installed, new[] { other }, new[] { target });
                Assert.StartsWith(target, a);
                Assert.True(File.Exists(Path.Combine(a, "DTMInformationSchema.xml")));
                Assert.True(File.Exists(Path.Combine(a, "036D1498-387B-11D4-86E1-00E0987270B9", "FDTHARTCommunicationSchema.xml")));
                Assert.True(File.Exists(Path.Combine(a, "FDTHARTCommunicationSchema.xml"))); // também na raiz, onde os DTMs procuram
                // Mesmo conteúdo: reaproveita a pasta (outra estação/sessão não remonta).
                Assert.Equal(a, FdtSchemaLocator.Compose(installed, new[] { other }, new[] { target }));
                Assert.Single(Directory.GetDirectories(target));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public void Operator_folder_schemas_loose_or_zipped_go_to_the_root_with_priority_over_other_frames()
        {
            string root = Path.Combine(Path.GetTempPath(), "LasecFDT-schemas-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                string installed = Path.Combine(root, "engine", "FDT XML Schemas");
                Directory.CreateDirectory(installed);
                File.WriteAllText(Path.Combine(installed, "DTMInformationSchema.xml"), "<oficial/>");

                // Operador: um schema solto numa subpasta e o .zip do anexo como foi baixado (com um arquivo que não é schema).
                string op = Path.Combine(root, "ProgramData", "Schemas");
                Directory.CreateDirectory(Path.Combine(op, "anexo"));
                File.WriteAllText(Path.Combine(op, "anexo", "DTMHARTDeviceSchema.xml"), "<operador/>");
                File.WriteAllText(Path.Combine(op, "DTMInformationSchema.xml"), "<operador/>"); // não substitui o oficial
                using (var zip = System.IO.Compression.ZipFile.Open(Path.Combine(op, "HART-Annex.zip"), System.IO.Compression.ZipArchiveMode.Create))
                {
                    using (var w = new StreamWriter(zip.CreateEntry("XML/Schemas/FDTHARTCommunicationSchema.xml").Open())) w.Write("<operador-zip/>");
                    using (var w = new StreamWriter(zip.CreateEntry("Leia-me.pdf").Open())) w.Write("pdf");
                }

                // Outro Frame com o mesmo arquivo: o do operador vence.
                string other = Path.Combine(root, "PACTware", "FDT XML Schemas", "036D1498-387B-11D4-86E1-00E0987270B9");
                Directory.CreateDirectory(other);
                File.WriteAllText(Path.Combine(other, "FDTHARTCommunicationSchema.xml"), "<pactware/>");

                string a = FdtSchemaLocator.Compose(installed, new[] { other }, new[] { Path.Combine(root, "plant") }, op);
                Assert.Equal("<oficial/>", File.ReadAllText(Path.Combine(a, "DTMInformationSchema.xml")));
                Assert.Equal("<operador/>", File.ReadAllText(Path.Combine(a, "DTMHARTDeviceSchema.xml")));
                Assert.Equal("<operador-zip/>", File.ReadAllText(Path.Combine(a, "FDTHARTCommunicationSchema.xml")));
                Assert.False(File.Exists(Path.Combine(a, "Leia-me.pdf")));

                // Novo arquivo do operador: a pasta é montada de novo (outro hash).
                File.WriteAllText(Path.Combine(op, "FDTHARTChannelParameterSchema.xml"), "<operador/>");
                File.SetLastWriteTimeUtc(Path.Combine(op, "FDTHARTChannelParameterSchema.xml"), DateTime.UtcNow.AddMinutes(1));
                string b = FdtSchemaLocator.Compose(installed, new[] { other }, new[] { Path.Combine(root, "plant") }, op);
                Assert.NotEqual(a, b);
                Assert.True(File.Exists(Path.Combine(b, "FDTHARTChannelParameterSchema.xml")));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }
}
