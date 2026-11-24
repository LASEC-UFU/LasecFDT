using System;
using System.IO;
using System.Linq;

namespace LasecFDT.Supervisor
{
    /// <summary>Localiza LasecFDT.Worker.Fdt1.exe: variável LASECFDT_WORKER, pasta da aplicação, ou layout de desenvolvimento.</summary>
    public static class WorkerLocator
    {
        public const string ExeName = "LasecFDT.Worker.Fdt1.exe";

        public static string Find(string baseDirectory = null)
        {
            var env = Environment.GetEnvironmentVariable("LASECFDT_WORKER");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return Path.GetFullPath(env);

            var baseDir = baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory;
            var local = Path.Combine(baseDir, ExeName);
            if (File.Exists(local)) return local;

            // Desenvolvimento: <raiz>\src\LasecFDT.Worker.Fdt1\bin\<Debug|Release>\net48\
            var dir = new DirectoryInfo(baseDir);
            string config = baseDir.Contains(@"\Release\") ? "Release" : "Debug";
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LasecFDT.sln"))) dir = dir.Parent;
            if (dir != null)
            {
                var candidates = new[] { config, config == "Debug" ? "Release" : "Debug" }
                    .Select(c => Path.Combine(dir.FullName, "src", "LasecFDT.Worker.Fdt1", "bin", c, "net48", ExeName));
                var found = candidates.FirstOrDefault(File.Exists);
                if (found != null) return found;
            }
            throw new FileNotFoundException($"{ExeName} não encontrado (defina LASECFDT_WORKER ou compile a solução).");
        }
    }
}
