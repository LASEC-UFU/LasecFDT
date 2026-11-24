using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LasecFDT.Core.Project;
using LasecFDT.Core.Rpc;

namespace LasecFDT.Supervisor
{
    public sealed class SupervisorOptions
    {
        public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(60);
        /// <summary>
        /// Executáveis de servidores fora do processo usados por Comm DTMs (ex.: CodeWrights HART: CWFdtHARTPort.exe).
        /// Se o worker do Comm DTM morrer, os servidores que surgiram durante as operações dele são encerrados
        /// para liberar a porta (doc 08 §8: servidor órfão segura a COM).
        /// </summary>
        public List<string> CommServerImages { get; set; } = new List<string> { "CWFdtHARTPort" };
        public string LogDirectory { get; set; }
    }

    /// <summary>Uma instância de DTM hospedada num worker.</summary>
    public sealed class DtmInstance
    {
        public WorkerHandle Worker { get; internal set; }
        public DtmSummary Summary { get; internal set; }
        public string SystemTag => Summary?.SystemTag;
        public bool IsCommunicationRoot { get; internal set; }
        internal bool Releasing { get; set; }
        public bool Alive => Worker != null && !Worker.HasExited;
        /// <summary>PIDs de servidores de comunicação que surgiram durante operações desta instância.</summary>
        internal HashSet<int> OwnedCommServers { get; } = new HashSet<int>();
        public override string ToString() => $"{Summary?.Name} [{SystemTag}] pid={Worker?.ProcessId}";
    }

    /// <summary>Ligação de comunicação entre dois workers (pai = Comm DTM, filho = Device DTM).</summary>
    public sealed class ChannelLink
    {
        public DtmInstance Parent { get; internal set; }
        public DtmInstance Child { get; internal set; }
        public string ChannelPath { get; internal set; }
        public string Policy { get; internal set; }
        public bool Broken { get; internal set; }
    }

    public sealed class SupervisorEvent
    {
        public DtmInstance Instance { get; set; }
        public string Kind { get; set; }      // "worker" (evento do worker) | "died" | "linkBroken" | "janitor"
        public WorkerEvent WorkerEvent { get; set; }
        public string Detail { get; set; }
        public override string ToString() => $"{Kind} {Instance} {WorkerEvent?.Kind} {WorkerEvent?.Interface}.{WorkerEvent?.Method} {WorkerEvent?.Detail ?? Detail}";
    }

    /// <summary>
    /// Supervisor: um worker por instância de DTM (doc 06). Orquestra a comunicação aninhada entre processos:
    /// o canal do Comm DTM é passado ao worker do Device DTM como referência COM (OBJREF), e o guarda de
    /// comunicação fica no worker do filho.
    /// </summary>
    public sealed class Supervisor : IDisposable
    {
        private readonly string _workerExe;
        private readonly List<DtmInstance> _instances = new List<DtmInstance>();
        private readonly List<ChannelLink> _links = new List<ChannelLink>();
        private readonly object _gate = new object();

        public SupervisorOptions Options { get; }
        public event Action<SupervisorEvent> EventRaised;
        public IReadOnlyList<DtmInstance> Instances { get { lock (_gate) return _instances.ToList(); } }
        public IReadOnlyList<ChannelLink> Links { get { lock (_gate) return _links.ToList(); } }

        public Supervisor(string workerExe, SupervisorOptions options = null)
        {
            _workerExe = Path.GetFullPath(workerExe);
            if (!File.Exists(_workerExe)) throw new FileNotFoundException("Worker não encontrado", _workerExe);
            Options = options ?? new SupervisorOptions();
        }

        private void Raise(SupervisorEvent e) { try { EventRaised?.Invoke(e); } catch { } }

        // ================================================================ instâncias

        private async Task<DtmInstance> StartWorkerAsync(string label)
        {
            string log = Options.LogDirectory == null ? null : Path.Combine(Options.LogDirectory, $"worker-{DateTime.Now:HHmmss-fff}-{Sanitize(label)}.log");
            if (log != null) Directory.CreateDirectory(Options.LogDirectory);
            var w = await WorkerHandle.StartAsync(_workerExe, log).ConfigureAwait(false);
            var inst = new DtmInstance { Worker = w };
            w.EventReceived += (_, e) => Raise(new SupervisorEvent { Instance = inst, Kind = "worker", WorkerEvent = e });
            w.Exited += _ => OnWorkerExited(inst);
            lock (_gate) _instances.Add(inst);
            return inst;
        }

        /// <summary>Informação estática do DTM num worker descartável (o DTM é liberado e o worker encerrado em seguida).</summary>
        public async Task<DtmDescription> DescribeAsync(string dtm, int lcid = 0x0409)
        {
            var inst = await StartWorkerAsync("describe-" + dtm).ConfigureAwait(false);
            try { return await inst.Worker.CallAsync<DtmDescription>(WorkerMethods.Describe, new DescribeParams { Dtm = dtm, Lcid = lcid }, Options.CallTimeout).ConfigureAwait(false); }
            finally { await DiscardAsync(inst).ConfigureAwait(false); }
        }

        /// <summary>Nova instância (worker novo) até "configured".</summary>
        public async Task<DtmInstance> CreateAsync(CreateDtmParams p)
        {
            var inst = await StartWorkerAsync(p.Dtm).ConfigureAwait(false);
            try
            {
                inst.Summary = await TrackCommServers(inst, () => inst.Worker.CallAsync<DtmSummary>(WorkerMethods.Create, p, Options.CallTimeout)).ConfigureAwait(false);
                return inst;
            }
            catch { await DiscardAsync(inst).ConfigureAwait(false); throw; }
        }

        /// <summary>Instância existente (data set salvo) em worker novo, até "configured".</summary>
        public async Task<DtmInstance> LoadAsync(LoadParams p)
        {
            var inst = await StartWorkerAsync(p.Dtm).ConfigureAwait(false);
            try
            {
                inst.Summary = await inst.Worker.CallAsync<DtmSummary>(WorkerMethods.Load, p, Options.CallTimeout).ConfigureAwait(false);
                return inst;
            }
            catch { await DiscardAsync(inst).ConfigureAwait(false); throw; }
        }

        public Task<PersistedDataSet> SaveAsync(DtmInstance i) => i.Worker.CallAsync<PersistedDataSet>(WorkerMethods.Save, null, Options.CallTimeout);
        public Task<List<FunctionDto>> GetFunctionsAsync(DtmInstance i) => i.Worker.CallAsync<List<FunctionDto>>(WorkerMethods.GetFunctions, null, Options.CallTimeout);
        public Task<UiResult> OpenUiAsync(DtmInstance i, OpenUiParams p = null) => i.Worker.CallAsync<UiResult>(WorkerMethods.OpenUi, p ?? new OpenUiParams(), Options.CallTimeout);
        /// <summary>IDtm::InvokeFunctionRequest — funções sem controle ActiveX (o DTM pode abrir janela própria).</summary>
        public Task<InvokeResult> InvokeFunctionAsync(DtmInstance i, string functionId) =>
            i.Worker.CallAsync<InvokeResult>(WorkerMethods.InvokeFunction, new InvokeParams { FunctionId = functionId }, Options.CallTimeout);
        public Task<bool> WaitFunctionAsync(DtmInstance i, string invokeId, int timeoutMs = 30000) =>
            i.Worker.CallAsync<bool>(WorkerMethods.WaitFunction, new WaitFunctionParams { InvokeId = invokeId, TimeoutMs = timeoutMs }, TimeSpan.FromMilliseconds(timeoutMs) + Options.CallTimeout);
        public Task<List<ChannelDto>> GetChannelsAsync(DtmInstance i) => i.Worker.CallAsync<List<ChannelDto>>(WorkerMethods.GetChannels, null, Options.CallTimeout);
        public Task<List<FunctionDto>> GetChannelFunctionsAsync(DtmInstance i, string channelPath) =>
            i.Worker.CallAsync<List<FunctionDto>>(WorkerMethods.GetChannelFunctions, new ChannelRefParams { ChannelPath = channelPath }, Options.CallTimeout);
        public Task<UiResult> OpenChannelUiAsync(DtmInstance i, ChannelUiParams p) => i.Worker.CallAsync<UiResult>(WorkerMethods.OpenChannelUi, p, Options.CallTimeout);
        public Task<bool> CloseUiAsync(DtmInstance i) => i.Worker.CallAsync<bool>(WorkerMethods.CloseUi, null, Options.CallTimeout);
        public Task<int> ScreenshotAsync(DtmInstance i, string path) => i.Worker.CallAsync<int>(WorkerMethods.Screenshot, new ScreenshotParams { Path = path }, Options.CallTimeout);
        public Task<List<CommRecordDto>> GetCommLogAsync(DtmInstance i) => i.Worker.CallAsync<List<CommRecordDto>>(WorkerMethods.GetCommLog, null, Options.CallTimeout);
        public Task<bool> WaitOnlineAsync(DtmInstance i, int timeoutMs = 10000) =>
            i.Worker.CallAsync<bool>(WorkerMethods.WaitOnline, new WaitParams { TimeoutMs = timeoutMs }, TimeSpan.FromMilliseconds(timeoutMs) + Options.CallTimeout);

        /// <summary>Coloca o Comm DTM como raiz da cadeia de comunicação (IDtm::SetCommunication(NULL)), uma vez.</summary>
        public async Task EnsureCommunicationRootAsync(DtmInstance i)
        {
            if (i.IsCommunicationRoot) return;
            await TrackCommServers(i, () => i.Worker.CallAsync(WorkerMethods.SetCommunicationNull, null, Options.CallTimeout)).ConfigureAwait(false);
            i.IsCommunicationRoot = true;
        }

        /// <summary>Busca de instrumentos no canal (IFdtChannelSubTopology::ScanRequest). Pode levar minutos.</summary>
        public Task<ScanResult> ScanAsync(DtmInstance i, string channelPath, int timeoutMs = 180000) =>
            TrackCommServers(i, () => i.Worker.CallAsync<ScanResult>(WorkerMethods.Scan, new ScanParams { ChannelPath = channelPath, TimeoutMs = timeoutMs },
                TimeSpan.FromMilliseconds(timeoutMs) + Options.CallTimeout));

        public Task<string> GetParametersAsync(DtmInstance i, string path = null) =>
            i.Worker.CallAsync<string>(WorkerMethods.GetParameters, new ParameterPathParams { Path = path }, Options.CallTimeout);
        public Task<bool> SetParametersAsync(DtmInstance i, string xml, string path = null) =>
            i.Worker.CallAsync<bool>(WorkerMethods.SetParameters, new SetParametersParams { Path = path, Xml = xml }, Options.CallTimeout);
        public Task<OnlineParameterResult> UploadAsync(DtmInstance i, string path = null, int timeoutMs = 300000) =>
            i.Worker.CallAsync<OnlineParameterResult>(WorkerMethods.Upload, new OnlineParameterParams { Path = path, TimeoutMs = timeoutMs }, TimeSpan.FromMilliseconds(timeoutMs) + Options.CallTimeout);
        public Task<OnlineParameterResult> DownloadAsync(DtmInstance i, string path = null, int timeoutMs = 300000) =>
            i.Worker.CallAsync<OnlineParameterResult>(WorkerMethods.Download, new OnlineParameterParams { Path = path, TimeoutMs = timeoutMs }, TimeSpan.FromMilliseconds(timeoutMs) + Options.CallTimeout);
        public Task<string> OnlineCompareAsync(DtmInstance i) => i.Worker.CallAsync<string>(WorkerMethods.OnlineCompare, null, Options.CallTimeout);
        public Task<string> DeviceStatusAsync(DtmInstance i) => i.Worker.CallAsync<string>(WorkerMethods.DeviceStatus, null, Options.CallTimeout);

        // ================================================================ comunicação aninhada entre processos

        /// <summary>
        /// Liga <paramref name="child"/> ao canal de comunicação de <paramref name="parent"/> (§6.2):
        /// pai raiz → SetCommunication(NULL); topologia: ValidateAddChild/OnAddChild no worker do pai;
        /// OBJREF do canal → worker do filho → SetCommunication(guarda(proxy)).
        /// </summary>
        public async Task<ChannelLink> ConnectAsync(DtmInstance parent, DtmInstance child, string policy = "Blocked", string channelPath = null)
        {
            await EnsureCommunicationRootAsync(parent).ConfigureAwait(false);
            var channels = await parent.Worker.CallAsync<List<ChannelDto>>(WorkerMethods.GetChannels, null, Options.CallTimeout).ConfigureAwait(false);
            var ch = channels.FirstOrDefault(c => c.IsCommunicationChannel && (channelPath == null || string.Equals(c.Path, channelPath, StringComparison.OrdinalIgnoreCase)))
                     ?? throw new RpcException(RpcException.InvalidParams, $"{parent} não expõe o canal de comunicação {channelPath ?? "(qualquer)"}.");

            await parent.Worker.CallAsync(WorkerMethods.AddChild, new ChildParams { ChannelPath = ch.Path, ChildTag = child.SystemTag }, Options.CallTimeout).ConfigureAwait(false);
            try
            {
                var m = await parent.Worker.CallAsync<MarshaledChannel>(WorkerMethods.MarshalChannel, new ChannelRefParams { ChannelPath = ch.Path }, Options.CallTimeout).ConfigureAwait(false);
                var r = await TrackCommServers(parent, () => child.Worker.CallAsync<ConnectResult>(WorkerMethods.ConnectChannel,
                    new ConnectChannelParams { ParentTag = parent.SystemTag, ChannelPath = ch.Path, ObjRefBase64 = m.ObjRefBase64, Policy = policy }, Options.CallTimeout)).ConfigureAwait(false);
                if (!r.Accepted) throw new RpcException(RpcException.DtmFailure, "O DTM filho recusou SetCommunication: " + r.Error);
            }
            catch
            {
                try { await parent.Worker.CallAsync(WorkerMethods.RemoveChild, new ChildParams { ChannelPath = ch.Path, ChildTag = child.SystemTag }, Options.CallTimeout).ConfigureAwait(false); } catch { }
                throw;
            }
            var link = new ChannelLink { Parent = parent, Child = child, ChannelPath = ch.Path, Policy = policy };
            lock (_gate) _links.Add(link);
            return link;
        }

        /// <summary>Liga o filho a um canal HART simulado pelo próprio Frame, dentro do worker do filho (sem hardware).</summary>
        public async Task<ConnectResult> ConnectSimulatorAsync(DtmInstance child, string policy = "Full")
        {
            var r = await child.Worker.CallAsync<ConnectResult>(WorkerMethods.ConnectSimulator, new SimulatorParams { Policy = policy }, Options.CallTimeout).ConfigureAwait(false);
            if (!r.Accepted) throw new RpcException(RpcException.DtmFailure, "O DTM recusou SetCommunication: " + r.Error);
            return r;
        }

        /// <summary>Desfaz a ligação na ordem da spec: filho libera a comunicação → OnRemoveChild no pai.</summary>
        public async Task<bool> DisconnectAsync(ChannelLink link)
        {
            bool ok = true;
            if (link.Child.Alive)
                try { ok &= await link.Child.Worker.CallAsync<bool>(WorkerMethods.ReleaseCommunication, null, Options.CallTimeout).ConfigureAwait(false); }
                catch (RpcException) { ok = false; }
            if (link.Parent.Alive)
                try { ok &= await link.Parent.Worker.CallAsync<bool>(WorkerMethods.RemoveChild, new ChildParams { ChannelPath = link.ChannelPath, ChildTag = link.Child.SystemTag }, Options.CallTimeout).ConfigureAwait(false); }
                catch (RpcException) { ok = false; }
            lock (_gate) _links.Remove(link);
            return ok && !link.Broken;
        }

        /// <summary>Libera a instância pelo protocolo (desligando antes filhos e pai) e encerra o worker.</summary>
        public async Task<bool> ReleaseAsync(DtmInstance inst)
        {
            bool ok = true;
            foreach (var l in Links.Where(l => l.Child == inst || l.Parent == inst).OrderBy(l => l.Parent == inst ? 0 : 1).ToList())
                ok &= await DisconnectAsync(l).ConfigureAwait(false);
            if (inst.Worker != null)
            {
                inst.Releasing = true;
                ok &= await inst.Worker.ShutdownAsync().ConfigureAwait(false);
                inst.Worker.Dispose();
            }
            lock (_gate) _instances.Remove(inst);
            return ok;
        }

        private async Task DiscardAsync(DtmInstance inst)
        {
            inst.Releasing = true;
            try { await inst.Worker.ShutdownAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); } catch { }
            inst.Worker.Dispose();
            KillOwnedCommServers(inst, "instância descartada após falha");
            lock (_gate) _instances.Remove(inst);
        }

        // ================================================================ quedas

        private void OnWorkerExited(DtmInstance inst)
        {
            if (inst.Releasing && !inst.Worker.Crashed) return;
            Raise(new SupervisorEvent { Instance = inst, Kind = "died", Detail = $"worker {inst.Worker.ProcessId} terminou inesperadamente" });
            KillOwnedCommServers(inst, "worker do Comm DTM caiu");
            List<ChannelLink> affected;
            lock (_gate) affected = _links.Where(l => l.Parent == inst || l.Child == inst).ToList();
            foreach (var l in affected)
            {
                l.Broken = true;
                Raise(new SupervisorEvent { Instance = inst, Kind = "linkBroken", Detail = $"{l.Parent?.SystemTag} [{l.ChannelPath}] → {l.Child?.SystemTag}" });
                // O lado vivo é avisado: o filho libera a comunicação (o proxy do canal morto falha de forma controlada);
                // o pai remove o filho da topologia.
                var other = l.Parent == inst ? l.Child : l.Parent;
                if (other.Alive)
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (other == l.Child) await other.Worker.CallAsync(WorkerMethods.ReleaseCommunication, null, Options.CallTimeout).ConfigureAwait(false);
                            else await other.Worker.CallAsync(WorkerMethods.RemoveChild, new ChildParams { ChannelPath = l.ChannelPath, ChildTag = l.Child.SystemTag }, Options.CallTimeout).ConfigureAwait(false);
                        }
                        catch { }
                        lock (_gate) _links.Remove(l);
                    });
                else lock (_gate) _links.Remove(l);
            }
        }

        /// <summary>Executa uma operação e atribui à instância os servidores de comunicação que surgirem durante ela.</summary>
        private async Task<T> TrackCommServers<T>(DtmInstance inst, Func<Task<T>> op)
        {
            var before = new HashSet<int>(CommServerPids());
            try { return await op().ConfigureAwait(false); }
            finally
            {
                foreach (var pid in CommServerPids()) if (!before.Contains(pid)) lock (_gate) inst.OwnedCommServers.Add(pid);
            }
        }

        private IEnumerable<int> CommServerPids() =>
            Options.CommServerImages.SelectMany(n => { try { return Process.GetProcessesByName(n).Select(p => p.Id).ToList(); } catch { return new List<int>(); } });

        private void KillOwnedCommServers(DtmInstance inst, string reason)
        {
            List<int> pids;
            lock (_gate) pids = inst.OwnedCommServers.ToList();
            foreach (var pid in pids)
            {
                try
                {
                    var p = Process.GetProcessById(pid);
                    if (!Options.CommServerImages.Any(n => string.Equals(p.ProcessName, n, StringComparison.OrdinalIgnoreCase))) continue; // PID reutilizado
                    // Espera breve: o servidor normalmente encerra sozinho quando o cliente some.
                    if (!p.WaitForExit(3000)) { p.Kill(); Raise(new SupervisorEvent { Instance = inst, Kind = "janitor", Detail = $"servidor de comunicação órfão {p.ProcessName} ({pid}) encerrado: {reason}" }); }
                }
                catch { }
            }
            lock (_gate) inst.OwnedCommServers.Clear();
        }

        private static string Sanitize(string s) => string.Concat((s ?? "dtm").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        public void Dispose()
        {
            foreach (var i in Instances)
            {
                try { ReleaseAsync(i).Wait(TimeSpan.FromSeconds(30)); } catch { }
                try { i.Worker?.Dispose(); } catch { }
            }
        }
    }
}
