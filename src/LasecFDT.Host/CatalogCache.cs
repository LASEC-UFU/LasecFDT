using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LasecFDT.Core;
using LasecFDT.Core.Catalog;
using LasecFDT.Core.Rpc;
using LasecFDT.Fdt1.Discovery;
using Microsoft.Win32;

namespace LasecFDT.Host
{
    /// <summary>
    /// Catálogo de DTMs e descrições (tipos de dispositivo), calculados UMA vez por servidor e lidos por todas as
    /// estações a partir de Cache\. Recalculado quando muda o ModificationFlag do FDT (§7.1.7), quando o cache tem mais
    /// de um dia, ou a pedido ("Atualizar catálogo"). Um arrendamento evita que 8 estações varram ao mesmo tempo.
    /// </summary>
    public sealed class CatalogCache
    {
        private readonly Plant _plant;
        private readonly LeaseManager _leases;
        private readonly StationInfo _station;
        private readonly Func<string, Task<DtmDescription>> _describe;
        private readonly SemaphoreSlim _describeGate = new SemaphoreSlim(1, 1);
        private CatalogDto _memory;
        private string _memoryFlag;

        public static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);
        private string CatalogFile => Path.Combine(_plant.Cache, "dtm-catalog.json");
        private string InfoDir => Path.Combine(_plant.Cache, "dtm-info");

        public CatalogCache(Plant plant, LeaseManager leases, StationInfo station, Func<string, Task<DtmDescription>> describe)
        {
            _plant = plant; _leases = leases; _station = station; _describe = describe;
            Directory.CreateDirectory(InfoDir);
        }

        private sealed class CacheFile
        {
            public string ModificationFlag { get; set; }
            public string GeneratedBy { get; set; }
            public CatalogDto Catalog { get; set; }
        }

        public static string CurrentModificationFlag()
        {
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                try
                {
                    using (var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var k = hk.OpenSubKey(@"SOFTWARE\FDT\DTMCatalogUpdates"))
                    {
                        var v = k?.GetValue("ModificationFlag");
                        if (v != null) return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
                catch { }
            }
            return null;
        }

        public CatalogDto Get(bool refresh)
        {
            string flag = CurrentModificationFlag();
            if (!refresh)
            {
                var c = _memory != null && _memoryFlag == flag ? _memory
                      : ReadFile()?.Let(f => f.ModificationFlag == flag && DateTime.UtcNow - f.Catalog.GeneratedAtUtc < MaxAge ? f.Catalog : null);
                if (c != null) { c.FromCache = true; _memory = c; _memoryFlag = flag; return Attach(c); }
            }
            // Só uma estação varre; as outras esperam o resultado dela (até 60 s) em vez de repetir o trabalho.
            const string key = "catalog-scan";
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (_leases.TryAcquire(key, "atualizando o catálogo de DTMs") != null && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(500);
                var other = ReadFile();
                if (other != null && other.ModificationFlag == flag && DateTime.UtcNow - other.Catalog.GeneratedAtUtc < TimeSpan.FromMinutes(2))
                { other.Catalog.FromCache = true; _memory = other.Catalog; _memoryFlag = flag; return Attach(other.Catalog); }
            }
            try
            {
                var scan = DtmDiscovery.Scan(readTypeLibs: false);
                var dto = new CatalogDto
                {
                    GeneratedAtUtc = DateTime.UtcNow,
                    GeneratedBy = _station.Id,
                    Dtms = scan.Dtms.Select(ToEntry).OrderBy(d => d.Vendor).ThenBy(d => d.Name).ToList(),
                };
                Plant.WriteAtomic(CatalogFile, Json.Serialize(new CacheFile { ModificationFlag = flag, GeneratedBy = _station.Id, Catalog = dto }));
                _memory = dto; _memoryFlag = flag;
                return Attach(dto);
            }
            finally { _leases.Release(key); }
        }

        private CacheFile ReadFile()
        {
            try { return File.Exists(CatalogFile) ? Json.Deserialize<CacheFile>(File.ReadAllText(CatalogFile)) : null; }
            catch { return null; }
        }

        /// <summary>Junta as descrições já conhecidas (cache da planta) às entradas do catálogo.</summary>
        private CatalogDto Attach(CatalogDto c)
        {
            foreach (var e in c.Dtms) Apply(e, ReadInfo(e));
            return c;
        }

        /// <summary>
        /// Nome e fabricante do próprio DTM (GetInformation) valem mais que os do registro, que em muitos DTMs são
        /// genéricos ("DTMCore Class", "TODO: &lt;Company name&gt;").
        /// </summary>
        private static void Apply(CatalogEntryDto e, DtmDescription d)
        {
            if (d == null) return;
            e.DeviceTypes = d.DeviceTypes;
            if (!string.IsNullOrWhiteSpace(d.Name)) e.Name = d.Name;
            if (!string.IsNullOrWhiteSpace(d.Vendor)) e.Vendor = d.Vendor;
            if (!string.IsNullOrWhiteSpace(d.Version)) e.Version = d.Version;
        }

        public static CatalogEntryDto ToEntry(DtmCatalogEntry d)
        {
            var roles = d.Roles ?? new List<string>();
            string role = roles.Contains("Communication") ? (roles.Contains("Device") ? "Gateway" : "Communication")
                        : roles.Contains("Channel") && !roles.Contains("Device") ? "Gateway" : "Device";
            return new CatalogEntryDto
            {
                Clsid = d.Clsid?.Trim('{', '}').ToUpperInvariant(),
                ProgId = d.ProgId,
                Name = d.Name ?? d.Server?.FileDescription ?? d.ProgId,
                Vendor = d.Server?.CompanyName,
                Version = d.Server?.FileVersion,
                FileVersion = d.Server?.FileVersion,
                Role = role,
                Roles = roles,
                Fieldbuses = d.Fieldbuses ?? new List<string>(),
                Hostable = d.RequiredHostArch == "x86" && (d.Server?.FileExists ?? false),
                Issues = d.Issues ?? new List<string>(),
            };
        }

        // ---------------------------------------------------------------- descrições (tipos de dispositivo)

        private string InfoFile(CatalogEntryDto e) =>
            Path.Combine(InfoDir, $"{e.Clsid}_{Station.Sanitize(e.FileVersion ?? "0")}_v2.json");

        private DtmDescription ReadInfo(CatalogEntryDto e)
        {
            try { var f = InfoFile(e); return File.Exists(f) ? Json.Deserialize<DtmDescription>(File.ReadAllText(f)) : null; }
            catch { return null; }
        }

        /// <summary>Descrição do DTM: do cache da planta, ou instanciando-o num worker descartável (uma vez por servidor).</summary>
        public async Task<DtmDescription> DescribeAsync(string clsid)
        {
            var e = (_memory ?? Get(false)).Dtms.FirstOrDefault(d => string.Equals(d.Clsid, clsid?.Trim('{', '}'), StringComparison.OrdinalIgnoreCase))
                    ?? throw new RpcException(RpcException.InvalidParams, "DTM fora do catálogo: " + clsid);
            var cached = ReadInfo(e);
            if (cached != null) { Apply(e, cached); return cached; }
            await _describeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                string key = "describe:" + e.Clsid;
                var deadline = DateTime.UtcNow.AddSeconds(90);
                while (_leases.TryAcquire(key, "descrevendo " + e.Name) != null && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(500).ConfigureAwait(false);
                    if ((cached = ReadInfo(e)) != null) { Apply(e, cached); return cached; }
                }
                try
                {
                    var d = await _describe(e.Clsid).ConfigureAwait(false);
                    Plant.WriteAtomic(InfoFile(e), Json.Serialize(d));
                    Apply(e, d);
                    return d;
                }
                finally { _leases.Release(key); }
            }
            finally { _describeGate.Release(); }
        }
    }

    public static class CatalogExtensions
    {
        /// <summary>Garante as descrições (tipos de dispositivo e identidade) de todos os DTMs hospedáveis que não são
        /// de comunicação. Na primeira vez no servidor cada DTM é instanciado num worker descartável.</summary>
        public static async Task<List<CatalogEntryDto>> DescribedDevicesAsync(this CatalogCache c, Action<string> progress = null)
        {
            var all = c.Get(false).Dtms;
            var todo = all.Where(e => e.Hostable && e.Role != "Communication" && e.DeviceTypes == null).ToList();
            for (int i = 0; i < todo.Count; i++)
            {
                progress?.Invoke($"Lendo DTM {i + 1}/{todo.Count}: {todo[i].Name}");
                try { await c.DescribeAsync(todo[i].Clsid).ConfigureAwait(false); } catch { } // DTM que não instancia: fica sem candidatos
            }
            return all;
        }
    }

    internal static class FuncExt
    {
        public static TR Let<T, TR>(this T v, Func<T, TR> f) => f(v);
    }
}
