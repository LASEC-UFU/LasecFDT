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
                // Mesmo conteúdo: reaproveita a pasta (outra estação/sessão não remonta).
                Assert.Equal(a, FdtSchemaLocator.Compose(installed, new[] { other }, new[] { target }));
                Assert.Single(Directory.GetDirectories(target));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }
}
