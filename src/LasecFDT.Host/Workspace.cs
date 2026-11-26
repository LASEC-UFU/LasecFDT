using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LasecFDT.Core;
using LasecFDT.Core.Project;
using LasecFDT.Core.Rpc;
using LasecFDT.Supervisor;

namespace LasecFDT.Host
{
    /// <summary>
    /// Projeto aberto nesta estação e o estado de execução de cada nó da topologia (PC → Comm DTM → [Gateway] → Device).
    /// Regras de economia de recursos (8 estações no mesmo servidor):
    ///  - um nó só ganha worker quando é usado (abrir tela, conectar, ler funções);
    ///  - nó ocioso (sem tela, sem conexão) é descarregado após <see cref="HostLimits.IdleUnloadMinutes"/>: o data set é
    ///    salvo em memória antes e o worker encerrado;
    ///  - teto de workers por estação (<see cref="HostLimits.MaxWorkers"/>).
    /// Regras de convivência entre estações: projeto aberto para edição por uma estação fica somente leitura nas
    /// outras; porta serial em uso por uma estação é recusada nas outras (arrendamentos em <see cref="LeaseManager"/>).
    /// </summary>
    public sealed class Workspace : IDisposable
    {
        private sealed class Node
        {
            public DeviceInstance Model;
            public DtmInstance Inst;
            public ChannelLink Link;           // este nó como filho conectado
            public string State = "offline";
            public string Policy;
            public bool UiOpen;
            public string Error;
            public bool Dirty;
            public bool Disconnecting;         // desconexão pedida pelo usuário: o online=False do DTM não é falha
            public string PortLease;           // só no nó raiz que segura a porta
            public PersistedDataSet Snapshot;  // data set de antes da última leitura do instrumento (para "restaurar")
            public DateTime LastUsed = DateTime.UtcNow;
        }

        private readonly Supervisor.Supervisor _sup;
        private readonly Plant _plant;
        private readonly LeaseManager _leases;
        private readonly AuditLog _audit;
        private readonly StationInfo _station;
        private readonly HostLimits _limits;
        private readonly Func<string, CatalogEntryDto> _catalogEntry;
        private readonly Action<HostEventDto> _event;
        private readonly Action<NodeDto> _nodeChanged;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        // ---------------------------------------------------------------- botão "Parar"
        // As operações do projeto passam uma de cada vez por _gate. Cada uma guarda a "época" em que entrou na fila;
        // Cancel() avança a época e encerra os workers ocupados: a que está rodando falha na hora (o DTM preso morre com
        // o processo dele) e as que esperavam desistem ao ganhar a vez.
        private int _epoch;
        private int _opEpoch; // época da operação que está com _gate (só uma por vez)

        private const string CanceledMessage = "Operação interrompida pelo operador.";
        private static RpcException Canceled() => new RpcException(RpcException.Canceled, CanceledMessage);
        public static bool IsCanceled(Exception ex) => (ex as RpcException ?? ex?.InnerException as RpcException)?.Code == RpcException.Canceled;

        private async Task EnterAsync()
        {
            int epoch = Volatile.Read(ref _epoch);
            await _gate.WaitAsync().ConfigureAwait(false);
            if (epoch != Volatile.Read(ref _epoch)) { _gate.Release(); throw Canceled(); }
            _opEpoch = epoch;
        }

        /// <summary>Ponto de parada entre etapas de uma operação longa.</summary>
        private void Check() { if (_opEpoch != Volatile.Read(ref _epoch)) throw Canceled(); }

        /// <summary>
        /// Botão "Parar": interrompe na hora a operação em andamento e as que estão na fila. Não passa por _gate.
        /// O DTM interrompido perde só o que não estava salvo (o data set do projeto fica como antes da operação).
        /// </summary>
        public CancelResultDto Cancel(int operations)
        {
            Interlocked.Increment(ref _epoch);
            var stopped = _sup.AbortBusy(CanceledMessage);
            var r = new CancelResultDto { Operations = operations };
            foreach (var i in stopped)
                if (i.SystemTag != null && _nodes.TryGetValue(i.SystemTag, out var n)) r.StoppedNodes.Add(n.Model.DisplayName);
            Emit("operation", null, "canceled",
                r.StoppedNodes.Count > 0 ? $"Interrompido pelo operador: {string.Join(", ", r.StoppedNodes)}" : "Interrompido pelo operador.", "warning");
            if (operations > 0 || stopped.Count > 0) _audit.Write("operation.cancel", _projectName, $"{operations} operação(ões); DTMs encerrados: {string.Join(", ", r.StoppedNodes)}");
            return r;
        }
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Node> _nodes =
            new System.Collections.Concurrent.ConcurrentDictionary<string, Node>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Catálogo com as descrições de todos os DTMs de dispositivo (para associar instrumentos encontrados na busca).</summary>
        public Func<Action<string>, Task<List<CatalogEntryDto>>> DescribedCatalog { get; set; }

        /// <summary>Idioma pedido aos DTMs (IDtm::SetLanguage); vem das preferências da estação.</summary>
        public int Lcid { get; set; } = 0x0416;
        private readonly Timer _idleTimer;

        private LasecProject _project;
        private string _projectName;
        private bool _readOnly;
        private string _lockedBy;
        private bool _dirty;

        public const string ProjectExtension = ".lasecfdt.json";

        public Workspace(Supervisor.Supervisor sup, Plant plant, LeaseManager leases, AuditLog audit, StationInfo station, HostLimits limits,
                         Func<string, CatalogEntryDto> catalogEntry, Action<HostEventDto> onEvent, Action<NodeDto> onNodeChanged)
        {
            _sup = sup; _plant = plant; _leases = leases; _audit = audit; _station = station; _limits = limits;
            _catalogEntry = catalogEntry; _event = onEvent; _nodeChanged = onNodeChanged;
            _sup.EventRaised += OnSupervisorEvent;
            _idleTimer = new Timer(_ => _ = SweepIdleAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        // ================================================================ projetos

        private static string ProjectKey(string name) => "project:" + name.Trim().ToLowerInvariant();
        private string ProjectPath(string name) => Path.Combine(_plant.Projects, Station.Sanitize(name).ToLowerInvariant() + ProjectExtension);

        public List<ProjectFileDto> ListProjects()
        {
            var list = new List<ProjectFileDto>();
            foreach (var f in Directory.EnumerateFiles(_plant.Projects, "*" + ProjectExtension))
            {
                try
                {
                    var p = Json.Deserialize<LasecProject>(File.ReadAllText(f));
                    var owner = _leases.Holds(ProjectKey(p.Name)) ? _station.Id : _leases.Owner(ProjectKey(p.Name))?.Station;
                    list.Add(new ProjectFileDto { Name = p.Name, ModifiedUtc = p.ModifiedUtc, Nodes = p.Devices.Count, LockedBy = owner });
                }
                catch { }
            }
            return list.OrderByDescending(p => p.ModifiedUtc).ToList();
        }

        public async Task<ProjectDto> NewProjectAsync(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw Bad("Informe o nome do projeto.");
            name = name.Trim();
            await EnterAsync().ConfigureAwait(false);
            try
            {
                if (File.Exists(ProjectPath(name))) throw Bad($"Já existe um projeto '{name}'.");
                await CloseCoreAsync().ConfigureAwait(false);
                var owner = _leases.TryAcquire(ProjectKey(name), "editando o projeto");
                if (owner != null) throw Bad($"O projeto '{name}' está sendo criado pela estação {owner.Station}.");
                _project = new LasecProject { Name = name };
                _projectName = name; _readOnly = false; _lockedBy = null;
                WriteProject();
                _audit.Write("project.new", name);
                return ProjectDto();
            }
            finally { _gate.Release(); }
        }

        public async Task<ProjectDto> OpenProjectAsync(string name)
        {
            await EnterAsync().ConfigureAwait(false);
            try
            {
                string path = ProjectPath(name ?? "");
                if (!File.Exists(path)) throw Bad($"Projeto '{name}' não encontrado.");
                await CloseCoreAsync().ConfigureAwait(false);
                var p = Json.Deserialize<LasecProject>(File.ReadAllText(path));
                var owner = _leases.TryAcquire(ProjectKey(p.Name), "editando o projeto");
                _project = p; _projectName = p.Name;
                _readOnly = owner != null; _lockedBy = owner?.Station;
                foreach (var d in p.Devices) _nodes[d.SystemTag] = new Node { Model = d };
                Emit("project", null, "opened", _readOnly ? $"{p.Name} (somente leitura: aberto para edição na estação {_lockedBy})" : p.Name);
                return ProjectDto();
            }
            finally { _gate.Release(); }
        }

        public async Task<ProjectDto> SaveProjectAsync(string saveAs = null)
        {
            await EnterAsync().ConfigureAwait(false);
            try
            {
                RequireProject();
                if (!string.IsNullOrWhiteSpace(saveAs) && !string.Equals(saveAs.Trim(), _projectName, StringComparison.OrdinalIgnoreCase))
                {
                    saveAs = saveAs.Trim();
                    if (File.Exists(ProjectPath(saveAs))) throw Bad($"Já existe um projeto '{saveAs}'.");
                    var owner = _leases.TryAcquire(ProjectKey(saveAs), "editando o projeto");
                    if (owner != null) throw Bad($"O projeto '{saveAs}' está em uso na estação {owner.Station}.");
                    if (!_readOnly) _leases.Release(ProjectKey(_projectName));
                    _projectName = _project.Name = saveAs; _readOnly = false; _lockedBy = null;
                }
                if (_readOnly) throw Bad($"Projeto aberto somente leitura (em edição na estação {_lockedBy}). Use 'Salvar como'.");
                foreach (var n in _nodes.Values.Where(n => n.Inst?.Alive == true))
                {
                    try { n.Model.DataSet = await _sup.SaveAsync(n.Inst).ConfigureAwait(false); n.Dirty = false; }
                    catch (Exception ex) { Emit("project", n.Model.SystemTag, "saveFailed", ex.Message, "warning"); }
                }
                WriteProject();
                _dirty = false;
                _audit.Write("project.save", _projectName, $"{_project.Devices.Count} nó(s)");
                foreach (var n in _nodes.Values) _nodeChanged(Dto(n));
                return ProjectDto();
            }
            finally { _gate.Release(); }
        }

        private void WriteProject()
        {
            _project.ModifiedUtc = DateTime.UtcNow;
            Plant.WriteAtomic(ProjectPath(_projectName), Json.Serialize(_project));
        }

        public async Task<bool> CloseProjectAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try { await CloseCoreAsync().ConfigureAwait(false); return true; }
            finally { _gate.Release(); }
        }

        private async Task CloseCoreAsync()
        {
            if (_project == null) return;
            // Filhos antes dos pais (§6.13.4): desliga e libera da folha para a raiz.
            foreach (var n in _nodes.Values.OrderByDescending(Depth).ToList())
                await ReleaseNodeAsync(n).ConfigureAwait(false);
            _nodes.Clear();
            if (!_readOnly) _leases.Release(ProjectKey(_projectName));
            Emit("project", null, "closed", _projectName);
            _project = null; _projectName = null; _readOnly = false; _lockedBy = null; _dirty = false;
        }

        public ProjectDto Current() => _project == null ? null : ProjectDto();

        private ProjectDto ProjectDto() => new ProjectDto
        {
            Name = _projectName, ReadOnly = _readOnly, LockedBy = _lockedBy, Dirty = _dirty || _nodes.Values.Any(n => n.Dirty),
            ModifiedUtc = _project.ModifiedUtc,
            Nodes = _project.Devices.Select(d => Dto(_nodes[d.SystemTag])).ToList(),
        };

        // ================================================================ nós

        public async Task<NodeDto> AddNodeAsync(NodeAddParams a)
        {
            await EnterAsync().ConfigureAwait(false);
            try
            {
                RequireWritable();
                var entry = _catalogEntry(a.Clsid) ?? throw Bad("DTM fora do catálogo: " + a.Clsid);
                if (!entry.Hostable) throw Bad($"O DTM {entry.Name} não pode ser hospedado ({string.Join("; ", entry.Issues)}).");
                Node parent = null;
                if (!string.IsNullOrEmpty(a.ParentTag))
                {
                    parent = Get(a.ParentTag);
                    if (parent.State == "online") throw Bad("Desconecte o nó pai antes de adicionar dispositivos a ele.");
                }
                RequireWorkerSlot();

                string channelPath = a.ChannelPath;
                if (parent != null && string.IsNullOrEmpty(channelPath))
                {
                    await EnsureLoadedAsync(parent).ConfigureAwait(false);
                    var chs = await _sup.GetChannelsAsync(parent.Inst).ConfigureAwait(false);
                    channelPath = chs.FirstOrDefault(c => c.IsCommunicationChannel)?.Path
                                  ?? throw Bad($"{parent.Model.DisplayName} não tem canal de comunicação: não aceita dispositivos.");
                }

                var inst = await _sup.CreateAsync(new CreateDtmParams { Dtm = entry.Clsid, DeviceTypeIndex = a.DeviceTypeIndex, Properties = a.Properties, Lcid = Lcid })
                    .ConfigureAwait(false);
                PersistedDataSet ds;
                try
                {
                    // Instrumento achado pela busca fora do endereço 0 (multidrop): o DTM precisa saber o endereço antes
                    // de conectar, senão pede ao canal o endereço 0 e "o instrumento não respondeu".
                    if (a.SlaveAddress is int address && parent != null) await WriteSlaveAddressAsync(inst, entry.Name, address).ConfigureAwait(false);
                    ds = await _sup.SaveAsync(inst).ConfigureAwait(false);
                }
                catch { await _sup.ReleaseAsync(inst).ConfigureAwait(false); throw; }

                var dt = entry.DeviceTypes?.FirstOrDefault(x => x.Index == a.DeviceTypeIndex);
                var model = new DeviceInstance
                {
                    SystemTag = inst.SystemTag,
                    DisplayName = string.IsNullOrWhiteSpace(a.Name) ? UniqueName(dt?.Name ?? inst.Summary.Name ?? entry.Name) : a.Name.Trim(),
                    DtmClsid = entry.Clsid, DtmProgId = entry.ProgId, DtmVersion = inst.Summary.Version ?? entry.Version,
                    DtmName = inst.Summary.Name ?? entry.Name, DtmVendor = inst.Summary.Vendor ?? entry.Vendor,
                    DeviceTypeXml = inst.Summary.DeviceTypeXml, DeviceTypeIndex = a.DeviceTypeIndex, DeviceTypeName = dt?.Name,
                    DataSet = ds, ParentTag = parent?.Model.SystemTag, ChannelPath = channelPath, Role = entry.Role,
                };
                _project.Devices.Add(model);
                var node = new Node { Model = model, Inst = inst, State = "loaded" };
                _nodes[model.SystemTag] = node;
                _dirty = true;
                _audit.Write("node.add", _projectName + "/" + model.DisplayName, $"{model.DtmName} [{model.SystemTag}] sob {parent?.Model.DisplayName ?? "PC"}");
                var dto = Dto(node);
                _nodeChanged(dto);
                return dto;
            }
            finally { _gate.Release(); }
        }

        private string UniqueName(string baseName)
        {
            baseName = string.IsNullOrWhiteSpace(baseName) ? "Dispositivo" : baseName.Trim();
            if (_project.Devices.All(d => !string.Equals(d.DisplayName, baseName, StringComparison.OrdinalIgnoreCase))) return baseName;
            for (int i = 2; ; i++)
                if (_project.Devices.All(d => !string.Equals(d.DisplayName, $"{baseName} ({i})", StringComparison.OrdinalIgnoreCase))) return $"{baseName} ({i})";
        }

        public async Task<bool> RemoveNodeAsync(string tag)
        {
            await EnterAsync().ConfigureAwait(false);
            try
            {
                RequireWritable();
                var root = Get(tag);
                var subtree = Descendants(root).Concat(new[] { root }).ToList(); // folhas primeiro
                foreach (var n in subtree) await ReleaseNodeAsync(n).ConfigureAwait(false);
                foreach (var n in subtree) { _project.Devices.Remove(n.Model); _nodes.TryRemove(n.Model.SystemTag, out _); }
                _dirty = true;
                _audit.Write("node.remove", _projectName + "/" + root.Model.DisplayName, $"{subtree.Count} nó(s)");
                return true;
            }
            finally { _gate.Release(); }
        }

        public async Task<NodeDto> RenameNodeAsync(string tag, string name)
        {
            await EnterAsync().ConfigureAwait(false);
            try
            {
                RequireWritable();
                if (string.IsNullOrWhiteSpace(name)) throw Bad("Informe o nome.");
                var n = Get(tag);
                n.Model.DisplayName = name.Trim();
                _dirty = true;
                var dto = Dto(n); _nodeChanged(dto); return dto;
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// Muda o endereço do instrumento no barramento (HART: endereço de polling) no DTM do instrumento:
        /// BusInformation/SlaveAddress do DTMParameterSchema, por IDtmParameter::SetParameters. Só desconectado — é o
        /// endereço que o DTM usa ao conectar. Não fala com o instrumento: para mudar o endereço DENTRO dele use a tela do
        /// DTM (HART: comando 6) e depois acerte aqui o mesmo número.
        /// </summary>
        public async Task<ParameterSetDto> SetAddressAsync(NodeAddressParams a)
        {
            await EnterAsync().ConfigureAwait(false);
            try
            {
                RequireWritable();
                var n = Get(a.Tag);
                if (n.Link != null || n.State == "online") throw Bad($"Desconecte {n.Model.DisplayName} antes de mudar o endereço no barramento.");
                await EnsureLoadedAsync(n).ConfigureAwait(false);
                await WriteSlaveAddressAsync(n.Inst, n.Model.DisplayName, a.Address).ConfigureAwait(false);
                n.Model.DataSet = await _sup.SaveAsync(n.Inst).ConfigureAwait(false);
                n.Dirty = true; _dirty = true;
                _audit.Write("node.setAddress", _projectName + "/" + n.Model.DisplayName, $"endereço no barramento = {a.Address}");
                Emit("project", a.Tag, "addressChanged", $"{n.Model.DisplayName}: endereço no barramento = {a.Address}");
                _nodeChanged(Dto(n));
                return ParameterModel.Parse(a.Tag, await _sup.GetParametersAsync(n.Inst).ConfigureAwait(false));
            }
            finally { _gate.Release(); }
        }

        /// <summary>Grava BusInformation/SlaveAddress no DTM e confere relendo os parâmetros.</summary>
        private async Task WriteSlaveAddressAsync(DtmInstance inst, string name, int address)
        {
            if (address < 0 || address > 63) throw Bad("Endereço fora da faixa 0 a 63.");
            string xml = await _sup.GetParametersAsync(inst).ConfigureAwait(false);
            string changed = ParameterModel.WithSlaveAddress(xml, address)
                ?? throw Bad($"O DTM de {name} não publica o endereço no barramento (BusInformation/SlaveAddress).");
            if (!await _sup.SetParametersAsync(inst, changed).ConfigureAwait(false))
                throw Bad($"O DTM de {name} recusou o endereço {address} (IDtmParameter::SetParameters).");
            var check = ParameterModel.Parse(name, await _sup.GetParametersAsync(inst).ConfigureAwait(false));
            string now = check.Items.FirstOrDefault(i => i.Name == ParameterModel.SlaveAddressName)?.Value;
            if (now != address.ToString(System.Globalization.CultureInfo.InvariantCulture))
                throw Bad($"O DTM de {name} aceitou o pedido mas continua no endereço {now ?? "?"}.");
        }

        public async Task<NodeFunctionsDto> FunctionsAsync(string tag)
        {
            await EnterAsync().ConfigureAwait(false);
            try
            {
                var n = Get(tag);
                await EnsureLoadedAsync(n).ConfigureAwait(false);
                var r = new NodeFunctionsDto { Functions = await _sup.GetFunctionsAsync(n.Inst).ConfigureAwait(false) };
                try
                {
                    foreach (var ch in await _sup.GetChannelsAsync(n.Inst).ConfigureAwait(false))
                    {
                        var c = new ChannelFunctionsDto { Path = ch.Path, IsCommunicationChannel = ch.IsCommunicationChannel };
                        if (ch.HasActiveX) c.Functions = await _sup.GetChannelFunctionsAsync(n.Inst, ch.Path).ConfigureAwait(false);
                        r.Channels.Add(c);
                    }
                }
                catch (RpcException) { } // DTM sem IDtmChannel
                return r;
            }
            finally { _gate.Release(); }
        }

        public async Task<UiResult> OpenUiAsync(NodeUiParams a)
        {
            await EnterAsync().ConfigureAwait(false);
            try
            {
                var n = Get(a.Tag);
                await EnsureLoadedAsync(n).ConfigureAwait(false);
                if (n.UiOpen) { await _sup.CloseUiAsync(n.Inst).ConfigureAwait(false); n.UiOpen = false; }
                var r = string.IsNullOrEmpty(a.ChannelPath)
                    ? await _sup.OpenUiAsync(n.Inst, new OpenUiParams { FunctionId = a.FunctionId, OwnerHwnd = a.OwnerHwnd, Embedded = true, Visible = true }).ConfigureAwait(false)
                    : await _sup.OpenChannelUiAsync(n.Inst, new ChannelUiParams { ChannelPath = a.ChannelPath, FunctionId = a.FunctionId, OwnerHwnd = a.OwnerHwnd, Embedded = true, Visible = true }).ConfigureAwait(false);
                n.UiOpen = r.Opened;
                n.LastUsed = DateTime.UtcNow;
                if (r.Opened) n.Dirty = true; // a tela pode alterar parâmetros; o data set é relido ao salvar
                _nodeChanged(Dto(n));
                return r;
            }
            finally { _gate.Release(); }
        }

        public async Task<bool> CloseUiAsync(string tag)
        {
            await EnterAsync().ConfigureAwait(false);
            try
            {
                var n = Get(tag);
                if (!n.UiOpen || n.Inst?.Alive != true) { n.UiOpen = false; return true; }
                bool ok = await _sup.CloseUiAsync(n.Inst).ConfigureAwait(false);
                n.UiOpen = false; n.LastUsed = DateTime.UtcNow;
                _nodeChanged(Dto(n));
                return ok;
            }
            finally { _gate.Release(); }
        }

        // ================================================================ conexão

        public async Task<NodeDto> ConnectAsync(NodeConnectParams a)
        {
            string policy = NormalizePolicy(a.Policy);
            if (policy == "Full" && !a.ConfirmFull)
                throw Bad("A política 'Full' permite ESCREVER no instrumento: exige confirmação explícita do operador.");
            await EnterAsync().ConfigureAwait(false);
            try
            {
                var n = Get(a.Tag);
                await ConnectCoreAsync(n, policy).ConfigureAwait(false);
                _audit.Write("node.connect", _projectName + "/" + n.Model.DisplayName, $"política {policy}{(policy == "Full" ? " (confirmada pelo operador)" : "")}; {n.State}");
                return Dto(n);
            }
            finally { _gate.Release(); }
        }

        private async Task ConnectCoreAsync(Node n, string policy)
        {
            if (n.State == "online" && n.Link != null && !n.Link.Broken) return;
            Check();
            if (string.IsNullOrEmpty(n.Model.ParentTag)) throw Bad($"{n.Model.DisplayName} está ligado direto ao PC: conecte os dispositivos abaixo dele.");
            var parent = Get(n.Model.ParentTag);
            if (!string.IsNullOrEmpty(parent.Model.ParentTag)) await ConnectCoreAsync(parent, policy).ConfigureAwait(false); // gateway: conecta o caminho acima primeiro

            var root = RootOf(n);
            await EnsureLoadedAsync(root).ConfigureAwait(false);
            if (root.PortLease == null) await AcquirePortAsync(root).ConfigureAwait(false);
            await EnsureLoadedAsync(parent).ConfigureAwait(false);
            await EnsureLoadedAsync(n).ConfigureAwait(false);

            n.State = "connecting"; n.Error = null; _nodeChanged(Dto(n));
            try
            {
                n.Link = await _sup.ConnectAsync(parent.Inst, n.Inst, policy, n.Model.ChannelPath).ConfigureAwait(false);
                n.Policy = policy;
                bool online = await _sup.WaitOnlineAsync(n.Inst, 15000).ConfigureAwait(false);
                if (!online)
                {
                    await _sup.DisconnectAsync(n.Link).ConfigureAwait(false);
                    n.Link = null;
                    throw Bad($"{n.Model.DisplayName}: o instrumento não respondeu (verifique endereço, modem/gateway e o laço).");
                }
                n.State = "online"; n.LastUsed = DateTime.UtcNow;
                parent.State = parent.State == "online" ? "online" : "loaded";
            }
            catch (Exception ex)
            {
                bool canceled = IsCanceled(ex);
                n.State = n.Inst?.Alive == true ? "loaded" : canceled ? "offline" : "error"; n.Error = canceled ? null : ex.Message; n.Link = null;
                ReleasePortIfIdle(root);
                throw;
            }
            finally { _nodeChanged(Dto(n)); }
        }

        public async Task<NodeDto> DisconnectAsync(string tag)
        {
            await EnterAsync().ConfigureAwait(false);
            try
            {
                var n = Get(tag);
                await DisconnectCoreAsync(n).ConfigureAwait(false);
                _audit.Write("node.disconnect", _projectName + "/" + n.Model.DisplayName);
                return Dto(n);
            }
            finally { _gate.Release(); }
        }

        private async Task DisconnectCoreAsync(Node n)
        {
            foreach (var c in Children(n).Where(c => c.Link != null).ToList()) await DisconnectCoreAsync(c).ConfigureAwait(false);
            n.Disconnecting = true;
            try
            {
                if (n.Link != null)
                {
                    try { await _sup.DisconnectAsync(n.Link).ConfigureAwait(false); n.Error = null; }
                    catch (Exception ex) { n.Error = ex.Message; }
                    n.Link = null;
                }
                if (n.State == "online" || n.State == "connecting") n.State = n.Inst?.Alive == true ? "loaded" : "offline";
            }
            finally { n.Disconnecting = false; }
            n.LastUsed = DateTime.UtcNow;
            ReleasePortIfIdle(RootOf(n));
            _nodeChanged(Dto(n));
        }

        public async Task<List<CommRecordDto>> CommLogAsync(string tag)
        {
            await EnterAsync().ConfigureAwait(false);
            try
            {
                var n = Get(tag);
                return n.Inst?.Alive == true ? await _sup.GetCommLogAsync(n.Inst).ConfigureAwait(false) : new List<CommRecordDto>();
            }
            finally { _gate.Release(); }
        }

        public async Task<NodeDto> UnloadAsync(string tag)
        {
            await EnterAsync().ConfigureAwait(false);
            try
            {
                var n = Get(tag);
                if (n.Link != null || Children(n).Any(c => c.Link != null)) throw Bad("Desconecte antes de descarregar.");
                await ReleaseNodeAsync(n).ConfigureAwait(false);
                return Dto(n);
            }
            finally { _gate.Release(); }
        }

        // ================================================================ parâmetros: ler, comparar, gravar (IDtmParameter / IDtmOnlineParameter)

        /// <summary>Parâmetros atuais do DTM (IDtmParameter::GetParameters na raiz do documento).</summary>
        public async Task<ParameterSetDto> ParametersAsync(string tag)
        {
            await EnterAsync().ConfigureAwait(false);
            try
            {
                var n = Get(tag);
                await EnsureLoadedAsync(n).ConfigureAwait(false);
                return ParameterModel.Parse(tag, await _sup.GetParametersAsync(n.Inst).ConfigureAwait(false));
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// Lê os parâmetros do instrumento (IDtmOnlineParameter::UploadRequest na raiz, §4.3.12.3) e compara com o que o
        /// DTM tinha (projeto). Ler (<paramref name="keep"/> = true): os valores do instrumento ficam no DTM e é possível
        /// restaurar os do projeto depois. Comparar (false): o projeto é restaurado em seguida — nada muda.
        /// </summary>
        public async Task<CompareResultDto> UploadCompareAsync(string tag, bool keep)
        {
            await EnterAsync().ConfigureAwait(false);
            try
            {
                var n = Get(tag);
                if (n.State != "online" || n.Link == null) throw Bad($"Conecte {n.Model.DisplayName} antes de ler ou comparar com o instrumento.");
                var snapshot = await _sup.SaveAsync(n.Inst).ConfigureAwait(false);
                n.Model.DataSet = snapshot; // se o operador mandar parar no meio, o DTM volta exatamente assim
                var before = ParameterModel.Parse(tag, await _sup.GetParametersAsync(n.Inst).ConfigureAwait(false));
                string verdict = null;
                try { verdict = ParameterModel.CompareVerdict(await _sup.OnlineCompareAsync(n.Inst).ConfigureAwait(false)); } catch (RpcException) { }
                int logBefore = (await _sup.GetCommLogAsync(n.Inst).ConfigureAwait(false)).Count;

                Check();
                Emit("project", tag, keep ? "uploadStarted" : "compareStarted", $"{n.Model.DisplayName}: lendo os parâmetros do instrumento");
                var up = await _sup.UploadAsync(n.Inst).ConfigureAwait(false);
                Check();
                var log = await _sup.GetCommLogAsync(n.Inst).ConfigureAwait(false);
                var after = ParameterModel.Parse(tag, await _sup.GetParametersAsync(n.Inst).ConfigureAwait(false));

                var r = new CompareResultDto
                {
                    Tag = tag, Upload = up, DtmVerdict = verdict, Items = ParameterModel.Diff(before, after),
                    RefusedCommands = log.Skip(logBefore).Count(x => !x.Forwarded),
                    RefusedNotes = log.Skip(logBefore).Where(x => !x.Forwarded).Select(x => x.Note).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList(),
                };
                r.Differences = r.Items.Count(i => i.Different);
                if (keep && up.Success)
                {
                    n.Snapshot = snapshot; n.Dirty = true; _dirty = true;
                    r.KeptDeviceValues = true; r.CanRevert = true;
                }
                else await RestoreAsync(n, snapshot).ConfigureAwait(false); // comparar, ou leitura que falhou: o projeto volta como estava

                Emit("project", tag, keep ? "uploadFinished" : "compareFinished",
                    $"{n.Model.DisplayName}: {(up.Success ? "leitura concluída" : "leitura falhou: " + up.Error)}; {r.Differences} diferença(s)" +
                    (r.RefusedCommands > 0 ? $"; {r.RefusedCommands} comando(s) recusado(s) pela política {n.Policy}" : ""), up.Success ? "info" : "warning");
                _audit.Write(keep ? "node.upload" : "node.compare", _projectName + "/" + n.Model.DisplayName,
                    $"sucesso={up.Success}; {r.Differences} diferença(s); veredito do DTM={verdict ?? "—"}");
                _nodeChanged(Dto(n));
                return r;
            }
            finally { _gate.Release(); }
        }

        /// <summary>Volta ao data set de antes da última leitura do instrumento.</summary>
        public async Task<NodeDto> RevertAsync(string tag)
        {
            await EnterAsync().ConfigureAwait(false);
            try
            {
                var n = Get(tag);
                var snap = n.Snapshot ?? throw Bad("Não há leitura do instrumento para desfazer.");
                await RestoreAsync(n, snap).ConfigureAwait(false);
                _audit.Write("node.revert", _projectName + "/" + n.Model.DisplayName);
                return Dto(n);
            }
            finally { _gate.Release(); }
        }

        /// <summary>Recarrega o DTM com um data set (worker novo) e, se estava conectado, reconecta com a mesma política.</summary>
        private async Task RestoreAsync(Node n, PersistedDataSet dataSet)
        {
            string policy = n.Link != null ? n.Policy : null;
            await ReleaseNodeAsync(n).ConfigureAwait(false);
            n.Model.DataSet = dataSet;
            n.Snapshot = null;
            await EnsureLoadedAsync(n).ConfigureAwait(false);
            if (policy != null) await ConnectCoreAsync(n, policy).ConfigureAwait(false);
            _nodeChanged(Dto(n));
        }

        /// <summary>
        /// Grava os parâmetros do DTM no instrumento (IDtmOnlineParameter::DownloadRequest na raiz, §4.3.12.2).
        /// Exige conexão com a política "Full" (confirmada ao conectar) e nova confirmação do operador; vai para a auditoria.
        /// </summary>
        public async Task<OnlineParameterResult> DownloadAsync(NodeDownloadParams a)
        {
            await EnterAsync().ConfigureAwait(false);
            try
            {
                var n = Get(a.Tag);
                if (n.State != "online" || n.Link == null) throw Bad($"Conecte {n.Model.DisplayName} antes de gravar no instrumento.");
                if (n.Policy != "Full") throw Bad($"{n.Model.DisplayName} está conectado como '{n.Policy}'. Para gravar, desconecte e conecte em modo completo (permite escrita).");
                if (!a.Confirm) throw Bad("Gravar no instrumento exige confirmação explícita do operador.");
                n.Model.DataSet = await _sup.SaveAsync(n.Inst).ConfigureAwait(false); // o que vai ser gravado fica no projeto mesmo se parar
                Check();
                Emit("project", a.Tag, "downloadStarted", $"{n.Model.DisplayName}: gravando os parâmetros no instrumento", "warning");
                OnlineParameterResult r;
                try { r = await _sup.DownloadAsync(n.Inst).ConfigureAwait(false); }
                catch (Exception ex) when (IsCanceled(ex))
                {
                    const string partial = "gravação interrompida pelo operador: o instrumento pode ter ficado parcialmente gravado";
                    _audit.Write("node.download", _projectName + "/" + n.Model.DisplayName, partial);
                    Emit("project", a.Tag, "downloadCanceled", $"{n.Model.DisplayName}: {partial}", "error");
                    throw new RpcException(RpcException.Canceled, "Gravação interrompida. O instrumento pode ter ficado parcialmente gravado: confira os parâmetros (Comparar) antes de usá-lo.");
                }
                _audit.Write("node.download", _projectName + "/" + n.Model.DisplayName, $"sucesso={r.Success}; {r.Error}");
                Emit("project", a.Tag, "downloadFinished", $"{n.Model.DisplayName}: {(r.Success ? "gravação concluída" : "gravação falhou: " + r.Error)}", r.Success ? "info" : "error");
                n.Snapshot = null;
                return r;
            }
            finally { _gate.Release(); }
        }

        // ================================================================ busca de instrumentos (topology scan)

        /// <summary>
        /// Busca de instrumentos no canal do nó (IFdtChannelSubTopology::ScanRequest → IDtmEvents::OnScanResponse,
        /// DTMTopologyScanSchema), como a "busca de topologia" do PACTware. Só lê: o Comm DTM varre os endereços com
        /// comandos de identificação. A porta fica arrendada durante a busca. Cada instrumento volta com os DTMs
        /// candidatos do catálogo: mesmo fabricante e tipo ("exact"), mesmo fabricante ("vendor") ou mesmo protocolo.
        /// </summary>
        public async Task<ScanResultDto> ScanAsync(NodeScanParams a)
        {
            // Descrições antes do bloqueio do projeto: na 1ª vez no servidor cada DTM é lido num worker descartável.
            // O "Parar" também vale nesta fase (antes da fila do projeto): o worker descartável em leitura é encerrado e
            // o próximo DTM da lista não começa.
            int epoch = Volatile.Read(ref _epoch);
            var catalog = DescribedCatalog == null ? new List<CatalogEntryDto>()
                : await DescribedCatalog(m =>
                {
                    if (epoch != Volatile.Read(ref _epoch)) throw Canceled();
                    Emit("catalog", a.Tag, "describing", m);
                }).ConfigureAwait(false);
            if (epoch != Volatile.Read(ref _epoch)) throw Canceled();
            await EnterAsync().ConfigureAwait(false);
            try
            {
                var n = Get(a.Tag);
                await EnsureLoadedAsync(n).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(n.Model.ParentTag)) await ConnectCoreAsync(n, "ReadOnly").ConfigureAwait(false); // gateway: o caminho acima conectado
                else await _sup.EnsureCommunicationRootAsync(n.Inst).ConfigureAwait(false);
                var chs = await _sup.GetChannelsAsync(n.Inst).ConfigureAwait(false);
                var ch = chs.FirstOrDefault(c => c.HasSubTopology && c.IsCommunicationChannel) ?? chs.FirstOrDefault(c => c.HasSubTopology)
                         ?? throw Bad($"{n.Model.DisplayName} não oferece busca de instrumentos (nenhum canal com IFdtChannelSubTopology).");
                var root = RootOf(n);
                if (root.PortLease == null) await AcquirePortAsync(root).ConfigureAwait(false);
                Emit("project", n.Model.SystemTag, "scanStarted", $"Buscando instrumentos em {n.Model.DisplayName} ({ch.Path})");
                ScanResult r;
                try { r = await _sup.ScanAsync(n.Inst, ch.Path, a.TimeoutMs).ConfigureAwait(false); }
                finally { ReleasePortIfIdle(root); n.LastUsed = DateTime.UtcNow; }
                Check();
                if (!r.Accepted || !r.Completed) throw Bad($"{n.Model.DisplayName}: {r.Error ?? "a busca não terminou"}");

                var dto = new ScanResultDto { Tag = n.Model.SystemTag, ChannelPath = ch.Path, DurationMs = r.DurationMs, Xml = r.Xml, Devices = ParseScan(r.Xml) };
                var children = Children(n).ToList();
                foreach (var d in dto.Devices)
                {
                    MatchDtms(d, catalog);
                    d.ExistingNodeTag = children.FirstOrDefault(c => string.Equals(c.Model.DisplayName?.Trim(), d.DeviceTag, StringComparison.OrdinalIgnoreCase))?.Model.SystemTag;
                }
                Emit("project", n.Model.SystemTag, "scanFinished", $"{dto.Devices.Count} instrumento(s) em {r.DurationMs / 1000.0:0.0} s");
                _audit.Write("node.scan", _projectName + "/" + n.Model.DisplayName, $"{dto.Devices.Count} instrumento(s): {string.Join(", ", dto.Devices.Select(d => d.DeviceTag))}");
                return dto;
            }
            finally { _gate.Release(); }
        }

        /// <summary>Instrumentos de um documento DTMTopologyScanSchema: elementos com manufacturerId e deviceTypeId.</summary>
        public static List<ScannedDeviceDto> ParseScan(string xml)
        {
            var list = new List<ScannedDeviceDto>();
            if (string.IsNullOrWhiteSpace(xml)) return list;
            var doc = new System.Xml.XmlDocument();
            doc.LoadXml(xml);
            foreach (var e in doc.GetElementsByTagName("*").OfType<System.Xml.XmlElement>())
            {
                if (e.LocalName == "LongAddress") continue; // faz parte do instrumento (elemento pai)
                int? m = Fdt1.Frame.FdtXml.IntAttr(e, "manufacturerId"), t = Fdt1.Frame.FdtXml.IntAttr(e, "deviceTypeId");
                if (m == null || t == null) continue;
                var d = new ScannedDeviceDto
                {
                    Protocol = e.LocalName.StartsWith("HART", StringComparison.OrdinalIgnoreCase) ? "HART" : e.LocalName,
                    ManufacturerId = m, DeviceTypeId = t,
                    ShortAddress = Fdt1.Frame.FdtXml.IntAttr(e, "shortAddress"),
                    DeviceTag = e.Attributes.OfType<System.Xml.XmlAttribute>().FirstOrDefault(x => x.LocalName == "tag")?.Value?.Trim(),
                };
                var la = e.ChildNodes.OfType<System.Xml.XmlElement>().FirstOrDefault(c => c.LocalName == "LongAddress");
                if (la != null)
                {
                    int? a1 = Fdt1.Frame.FdtXml.IntAttr(la, "address1"), a2 = Fdt1.Frame.FdtXml.IntAttr(la, "address2"), a3 = Fdt1.Frame.FdtXml.IntAttr(la, "address3");
                    if (a1 != null && a2 != null && a3 != null) d.LongAddress = $"{m:X2}{t:X2}{a1:X2}{a2:X2}{a3:X2}";
                }
                if (string.IsNullOrEmpty(d.DeviceTag)) d.DeviceTag = d.ShortAddress != null ? $"{d.Protocol}-{d.ShortAddress}" : $"{d.Protocol}-{d.LongAddress}";
                list.Add(d);
            }
            return list;
        }

        /// <summary>Candidatos: mesmo fabricante e tipo (declarados pelo DTM), depois mesmo fabricante, depois mesmo protocolo.</summary>
        public static void MatchDtms(ScannedDeviceDto d, List<CatalogEntryDto> catalog)
        {
            var devices = catalog.Where(e => e.Hostable && e.Role != "Communication" && e.DeviceTypes != null).ToList();
            DtmCandidateDto C(CatalogEntryDto e, DeviceTypeDto t, string match) => new DtmCandidateDto
            { Clsid = e.Clsid, Name = e.Name, Vendor = e.Vendor, DeviceTypeIndex = t?.Index ?? 0, DeviceTypeName = t?.Name ?? e.Name, Match = match };

            var exact = devices.SelectMany(e => e.DeviceTypes.Where(t => t.ManufacturerId == d.ManufacturerId && t.DeviceTypeId == d.DeviceTypeId).Select(t => C(e, t, "exact"))).ToList();
            d.ManufacturerName = devices.SelectMany(e => e.DeviceTypes.Where(t => t.ManufacturerId == d.ManufacturerId).Select(t => t.Vendor ?? e.Vendor))
                                        .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            bool SameProtocol(CatalogEntryDto e) =>
                e.Fieldbuses.Any(f => string.Equals(f, d.Protocol, StringComparison.OrdinalIgnoreCase))
                || e.DeviceTypes.Any(t => t.BusCategories.Any(b => string.Equals(b, d.Protocol, StringComparison.OrdinalIgnoreCase)));
            var vendor = d.ManufacturerName == null ? new List<DtmCandidateDto>() : devices
                .Where(e => string.Equals(e.Vendor, d.ManufacturerName, StringComparison.OrdinalIgnoreCase) || e.DeviceTypes.Any(t => string.Equals(t.Vendor, d.ManufacturerName, StringComparison.OrdinalIgnoreCase)))
                .Where(e => !exact.Any(x => x.Clsid == e.Clsid))
                .SelectMany(e => e.DeviceTypes.Where(t => t.ManufacturerId == null || t.ManufacturerId == d.ManufacturerId).Select(t => C(e, t, "vendor"))).ToList();
            var protocol = devices.Where(SameProtocol).Where(e => !exact.Any(x => x.Clsid == e.Clsid) && !vendor.Any(x => x.Clsid == e.Clsid))
                .SelectMany(e => e.DeviceTypes.Where(t => t.ManufacturerId == null).Select(t => C(e, t, "protocol"))).ToList();
            d.Candidates = exact.Concat(vendor.OrderBy(c => c.Name)).Concat(protocol.OrderBy(c => c.Vendor).ThenBy(c => c.Name)).ToList();
        }

        // ================================================================ porta (arrendamento entre estações)

        /// <summary>
        /// Arrenda a porta física usada pelo Comm DTM raiz. A porta vem do data set do DTM (propriedade "Port"; com
        /// "Interface" ≠ Serial — HART-IP — não há porta local). Porta redirecionada do thin client (RDP) é da estação;
        /// porta do servidor é da máquina.
        /// </summary>
        private async Task AcquirePortAsync(Node root)
        {
            PersistedDataSet ds;
            try { ds = await _sup.SaveAsync(root.Inst).ConfigureAwait(false); root.Model.DataSet = ds; }
            catch { ds = root.Model.DataSet; }
            string Prop(string name) => ds?.Properties?.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;
            string iface = Prop("Interface");
            string port = Prop("Port");
            if (string.IsNullOrWhiteSpace(port) || (iface != null && !iface.Equals("Serial", StringComparison.OrdinalIgnoreCase))) return;
            string key = PortKey(port.Trim(), _station);
            var owner = _leases.TryAcquire(key, $"{root.Model.DisplayName} ({_projectName})");
            if (owner != null)
                throw Bad($"A porta {port} está em uso pela estação {owner.Station} ({owner.User}, desde {owner.SinceUtc.ToLocalTime():HH:mm}).");
            root.PortLease = key;
            Emit("plant", root.Model.SystemTag, "portAcquired", key);
        }

        public static string PortKey(string port, StationInfo station)
        {
            string target = QueryDosDevice(port);
            bool redirected = target != null && target.IndexOf("RdpDr", StringComparison.OrdinalIgnoreCase) >= 0;
            return redirected ? $"port:{station.Id}:{port.ToUpperInvariant()}" : $"port:{station.Machine}:{port.ToUpperInvariant()}";
        }

        private void ReleasePortIfIdle(Node root)
        {
            if (root?.PortLease == null) return;
            if (Descendants(root).Any(d => d.Link != null)) return;
            _leases.Release(root.PortLease);
            Emit("plant", root.Model.SystemTag, "portReleased", root.PortLease);
            root.PortLease = null;
        }

        private static string QueryDosDevice(string device)
        {
            var sb = new StringBuilder(1024);
            return QueryDosDeviceW(device, sb, sb.Capacity) > 0 ? sb.ToString() : null;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryDosDeviceW", SetLastError = true)]
        private static extern int QueryDosDeviceW(string deviceName, StringBuilder target, int max);

        // ================================================================ ciclo de vida dos workers

        private async Task EnsureLoadedAsync(Node n)
        {
            n.LastUsed = DateTime.UtcNow;
            if (n.Inst?.Alive == true) return;
            Check();
            RequireWorkerSlot();
            n.State = "loading"; n.Error = null; _nodeChanged(Dto(n));
            try
            {
                n.Inst = n.Model.DataSet != null
                    ? await _sup.LoadAsync(new LoadParams { Dtm = n.Model.DtmClsid, SystemTag = n.Model.SystemTag, DataSet = n.Model.DataSet, Lcid = Lcid }).ConfigureAwait(false)
                    : await _sup.CreateAsync(new CreateDtmParams { Dtm = n.Model.DtmClsid, SystemTag = n.Model.SystemTag, DeviceTypeIndex = n.Model.DeviceTypeIndex, Lcid = Lcid }).ConfigureAwait(false);
                n.State = "loaded";
            }
            catch (Exception ex)
            {
                bool canceled = IsCanceled(ex);
                n.State = canceled ? "offline" : "error"; n.Error = canceled ? null : ex.Message; n.Inst = null;
                throw;
            }
            finally { _nodeChanged(Dto(n)); }
        }

        /// <summary>Salva o data set em memória e encerra o worker do nó (desconectando antes).</summary>
        private async Task ReleaseNodeAsync(Node n)
        {
            if (n.Link != null) await DisconnectCoreAsync(n).ConfigureAwait(false);
            if (n.Inst != null)
            {
                if (n.Inst.Alive)
                {
                    try { n.Model.DataSet = await _sup.SaveAsync(n.Inst).ConfigureAwait(false); } catch { }
                    try { await _sup.ReleaseAsync(n.Inst).ConfigureAwait(false); } catch { }
                }
                n.Inst = null;
            }
            if (n.PortLease != null) { _leases.Release(n.PortLease); n.PortLease = null; }
            n.UiOpen = false;
            if (n.State != "error") n.State = "offline";
            _nodeChanged(Dto(n));
        }

        private async Task SweepIdleAsync()
        {
            if (_limits.IdleUnloadMinutes <= 0 || _project == null) return;
            if (!await _gate.WaitAsync(0).ConfigureAwait(false)) return; // ocupado: tenta no próximo ciclo
            try
            {
                var limit = DateTime.UtcNow.AddMinutes(-_limits.IdleUnloadMinutes);
                foreach (var n in _nodes.Values.Where(n => n.Inst?.Alive == true && !n.UiOpen && n.Link == null && n.PortLease == null
                                                          && !Children(n).Any(c => c.Link != null) && n.LastUsed < limit).ToList())
                {
                    await ReleaseNodeAsync(n).ConfigureAwait(false);
                    Emit("worker", n.Model.SystemTag, "idleUnloaded", $"{n.Model.DisplayName}: descarregado após {_limits.IdleUnloadMinutes} min sem uso");
                }
            }
            catch { }
            finally { _gate.Release(); }
        }

        private void RequireWorkerSlot()
        {
            int alive = _sup.Instances.Count(i => i.Alive);
            if (alive >= _limits.MaxWorkers)
                throw Bad($"Limite de {_limits.MaxWorkers} DTMs carregados nesta estação. Feche telas ou descarregue dispositivos sem uso.");
        }

        // ================================================================ eventos do supervisor

        private void OnSupervisorEvent(SupervisorEvent e)
        {
            string tag = e.Instance?.SystemTag;
            Node n = null;
            if (tag != null) _nodes.TryGetValue(tag, out n);
            var we = e.WorkerEvent;
            switch (e.Kind)
            {
                case "worker" when we?.Kind == "comm":
                    Emit("comm", tag, null, we.Detail);
                    return;
                case "worker" when we?.Kind == "ui":
                    if (n != null && (we.Method == "closedByUser" || we.Method == "closedByDtm")) { n.UiOpen = false; _nodeChanged(Dto(n)); }
                    Emit("ui", tag, we.Method, we.Detail);
                    return;
                case "worker":
                    if (n != null && we?.Method == "OnOnlineStateChanged" && we.Detail == "online=False" && n.State == "online" && !n.Disconnecting)
                    { n.State = "loaded"; n.Error = "O DTM informou que ficou offline."; _nodeChanged(Dto(n)); }
                    if (n != null && (we?.Method == "SaveRequest" || we?.Method == "OnParameterChanged")) { n.Dirty = true; _nodeChanged(Dto(n)); }
                    Emit("frame", tag, we?.Method, we?.Detail, we?.Method == "OnErrorMessage" ? "warning" : "info");
                    return;
                case "died":
                    if (n != null) { n.Inst = null; n.Link = null; n.UiOpen = false; n.State = "error"; n.Error = "O processo do DTM terminou inesperadamente."; _nodeChanged(Dto(n)); }
                    Emit("worker", tag, "died", e.Detail, "error");
                    return;
                case "aborted":
                    // Botão Parar: não é falha. O nó volta a "offline" e carrega de novo no próximo uso, com o data set salvo.
                    if (n != null)
                    {
                        n.Inst = null; n.Link = null; n.UiOpen = false; n.State = "offline"; n.Error = null;
                        if (n.PortLease != null) { _leases.Release(n.PortLease); Emit("plant", tag, "portReleased", n.PortLease); n.PortLease = null; }
                        _nodeChanged(Dto(n));
                    }
                    Emit("worker", tag, "aborted", e.Detail, "warning");
                    return;
                case "linkBroken":
                    bool byOperator = e.Instance?.Worker?.AbortReason != null;
                    foreach (var c in _nodes.Values.Where(c => c.Link?.Broken == true).ToList())
                    {
                        c.Link = null;
                        c.State = c.Inst?.Alive == true ? "loaded" : byOperator ? "offline" : "error";
                        c.Error = byOperator ? null : "Conexão interrompida: " + e.Detail;
                        _nodeChanged(Dto(c));
                    }
                    if (byOperator) // a porta do Comm DTM raiz não fica presa
                        foreach (var r in _nodes.Values.Where(x => x.PortLease != null).ToList()) ReleasePortIfIdle(r);
                    Emit("worker", tag, "linkBroken", e.Detail, byOperator ? "info" : "error");
                    return;
                default:
                    Emit("worker", tag, e.Kind, e.Detail, "warning");
                    return;
            }
        }

        // ================================================================ utilitários

        private NodeDto Dto(Node n) => new NodeDto
        {
            Tag = n.Model.SystemTag, Name = n.Model.DisplayName, ParentTag = n.Model.ParentTag, ChannelPath = n.Model.ChannelPath,
            Role = n.Model.Role, DtmClsid = n.Model.DtmClsid, DtmName = n.Model.DtmName, Vendor = n.Model.DtmVendor,
            DeviceType = n.Model.DeviceTypeName, State = n.State, Policy = n.Link != null ? n.Policy : null, UiOpen = n.UiOpen,
            WorkerPid = n.Inst?.Alive == true ? n.Inst.Worker.ProcessId : (int?)null, Error = n.Error, Dirty = n.Dirty,
        };

        private Node Get(string tag)
        {
            RequireProject();
            return tag != null && _nodes.TryGetValue(tag, out var n) ? n : throw Bad("Nó inexistente: " + tag);
        }

        private IEnumerable<Node> Children(Node n) => _nodes.Values.Where(c => string.Equals(c.Model.ParentTag, n.Model.SystemTag, StringComparison.OrdinalIgnoreCase));

        /// <summary>Descendentes, das folhas para cima.</summary>
        private List<Node> Descendants(Node n)
        {
            var list = new List<Node>();
            foreach (var c in Children(n).ToList()) { list.AddRange(Descendants(c)); list.Add(c); }
            return list;
        }

        private Node RootOf(Node n)
        {
            var cur = n;
            for (int guard = 0; guard < 32 && !string.IsNullOrEmpty(cur.Model.ParentTag) && _nodes.TryGetValue(cur.Model.ParentTag, out var p); guard++) cur = p;
            return cur;
        }

        private int Depth(Node n)
        {
            int d = 0;
            for (var cur = n; !string.IsNullOrEmpty(cur.Model.ParentTag) && _nodes.TryGetValue(cur.Model.ParentTag, out var p) && d < 32; cur = p) d++;
            return d;
        }

        private void RequireProject() { if (_project == null) throw Bad("Nenhum projeto aberto."); }

        private void RequireWritable()
        {
            RequireProject();
            if (_readOnly) throw Bad($"Projeto somente leitura: está aberto para edição na estação {_lockedBy}.");
        }

        private static string NormalizePolicy(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return "ReadOnly";
            foreach (var v in new[] { "Blocked", "ReadOnly", "Full" }) if (string.Equals(p, v, StringComparison.OrdinalIgnoreCase)) return v;
            throw Bad("Política inválida: " + p);
        }

        private static RpcException Bad(string m) => new RpcException(RpcException.InvalidParams, m);

        private void Emit(string kind, string tag, string method, string detail, string level = "info") =>
            _event(new HostEventDto { Kind = kind, Tag = tag, Method = method, Detail = detail, Level = level });

        public void Dispose()
        {
            _idleTimer.Dispose();
            try { CloseProjectAsync().Wait(TimeSpan.FromSeconds(10)); } catch { } // o Host limita o tempo total do fechamento
        }
    }
}
