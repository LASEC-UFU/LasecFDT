using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LasecFDT.Core;
using LasecFDT.Core.Rpc;

namespace LasecFDT.Host
{
    /// <summary>
    /// Pasta comum da planta (padrão %ProgramData%\LasecFDT), compartilhada por todas as estações do servidor:
    ///   Projects\  projetos (um arquivo por projeto)          Cache\    catálogo de DTMs e descrições (calculados uma vez)
    ///   Locks\     arrendamentos (portas, projetos, estações)  Audit\    trilha de auditoria (escritas, ligações Full…)
    ///   Stations\  preferências de cada estação (tema…)
    /// Não há serviço central: a coordenação entre sessões usa arquivos abertos com compartilhamento restrito, que o
    /// Windows libera sozinho quando o processo termina (inclusive se cair). Ver doc 11.
    /// </summary>
    public sealed class Plant
    {
        public string Root { get; }
        public bool Shared { get; }
        public string Projects => Path.Combine(Root, "Projects");
        public string Locks => Path.Combine(Root, "Locks");
        public string Cache => Path.Combine(Root, "Cache");
        public string Audit => Path.Combine(Root, "Audit");
        public string Stations => Path.Combine(Root, "Stations");

        private Plant(string root, bool shared)
        {
            Root = root; Shared = shared;
            foreach (var d in new[] { Projects, Locks, Cache, Audit, Stations }) Directory.CreateDirectory(d);
        }

        /// <summary>
        /// Pasta da planta: --plant, LASECFDT_PLANT ou %ProgramData%\LasecFDT. Se a estação não puder escrever nela
        /// (instalação sem a permissão dada pelo instalador), cai para uma pasta própria do usuário: funciona, mas
        /// isolada — o aviso aparece na UI.
        /// </summary>
        public static Plant Open(string requested, List<string> warnings)
        {
            string root = requested ?? Environment.GetEnvironmentVariable("LASECFDT_PLANT");
            if (string.IsNullOrWhiteSpace(root)) root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LasecFDT");
            try
            {
                var p = new Plant(Path.GetFullPath(root), true);
                string probe = Path.Combine(p.Locks, $".probe-{Guid.NewGuid():N}");
                File.WriteAllText(probe, ""); File.Delete(probe);
                return p;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LasecFDT", "Plant");
                warnings.Add($"Sem permissão de escrita na pasta da planta '{root}' ({ex.Message}). Usando pasta isolada '{local}': projetos e bloqueios não são compartilhados com outras estações.");
                return new Plant(local, false);
            }
        }

        public static void WriteAtomic(string path, string content)
        {
            string tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            try { if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path); }
            catch (IOException) when (File.Exists(path)) { File.Copy(tmp, path, true); File.Delete(tmp); } // corrida com outra estação
        }
    }

    /// <summary>
    /// Arrendamentos entre estações. Cada arrendamento é um arquivo em Locks\ mantido ABERTO pelo Host com
    /// compartilhamento só de leitura e DeleteOnClose:
    ///  - outra estação que tente o mesmo recurso recebe violação de compartilhamento → "em uso por X" (lê o dono);
    ///  - se o Host terminar (até por queda), o Windows fecha o arquivo e o apaga: nada fica preso.
    /// </summary>
    public sealed class LeaseManager : IDisposable
    {
        private readonly Plant _plant;
        private readonly StationInfo _station;
        private readonly Dictionary<string, FileStream> _held = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new object();

        public LeaseManager(Plant plant, StationInfo station) { _plant = plant; _station = station; }

        public static string FileFor(string key)
        {
            string safe = new string(key.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '.' ? c : '_').ToArray());
            if (safe.Length > 60) safe = safe.Substring(0, 60);
            using (var sha = SHA1.Create())
                return safe + "-" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key.ToLowerInvariant()))).Replace("-", "").Substring(0, 10) + ".lease";
        }

        /// <summary>Tenta arrendar. Devolve null se conseguiu; senão o arrendamento de quem está com o recurso.</summary>
        public LeaseInfo TryAcquire(string key, string detail = null)
        {
            lock (_gate)
            {
                if (_held.ContainsKey(key)) return null;
                string path = Path.Combine(_plant.Locks, FileFor(key));
                var info = new LeaseInfo
                {
                    Key = key, Station = _station.Id, User = _station.User, SessionId = _station.SessionId,
                    ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id, SinceUtc = DateTime.UtcNow, Detail = detail,
                };
                FileStream fs;
                try { fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.DeleteOnClose); }
                catch (IOException) { return Read(path) ?? new LeaseInfo { Key = key, Station = "?", Detail = "em uso" }; }
                catch (UnauthorizedAccessException) { return Read(path) ?? new LeaseInfo { Key = key, Station = "?", Detail = "sem acesso" }; }
                var bytes = Encoding.UTF8.GetBytes(Json.Serialize(info));
                fs.SetLength(0); fs.Write(bytes, 0, bytes.Length); fs.Flush(true);
                _held[key] = fs;
                return null;
            }
        }

        public bool Holds(string key) { lock (_gate) return _held.ContainsKey(key); }

        public void Release(string key)
        {
            lock (_gate)
                if (_held.TryGetValue(key, out var fs)) { _held.Remove(key); fs.Dispose(); }
        }

        /// <summary>Dono atual de um recurso (null = livre ou nosso).</summary>
        public LeaseInfo Owner(string key)
        {
            if (Holds(key)) return null;
            string path = Path.Combine(_plant.Locks, FileFor(key));
            if (!File.Exists(path)) return null;
            // Arquivo sem dono (ex.: queda de energia do servidor): se der para abri-lo exclusivo, está livre.
            try { using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } try { File.Delete(path); } catch { } return null; }
            catch (IOException) { return Read(path); }
            catch (UnauthorizedAccessException) { return Read(path); }
        }

        /// <summary>Todos os arrendamentos vivos da planta (todas as estações).</summary>
        public List<LeaseInfo> List()
        {
            var list = new List<LeaseInfo>();
            foreach (var f in Directory.EnumerateFiles(_plant.Locks, "*.lease"))
            {
                LeaseInfo l;
                string key;
                lock (_gate) key = _held.FirstOrDefault(kv => string.Equals(kv.Value.Name, f, StringComparison.OrdinalIgnoreCase)).Key;
                if (key != null) { l = Read(f, own: true); if (l != null) { l.Mine = true; list.Add(l); } continue; }
                try { using (new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } try { File.Delete(f); } catch { } continue; } // órfão
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                l = Read(f);
                if (l != null) list.Add(l);
            }
            return list.OrderBy(l => l.Key).ToList();
        }

        private LeaseInfo Read(string path, bool own = false)
        {
            try
            {
                if (own)
                {
                    lock (_gate)
                    {
                        var fs = _held.Values.First(v => string.Equals(v.Name, path, StringComparison.OrdinalIgnoreCase));
                        fs.Position = 0;
                        var buf = new byte[fs.Length];
                        fs.Read(buf, 0, buf.Length);
                        return Json.Deserialize<LeaseInfo>(Encoding.UTF8.GetString(buf));
                    }
                }
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(fs, Encoding.UTF8))
                {
                    string txt = r.ReadToEnd();
                    return txt.Length == 0 ? null : Json.Deserialize<LeaseInfo>(txt);
                }
            }
            catch { return null; }
        }

        public void Dispose()
        {
            lock (_gate) { foreach (var fs in _held.Values) try { fs.Dispose(); } catch { } _held.Clear(); }
        }
    }

    /// <summary>Trilha de auditoria comum da planta: uma linha JSON por ação relevante (quem, de qual estação, o quê).</summary>
    public sealed class AuditLog
    {
        private readonly Plant _plant;
        private readonly StationInfo _station;
        public AuditLog(Plant plant, StationInfo station) { _plant = plant; _station = station; }

        public void Write(string action, string target, string detail = null)
        {
            var line = Json.Serialize(new Dictionary<string, object>
            {
                ["timeUtc"] = DateTime.UtcNow, ["station"] = _station.Id, ["user"] = _station.User, ["session"] = _station.SessionId,
                ["action"] = action, ["target"] = target, ["detail"] = detail,
            }).Replace("\r", "").Replace("\n", "") + Environment.NewLine;
            string path = Path.Combine(_plant.Audit, $"audit-{DateTime.UtcNow:yyyyMM}.log");
            var bytes = Encoding.UTF8.GetBytes(line);
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                        fs.Write(bytes, 0, bytes.Length);
                    return;
                }
                catch (IOException) { System.Threading.Thread.Sleep(20); }
            }
        }
    }
}
