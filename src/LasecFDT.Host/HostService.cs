using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using LasecFDT.Core;
using LasecFDT.Core.Rpc;
using LasecFDT.Supervisor;

namespace LasecFDT.Host
{
    /// <summary>
    /// Backend de uma estação: identidade, pasta da planta, arrendamentos, catálogo compartilhado, workspace e o
    /// supervisor dos workers. Atende a UI por JSON-RPC (um <see cref="JsonLineChannel"/>); requisições rodam em
    /// paralelo e as que mexem no projeto são serializadas pelo próprio <see cref="Workspace"/>.
    /// </summary>
    public sealed class HostService : IDisposable
    {
        private readonly JsonLineChannel _ui;
        private readonly Supervisor.Supervisor _sup;
        private readonly LeaseManager _leases;
        private readonly AuditLog _audit;
        private readonly CatalogCache _catalog;
        private readonly Workspace _ws;
        private StationSettings _settings;

        public HostInfo Info { get; }
        public Plant Plant { get; }
        public Workspace Workspace => _ws;

        public HostService(JsonLineChannel ui, string plantDir = null, HostLimits limits = null)
        {
            _ui = ui;
            var warnings = new List<string>();
            var station = Station.Detect();
            Plant = Plant.Open(plantDir, warnings);
            _leases = new LeaseManager(Plant, station);

            // A mesma estação em duas sessões (raro: thin client com duas conexões) ganha um sufixo de sessão,
            // para que preferências, logs e arrendamentos nunca se misturem.
            var other = _leases.TryAcquire("station:" + station.Id, "estação ativa");
            if (other != null)
            {
                station.Id = $"{station.Id}-S{station.SessionId}";
                _leases.TryAcquire("station:" + station.Id, "estação ativa");
                warnings.Add($"Outra sessão ({other.SessionId}) já usa o nome desta estação; esta sessão aparece como {station.Id}.");
            }

            string sessionDir = Path.Combine(Path.GetTempPath(), "LasecFDT", $"S{station.SessionId}-{station.Id}");
            Directory.CreateDirectory(sessionDir);
            _audit = new AuditLog(Plant, station);
            var lim = limits ?? new HostLimits();
            if (int.TryParse(Environment.GetEnvironmentVariable("LASECFDT_MAX_WORKERS"), out int mw) && mw > 0) lim.MaxWorkers = mw;
            if (int.TryParse(Environment.GetEnvironmentVariable("LASECFDT_IDLE_MINUTES"), out int im) && im >= 0) lim.IdleUnloadMinutes = im;

            // Logs dos workers por sessão (nunca numa pasta comum a todas as estações).
            string logDir = Environment.GetEnvironmentVariable("LASECFDT_LOGDIR");
            _sup = new Supervisor.Supervisor(WorkerLocator.Find(), new SupervisorOptions { LogDirectory = string.IsNullOrEmpty(logDir) ? null : Path.Combine(logDir, $"S{station.SessionId}-{station.Id}") });
            _catalog = new CatalogCache(Plant, _leases, station, clsid => _sup.DescribeAsync(clsid));
            _settings = LoadSettings(station);
            _ws = new Workspace(_sup, Plant, _leases, _audit, station, lim,
                clsid => _catalog.Get(false).Dtms.FirstOrDefault(d => string.Equals(d.Clsid, clsid?.Trim('{', '}'), StringComparison.OrdinalIgnoreCase)),
                e => Notify(HostMethods.Event, e), n => Notify(HostMethods.NodeChanged, n))
            {
                Lcid = LcidOf(_settings.Language),
                DescribedCatalog = progress => _catalog.DescribedDevicesAsync(progress),
            };

            Info = new HostInfo
            {
                Version = typeof(HostService).Assembly.GetName().Version.ToString(),
                Station = station, PlantDirectory = Plant.Root, PlantShared = Plant.Shared, SessionDirectory = sessionDir, Limits = lim, Warnings = warnings,
            };
        }

        private void Notify(string method, object payload)
        {
            try { _ui?.Write(new RpcMessage { Method = method, Params = JsonLineChannel.ToNode(payload) }); } catch { }
        }

        // ================================================================ laço

        private int _active; // requisições em andamento ou na fila (sem contar o próprio "Parar")

        public async Task RunAsync()
        {
            var pending = new List<Task>();
            while (true)
            {
                var m = await _ui.ReadAsync().ConfigureAwait(false);
                if (m == null) break;              // a UI fechou
                if (!m.IsRequest) continue;
                pending.Add(Task.Run(() => HandleAsync(m)));
                pending.RemoveAll(t => t.IsCompleted);
            }
            // Fechar a janela no meio de uma operação (ex.: busca de minutos) não espera por ela: interrompe como o botão
            // Parar; o que estiver ocioso é liberado pelo protocolo em seguida (Dispose, com tempo limitado).
            if (pending.Any(t => !t.IsCompleted)) _ws.Cancel(Volatile.Read(ref _active));
            await Task.WhenAny(Task.WhenAll(pending), Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
        }

        private async Task HandleAsync(RpcMessage m)
        {
            bool counted = m.Method != HostMethods.OperationCancel;
            if (counted) Interlocked.Increment(ref _active);
            try { await HandleCoreAsync(m).ConfigureAwait(false); }
            finally { if (counted) Interlocked.Decrement(ref _active); }
        }

        private async Task HandleCoreAsync(RpcMessage m)
        {
            RpcMessage reply;
            try { reply = new RpcMessage { Id = m.Id, Result = await DispatchAsync(m.Method, m.Params).ConfigureAwait(false) ?? JsonValue.Create(true) }; }
            catch (RpcException ex) { reply = new RpcMessage { Id = m.Id, Error = new RpcError { Code = ex.Code, Message = ex.Message } }; }
            catch (Exception ex)
            {
                var inner = ex is AggregateException a && a.InnerException != null ? a.InnerException : ex;
                reply = new RpcMessage { Id = m.Id, Error = new RpcError { Code = inner is RpcException r ? r.Code : RpcException.Internal, Message = inner.Message } };
            }
            _ui.Write(reply);
        }

        private static T P<T>(JsonNode n) where T : new() => n == null ? new T() : JsonLineChannel.FromNode<T>(n);
        private static JsonNode R<T>(T v) => JsonLineChannel.ToNode(v);

        public async Task<JsonNode> DispatchAsync(string method, JsonNode p)
        {
            switch (method)
            {
                case HostMethods.Hello: return R(Info);
                case HostMethods.Settings:
                {
                    var s = p == null ? null : JsonLineChannel.FromNode<StationSettings>(p);
                    if (s != null) { _settings = s; SaveSettings(); _ws.Lcid = LcidOf(s.Language); }
                    return R(_settings);
                }
                case HostMethods.Leases: return R(_leases.List());
                case HostMethods.Stations: return R(_leases.List().Where(l => l.Key.StartsWith("station:", StringComparison.Ordinal)).ToList());

                case HostMethods.CatalogList: return R(await Task.Run(() => _catalog.Get(P<CatalogParams>(p).Refresh)).ConfigureAwait(false));
                case HostMethods.CatalogDescribe: return R(await _catalog.DescribeAsync(P<DtmRef>(p).Clsid).ConfigureAwait(false));

                case HostMethods.ProjectList: return R(_ws.ListProjects());
                case HostMethods.ProjectNew: return R(await _ws.NewProjectAsync(P<ProjectRef>(p).Name).ConfigureAwait(false));
                case HostMethods.ProjectOpen:
                {
                    var r = await _ws.OpenProjectAsync(P<ProjectRef>(p).Name).ConfigureAwait(false);
                    _settings.LastProject = r.Name; SaveSettings();
                    return R(r);
                }
                case HostMethods.ProjectSave: return R(await _ws.SaveProjectAsync(p == null ? null : P<ProjectRef>(p).Name).ConfigureAwait(false));
                case HostMethods.ProjectClose: return JsonValue.Create(await _ws.CloseProjectAsync().ConfigureAwait(false));
                case HostMethods.ProjectCurrent: return _ws.Current() == null ? new JsonObject() : R(_ws.Current()); // {} = nenhum projeto

                case HostMethods.NodeAdd: return R(await _ws.AddNodeAsync(P<NodeAddParams>(p)).ConfigureAwait(false));
                case HostMethods.NodeRemove: return JsonValue.Create(await _ws.RemoveNodeAsync(P<NodeRef>(p).Tag).ConfigureAwait(false));
                case HostMethods.NodeRename: { var a = P<NodeRenameParams>(p); return R(await _ws.RenameNodeAsync(a.Tag, a.Name).ConfigureAwait(false)); }
                case HostMethods.NodeFunctions: return R(await _ws.FunctionsAsync(P<NodeRef>(p).Tag).ConfigureAwait(false));
                case HostMethods.NodeOpenUi: return R(await _ws.OpenUiAsync(P<NodeUiParams>(p)).ConfigureAwait(false));
                case HostMethods.NodeCloseUi: return JsonValue.Create(await _ws.CloseUiAsync(P<NodeRef>(p).Tag).ConfigureAwait(false));
                case HostMethods.NodeConnect: return R(await _ws.ConnectAsync(P<NodeConnectParams>(p)).ConfigureAwait(false));
                case HostMethods.NodeDisconnect: return R(await _ws.DisconnectAsync(P<NodeRef>(p).Tag).ConfigureAwait(false));
                case HostMethods.NodeCommLog: return R(await _ws.CommLogAsync(P<NodeRef>(p).Tag).ConfigureAwait(false));
                case HostMethods.NodeParameters: return R(await _ws.ParametersAsync(P<NodeRef>(p).Tag).ConfigureAwait(false));
                case HostMethods.NodeUpload: return R(await _ws.UploadCompareAsync(P<NodeRef>(p).Tag, keep: true).ConfigureAwait(false));
                case HostMethods.NodeCompare: return R(await _ws.UploadCompareAsync(P<NodeRef>(p).Tag, keep: false).ConfigureAwait(false));
                case HostMethods.NodeRevert: return R(await _ws.RevertAsync(P<NodeRef>(p).Tag).ConfigureAwait(false));
                case HostMethods.NodeDownload: return R(await _ws.DownloadAsync(P<NodeDownloadParams>(p)).ConfigureAwait(false));
                case HostMethods.NodeScan: return R(await _ws.ScanAsync(P<NodeScanParams>(p)).ConfigureAwait(false));
                case HostMethods.OperationCancel: return R(await Task.Run(() => _ws.Cancel(Volatile.Read(ref _active))).ConfigureAwait(false));
                case HostMethods.NodeUnload: return R(await _ws.UnloadAsync(P<NodeRef>(p).Tag).ConfigureAwait(false));

                default: throw new RpcException(RpcException.MethodNotFound, "Método desconhecido: " + method);
            }
        }

        // ================================================================ preferências da estação

        private string SettingsPath(StationInfo st) => Path.Combine(Plant.Stations, st.Id, "settings.json");

        private StationSettings LoadSettings(StationInfo st)
        {
            try { var f = SettingsPath(st); if (File.Exists(f)) return Json.Deserialize<StationSettings>(File.ReadAllText(f)) ?? new StationSettings(); }
            catch { }
            return new StationSettings();
        }

        private void SaveSettings()
        {
            var f = SettingsPath(Info.Station);
            Directory.CreateDirectory(Path.GetDirectoryName(f));
            Plant.WriteAtomic(f, Json.Serialize(_settings));
        }

        private static int LcidOf(string lang) =>
            string.Equals(lang, "en-US", StringComparison.OrdinalIgnoreCase) || string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase) ? 0x0409
            : string.Equals(lang, "de-DE", StringComparison.OrdinalIgnoreCase) ? 0x0407 : 0x0416;

        /// <summary>
        /// Fecha o projeto liberando os DTMs pelo protocolo, com tempo total limitado: um DTM que não responde não segura
        /// o fechamento da janela (os que sobrarem são encerrados à força, liberando portas e servidores de comunicação).
        /// </summary>
        public void Dispose()
        {
            var graceful = Task.Run(() => { _ws.Dispose(); _sup.Dispose(); });
            if (!graceful.Wait(TimeSpan.FromSeconds(12))) _sup.KillAll();
            _leases.Dispose();
        }
    }
}
