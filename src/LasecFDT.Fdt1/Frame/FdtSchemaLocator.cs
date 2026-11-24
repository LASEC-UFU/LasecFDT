using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace LasecFDT.Fdt1.Frame
{
    /// <summary>
    /// Pasta de schemas que o Frame informa aos DTMs (IFdtContainer::GetXmlSchemaPath; §7.1.4: "a Frame Application is
    /// responsible to install all FDT related XML schemas").
    ///  1. LASECFDT_SCHEMA_PATH (diagnóstico).
    ///  2. Desenvolvimento: FDTSchemas\ ao lado do executável (montada pelo build a partir da especificação local).
    ///  3. Instalação: o instalador aplica o merge module oficial redistribuível FDT_XMLSchemata (FDT 1.2.1), que cria
    ///     "FDT XML Schemas\" ao lado do executável. A pasta efetiva é composta, uma vez por versão, em
    ///     %ProgramData%\LasecFDT\FDTSchemas-&lt;hash&gt;\ com esses schemas e as subpastas de protocolo (§6.4, nome =
    ///     GUID do protocolo, ex.: HART 036D1498-…) que outros Frames já instalaram nesta máquina (ex.: PACTware).
    ///     Os schemas de protocolo HART da FDT 1.2 não são redistribuíveis; por isso vêm da própria máquina.
    /// </summary>
    public static class FdtSchemaLocator
    {
        private static string _cached;

        public static string Resolve()
        {
            if (_cached != null) return _cached;
            string env = Environment.GetEnvironmentVariable("LASECFDT_SCHEMA_PATH");
            if (!string.IsNullOrWhiteSpace(env)) return _cached = env;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string dev = Path.Combine(baseDir, "FDTSchemas");
            if (File.Exists(Path.Combine(dev, "DTMInformationSchema.xml"))) return _cached = dev;

            string installed = Path.Combine(baseDir, "FDT XML Schemas");
            if (!Directory.Exists(installed)) return _cached = dev; // sem schemas: o DTM recebe a pasta mesmo assim
            try
            {
                var protocolDirs = OtherFrameSchemaFolders().SelectMany(f => SafeDirs(f).Where(IsProtocolFolder)).ToList();
                return _cached = Compose(installed, protocolDirs, new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LasecFDT"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LasecFDT"),
                });
            }
            catch { return _cached = installed; }
        }

        /// <summary>Pastas "FDT XML Schemas" de outros Frames instalados (somente leitura; só subpastas de protocolo são usadas).</summary>
        public static IEnumerable<string> OtherFrameSchemaFolders()
        {
            var roots = new[] { Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonProgramFilesX86 }
                .Select(Environment.GetFolderPath).Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)).Distinct();
            foreach (var root in roots)
            {
                IEnumerable<string> level1;
                try { level1 = Directory.EnumerateDirectories(root); } catch { continue; }
                foreach (var d1 in level1)
                {
                    if (Path.GetFileName(d1).StartsWith("LasecFDT", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var cand in SafeDirs(d1).Concat(SafeDirs(d1).SelectMany(SafeDirs)))
                        if (string.Equals(Path.GetFileName(cand), "FDT XML Schemas", StringComparison.OrdinalIgnoreCase)) yield return cand;
                }
            }
        }

        private static IEnumerable<string> SafeDirs(string d)
        {
            try { return Directory.EnumerateDirectories(d).ToList(); } catch { return Enumerable.Empty<string>(); }
        }

        public static bool IsProtocolFolder(string dir) => Guid.TryParse(Path.GetFileName(dir), out _);

        /// <summary>Monta (uma vez por conteúdo) a pasta de schemas: os instalados + subpastas de protocolo de outros Frames.</summary>
        public static string Compose(string installed, IList<string> protocolDirs, IEnumerable<string> targetRoots)
        {
            var sources = new List<string> { installed };
            sources.AddRange(protocolDirs);
            string stamp = string.Join("|", sources.Select(s =>
            {
                var files = Directory.GetFiles(s, "*", SearchOption.AllDirectories);
                return s + ":" + files.Length + ":" + (files.Length == 0 ? 0 : files.Max(f => File.GetLastWriteTimeUtc(f).Ticks));
            }));
            string hash;
            using (var sha = SHA1.Create()) hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(stamp.ToLowerInvariant()))).Replace("-", "").Substring(0, 12);

            foreach (var root in targetRoots)
            {
                string target = Path.Combine(root, "FDTSchemas-" + hash);
                if (File.Exists(Path.Combine(target, ".complete"))) return target;
                try
                {
                    // Montagem em pasta temporária e renomeação: outra estação montando ao mesmo tempo não vê pasta pela metade.
                    string tmp = target + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    CopyTree(installed, tmp);
                    foreach (var p in protocolDirs)
                    {
                        string dest = Path.Combine(tmp, Path.GetFileName(p));
                        if (!Directory.Exists(dest)) CopyTree(p, dest); // o primeiro Frame encontrado vence
                    }
                    File.WriteAllText(Path.Combine(tmp, ".complete"), stamp);
                    try { Directory.Move(tmp, target); }
                    catch (IOException) { try { Directory.Delete(tmp, true); } catch { } } // outra sessão montou antes
                    if (File.Exists(Path.Combine(target, ".complete"))) return target;
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            return installed;
        }

        private static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(from)) CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
        }
    }
}
