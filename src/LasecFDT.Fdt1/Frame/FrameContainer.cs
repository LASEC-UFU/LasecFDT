using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml;
using Jigfdt.Fdt100;
using LasecFDT.Fdt1.Comm;

namespace LasecFDT.Fdt1.Frame
{
    /// <summary>Ligação pai(canal) → filho na topologia do Frame.</summary>
    public sealed class TopologyLink
    {
        public string ParentTag { get; set; }
        public string ChannelPath { get; set; }
        public string ChildTag { get; set; }
        public DtmSession Parent { get; set; }
        public DtmSession.ChannelRef Channel { get; set; }
        public HartSimulatedChannel Simulator { get; set; }
        public CommunicationGuard Guard { get; set; }
        public bool CommunicationAccepted { get; set; }
    }

    /// <summary>Evento recebido de um DTM (para log, testes e futura API de integração).</summary>
    public sealed class FrameEvent
    {
        public DateTime Time { get; set; } = DateTime.Now;
        public string Interface { get; set; }
        public string Method { get; set; }
        public string SystemTag { get; set; }
        public string InvokeId { get; set; }
        public string Detail { get; set; }
        public override string ToString() =>
            $"{Time:HH:mm:ss.fff} {Interface}.{Method} tag={SystemTag} {(InvokeId != null ? "invoke=" + InvokeId + " " : "")}{Detail}";
    }

    /// <summary>
    /// Ponto de extensão para a interface gráfica (WinForms de teste hoje, Flutter amanhã).
    /// Sem UI (modo headless) o Frame responde com os valores padrão definidos na especificação.
    /// </summary>
    public interface IFrameUi
    {
        /// <summary>IFdtActiveX::OpenActiveXControlRequest — o DTM pede ao Frame que abra um controle.</summary>
        bool OpenActiveXRequest(string systemTag, string functionCall, bool modal);
        /// <summary>IFdtActiveX::CloseActiveXControlRequest.</summary>
        bool CloseActiveXRequest(string invokeId);
        /// <summary>IFdtDialog::UserDialog — retorna o XML de resposta, ou null para usar a resposta padrão.</summary>
        string UserDialog(string systemTag, string userMessage);
    }

    /// <summary>
    /// Objeto Frame Application exposto aos DTMs. Um único objeto COM implementa o "main interface"
    /// IFdtContainer e as interfaces de tarefa, que o DTM obtém por QueryInterface (FDT 1.2.1 §2.4.3, §4.2.1).
    ///
    /// Interfaces obrigatórias do Frame segundo a tabela de §4.10.1: IFdtContainer, IDtmEvents, IDtmEvents2,
    /// IDtmAuditTrailEvents, IFdtDialog, IFdtTopology, IFdtBtmTopology, IDtmSingleDeviceDataAccessEvents,
    /// IDtmSingleInstanceDataAccessEvents. Opcionais implementadas: IFdtActiveX, IFdtActiveX2, IFdtBulkData.
    /// Não implementadas nesta fase: IDtmScanEvents, IDtmRedundancyEvents (opcionais).
    ///
    /// Regra de threading: os DTMs são objetos STA. StandardOleMarshalObject garante que chamadas vindas de threads
    /// de trabalho dos DTMs sejam encaminhadas pelo COM à thread STA do host (sem ele, o objeto .NET seria ágil).
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class FrameContainer : StandardOleMarshalObject,
        IFdtContainer, IDtmEvents, IDtmEvents2, IDtmAuditTrailEvents, IFdtDialog,
        IFdtTopology, IFdtBtmTopology, IDtmSingleDeviceDataAccessEvents, IDtmSingleInstanceDataAccessEvents,
        IFdtActiveX, IFdtActiveX2, IFdtBulkData
    {
        private readonly ConcurrentDictionary<string, DtmSession> _sessions = new ConcurrentDictionary<string, DtmSession>(StringComparer.OrdinalIgnoreCase);

        public FrameContainer(string schemaPath = null, string dataRoot = null)
        {
            // LASECFDT_SCHEMA_PATH: substitui o diretório de schemas (diagnóstico/experimentos).
            SchemaPath = schemaPath ?? FdtSchemaLocator.Resolve();
            if (!SchemaPath.EndsWith("\\")) SchemaPath += "\\";
            DataRoot = dataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LasecFDT", "BulkData");
        }

        public string SchemaPath { get; }
        public string DataRoot { get; }
        public string ProjectName { get; set; } = "LasecFDT";
        public IFrameUi Ui { get; set; }

        /// <summary>Todos os eventos recebidos dos DTMs.</summary>
        public event Action<FrameEvent> EventRaised;

        internal void Register(DtmSession s) => _sessions[s.SystemTag] = s;
        internal void Unregister(DtmSession s) => _sessions.TryRemove(s.SystemTag, out _);
        public DtmSession FindSession(string systemTag) => systemTag != null && _sessions.TryGetValue(systemTag, out var s) ? s : null;

        private void Raise(string itf, string method, string tag = null, string invoke = null, string detail = null)
        {
            var e = new FrameEvent { Interface = itf, Method = method, SystemTag = tag, InvokeId = invoke, Detail = detail };
            FindSession(tag)?.OnFrameEvent(e);
            if (tag == null && invoke != null)
                foreach (var s in _sessions.Values) if (s.OwnsInvokeId(invoke)) s.OnFrameEvent(e);
            EventRaised?.Invoke(e);
        }

        private static string Short(string xml) => xml == null ? null : (xml.Length > 300 ? xml.Substring(0, 300) + "…" : xml);

        // ================================================================ IFdtContainer (§4.9.8)
        public bool SaveRequest(string systemTag)
        {
            Raise("IFdtContainer", "SaveRequest", systemTag);
            var s = FindSession(systemTag);
            if (s == null) return false;
            s.SaveRequested = true; // o projeto decide quando persistir (§6.14.1.2: só informa)
            return true;
        }
        public bool LockDataSet(string systemTag)
        {
            Raise("IFdtContainer", "LockDataSet", systemTag);
            var s = FindSession(systemTag);
            if (s == null) return false;
            s.DataSetLocked = true; // ambiente mono-usuário: o bloqueio é sempre concedido
            return true;
        }
        public bool UnlockDataSet(string systemTag)
        {
            Raise("IFdtContainer", "UnlockDataSet", systemTag);
            var s = FindSession(systemTag);
            if (s == null) return false;
            s.DataSetLocked = false;
            return true;
        }
        public string GetXmlSchemaPath()
        {
            Raise("IFdtContainer", "GetXmlSchemaPath", detail: SchemaPath);
            return SchemaPath; // com barra final, como exige §4.9.8.1
        }

        // ================================================================ IDtmEvents (§4.9.1)
        public void OnParameterChanged(string systemTag, string parameter) => Raise("IDtmEvents", "OnParameterChanged", systemTag, detail: Short(parameter));
        public void OnErrorMessage(string systemTag, string errorMessage) => Raise("IDtmEvents", "OnErrorMessage", systemTag, detail: errorMessage);
        public void OnProgress(string systemTag, string title, short percent, bool show) => Raise("IDtmEvents", "OnProgress", systemTag, detail: $"{title} {percent}% show={show}");
        public void OnUploadFinished(string invokeId, bool success) => Raise("IDtmEvents", "OnUploadFinished", invoke: invokeId, detail: "success=" + success);
        public void OnDownloadFinished(string invokeId, bool success) => Raise("IDtmEvents", "OnDownloadFinished", invoke: invokeId, detail: "success=" + success);
        public void OnApplicationClosed(string invokeId) => Raise("IDtmEvents", "OnApplicationClosed", invoke: invokeId);
        public void OnFunctionChanged(string systemTag) => Raise("IDtmEvents", "OnFunctionChanged", systemTag);
        public void OnChannelFunctionChanged(string systemTag, string channelPath) => Raise("IDtmEvents", "OnChannelFunctionChanged", systemTag, detail: channelPath);
        public void OnPrint(string systemTag, string functionCall) => Raise("IDtmEvents", "OnPrint", systemTag, detail: Short(functionCall));
        public void OnNavigation(string systemTag) => Raise("IDtmEvents", "OnNavigation", systemTag);
        public void OnOnlineStateChanged(string systemTag, bool onlineState) => Raise("IDtmEvents", "OnOnlineStateChanged", systemTag, detail: "online=" + onlineState);
        public void OnPreparedToRelease(string systemTag) => Raise("IDtmEvents", "OnPreparedToRelease", systemTag);
        public void OnPreparedToReleaseCommunication(string systemTag) => Raise("IDtmEvents", "OnPreparedToReleaseCommunication", systemTag);
        public void OnInvokedFunctionFinished(string invokeId, bool success) => Raise("IDtmEvents", "OnInvokedFunctionFinished", invoke: invokeId, detail: "success=" + success);
        public void OnScanResponse(string invokeId, string response)
        {
            lock (ScanResponses) ScanResponses[invokeId ?? ""] = response;
            Raise("IDtmEvents", "OnScanResponse", invoke: invokeId, detail: Short(response));
        }

        /// <summary>Documentos completos de IDtmEvents::OnScanResponse (DTMTopologyScanSchema), por invokeId.</summary>
        public Dictionary<string, string> ScanResponses { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // ================================================================ IDtmEvents2 (§4.9.2)
        public void OnStateChanged(string systemTag, string xmldoc) => Raise("IDtmEvents2", "OnStateChanged", systemTag, detail: Short(xmldoc));

        // ================================================================ IDtmAuditTrailEvents (§4.9.4)
        public bool OnStartTransaction(string systemTag) { Raise("IDtmAuditTrailEvents", "OnStartTransaction", systemTag); return true; }
        public void OnAuditTrailEvent(string systemTag, string logEntry) => Raise("IDtmAuditTrailEvents", "OnAuditTrailEvent", systemTag, detail: Short(logEntry));
        public bool OnEndTransaction(string systemTag) { Raise("IDtmAuditTrailEvents", "OnEndTransaction", systemTag); return true; }

        // ================================================================ IFdtDialog (§4.9.9)
        public string UserDialog(string systemTag, string userMessage)
        {
            Raise("IFdtDialog", "UserDialog", systemTag, detail: userMessage);
            string answer = Ui?.UserDialog(systemTag, userMessage);
            return answer ?? DefaultDialogAnswer(userMessage, "systemResponse");
        }

        /// <summary>Resposta padrão: devolve a própria mensagem com resultMessage = messageDefault (FDTUserMessageSchema).</summary>
        public static string DefaultDialogAnswer(string userMessage, string resultStatus, string resultButton = null)
        {
            try
            {
                var doc = FdtXml.Load(userMessage);
                var msg = doc.GetElementsByTagName("*").OfType<XmlElement>().First(e => e.LocalName == "FDTUserMessage");
                string def = msg.GetAttribute("messageDefault");
                msg.SetAttribute("resultMessage", resultButton ?? (string.IsNullOrEmpty(def) ? "nobutton" : def));
                msg.SetAttribute("resultStatus", resultStatus);
                return doc.OuterXml;
            }
            catch
            {
                return userMessage;
            }
        }

        // ================================================================ Topologia e comunicação aninhada (§6.2)

        private readonly List<TopologyLink> _links = new List<TopologyLink>();
        public IReadOnlyList<TopologyLink> Links => _links;
        /// <summary>Registra uma ligação já estabelecida (ex.: topologia lida de um projeto, antes de instanciar os DTMs).</summary>
        public void RegisterLink(TopologyLink link) => _links.Add(link);
        public bool HasParentLink(string childTag) => _links.Any(l => Eq(l.ChildTag, childTag));
        private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Liga um DTM filho ao canal de comunicação de um DTM pai (§6.2.1.1 + §6.2.2):
        /// topologia interna → ValidateAddChild/OnAddChild no canal → IDtm::SetCommunication(guarda) no filho.
        /// O pai deve estar em "communication set" (raiz: SetCommunication(NULL), §4.3.1.11).
        /// O filho recebe um <see cref="CommunicationGuard"/> com a política informada — nunca o canal direto.
        /// </summary>
        public TopologyLink ConnectToChannel(DtmSession parent, DtmSession.ChannelRef channel, DtmSession child, CommPolicy policy)
        {
            if (!(channel.Channel is IFdtCommunication comm))
                throw new InvalidOperationException($"O canal {channel.Path} não fornece IFdtCommunication (não é canal de comunicação).");
            var link = new TopologyLink { ParentTag = parent.SystemTag, ChannelPath = channel.Path, ChildTag = child.SystemTag, Parent = parent, Channel = channel };
            _links.Add(link); // o DTM pai pode consultar GetChildNodes durante OnAddChild
            if (!parent.AddChildToChannel(channel, child.SystemTag)) { _links.Remove(link); return null; }
            link.Guard = new CommunicationGuard(comm, policy, child.SystemTag);
            link.CommunicationAccepted = child.SetCommunication(link.Guard, $"canal {channel.Path}, política {policy}");
            return link;
        }

        // ---- Variantes entre processos (worker do pai ≠ worker do filho; o supervisor orquestra) ----

        /// <summary>Lado do PAI: registra o filho remoto na topologia e chama ValidateAddChild/OnAddChild no canal.</summary>
        public TopologyLink AddRemoteChild(DtmSession parent, DtmSession.ChannelRef channel, string childTag)
        {
            var link = new TopologyLink { ParentTag = parent.SystemTag, ChannelPath = channel.Path, ChildTag = childTag, Parent = parent, Channel = channel };
            _links.Add(link);
            if (!parent.AddChildToChannel(channel, childTag)) { _links.Remove(link); return null; }
            return link;
        }

        /// <summary>Lado do PAI: OnRemoveChild e remoção da topologia (após o filho liberar a comunicação).</summary>
        public bool RemoveRemoteChild(string childTag)
        {
            var link = _links.FirstOrDefault(l => Eq(l.ChildTag, childTag) && l.Parent != null);
            if (link == null) return false;
            bool ok = link.Parent.ComObject == null || link.Parent.RemoveChildFromChannel(link.Channel, childTag);
            _links.Remove(link);
            return ok;
        }

        /// <summary>
        /// Lado do FILHO: recebe o proxy COM do canal (vindo de outro processo) e o entrega ao DTM através do guarda.
        /// </summary>
        public TopologyLink ConnectToRemoteChannel(DtmSession child, IFdtCommunication remoteChannel, string parentTag, string channelPath, CommPolicy policy)
        {
            var link = new TopologyLink { ParentTag = parentTag, ChannelPath = channelPath, ChildTag = child.SystemTag };
            _links.Add(link);
            link.Guard = new CommunicationGuard(remoteChannel, policy, child.SystemTag);
            link.CommunicationAccepted = child.SetCommunication(link.Guard, $"canal remoto {channelPath}, política {policy}");
            if (!link.CommunicationAccepted) _links.Remove(link);
            return link;
        }

        /// <summary>Liga o DTM filho a um canal HART simulado pelo Frame (sem hardware).</summary>
        public TopologyLink ConnectToSimulator(DtmSession child, HartSimulatedChannel simulator, CommPolicy policy = CommPolicy.Full)
        {
            var link = new TopologyLink { ParentTag = simulator.OwnerTag, ChannelPath = simulator.ChannelPath, ChildTag = child.SystemTag, Simulator = simulator };
            _links.Add(link);
            link.Guard = new CommunicationGuard(simulator, policy, child.SystemTag);
            link.CommunicationAccepted = child.SetCommunication(link.Guard, $"canal simulado {simulator.ChannelPath}, política {policy}");
            return link;
        }

        /// <summary>Desfaz a ligação na ordem inversa: ReleaseCommunication no filho → OnRemoveChild no canal pai.</summary>
        public bool Disconnect(string childTag, TimeSpan timeout)
        {
            var link = _links.FirstOrDefault(l => Eq(l.ChildTag, childTag));
            if (link == null) return false;
            bool ok = true;
            var child = FindSession(childTag);
            if (child != null && child.State == DtmState.CommunicationSet) ok &= child.ReleaseCommunication(timeout);
            if (link.Parent != null && link.Channel != null && link.Parent.ComObject != null) ok &= link.Parent.RemoveChildFromChannel(link.Channel, childTag);
            _links.Remove(link);
            return ok;
        }

        /// <summary>Desliga todos os filhos de um DTM pai (antes de liberá-lo).</summary>
        public void DisconnectChildrenOf(string parentTag, TimeSpan timeout)
        {
            foreach (var l in _links.Where(l => Eq(l.ParentTag, parentTag)).ToList()) Disconnect(l.ChildTag, timeout);
        }

        private static string TagList(string root, IEnumerable<string> tags) =>
            $"<?xml version=\"1.0\"?><FDT xmlns=\"x-schema:DTMSystemTagListSchema.xml\"><{root}>" +
            (tags.Any() ? "<DtmSystemTagList>" + string.Concat(tags.Select(t => $"<DtmSystemTag systemTag=\"{System.Security.SecurityElement.Escape(t)}\"/>")) + "</DtmSystemTagList>" : "") +
            $"</{root}></FDT>";

        // ================================================================ IFdtTopology (§4.9.10)
        // Documentos conforme DTMSystemTagListSchema, a partir das ligações criadas por ConnectToChannel/ConnectToSimulator.
        public string GetParentNodes(string systemTag)
        {
            var parents = _links.Where(l => Eq(l.ChildTag, systemTag)).Select(l => l.ParentTag).Distinct().ToList();
            Raise("IFdtTopology", "GetParentNodes", systemTag, detail: string.Join(",", parents));
            return TagList("DtmParentList", parents);
        }
        public string GetChildNodes(string systemTag, string channelPath)
        {
            var kids = _links.Where(l => Eq(l.ParentTag, systemTag) && (string.IsNullOrEmpty(channelPath) || Eq(l.ChannelPath, channelPath)))
                             .Select(l => l.ChildTag).Distinct().ToList();
            Raise("IFdtTopology", "GetChildNodes", systemTag, detail: channelPath + " → " + string.Join(",", kids));
            return TagList("DtmChildList", kids);
        }
        public string CreateChild(string deviceType, string channelPath) { Raise("IFdtTopology", "CreateChild", detail: channelPath); return null; }
        public bool DeleteChild(string systemTag, string channelPath) { Raise("IFdtTopology", "DeleteChild", systemTag); return false; }
        public IDtm GetDtmForSystemTag(string systemTag)
        {
            Raise("IFdtTopology", "GetDtmForSystemTag", systemTag);
            return FindSession(systemTag)?.Dtm;
        }
        public string GetDtmInfoList() { Raise("IFdtTopology", "GetDtmInfoList"); return null; }
        public bool MoveChild(string systemTag, string destinationchannelPath) { Raise("IFdtTopology", "MoveChild", systemTag); return false; }
        public bool ReleaseDtmForSystemTag(string systemTag) { Raise("IFdtTopology", "ReleaseDtmForSystemTag", systemTag); return true; }

        // ================================================================ IFdtBtmTopology (§4.9.14) — sem BTMs nesta fase
        string IFdtBtmTopology.GetParentNodes(string systemTag) => GetParentNodes(systemTag);
        string IFdtBtmTopology.GetChildNodes(string systemTag, string channelPath) => GetChildNodes(systemTag, channelPath);
        string IFdtBtmTopology.CreateChild(string deviceType, string channelPath) => null;
        bool IFdtBtmTopology.DeleteChild(string systemTag, string channelPath) => false;
        public IBtm GetBtmForSystemTag(string systemTag) { Raise("IFdtBtmTopology", "GetBtmForSystemTag", systemTag); return null; }
        public string GetBtmInfoList() { Raise("IFdtBtmTopology", "GetBtmInfoList"); return null; }
        bool IFdtBtmTopology.MoveChild(string systemTag, string destinationchannelPath) => false;
        public bool ReleaseBtmForSystemTag(string systemTag) => true;

        // ================================================================ IDtmSingleDeviceDataAccessEvents (§4.9.12)
        public void OnItemListResponse(string invokeId, string response) => Raise("IDtmSingleDeviceDataAccessEvents", "OnItemListResponse", invoke: invokeId, detail: Short(response));
        public void OnDeviceItemListChanged(string systemTag) => Raise("IDtmSingleDeviceDataAccessEvents", "OnDeviceItemListChanged", systemTag);
        public void OnReadResponse(string invokeId, string response) => Raise("IDtmSingleDeviceDataAccessEvents", "OnReadResponse", invoke: invokeId, detail: Short(response));
        public void OnWriteResponse(string invokeId, string response) => Raise("IDtmSingleDeviceDataAccessEvents", "OnWriteResponse", invoke: invokeId, detail: Short(response));

        // ================================================================ IDtmSingleInstanceDataAccessEvents (§4.9.13)
        public void OnInstanceItemListChanged(string systemTag) => Raise("IDtmSingleInstanceDataAccessEvents", "OnInstanceItemListChanged", systemTag);

        // ================================================================ IFdtActiveX / IFdtActiveX2 (§4.9.5, §4.9.6)
        public bool OpenActiveXControlRequest(string systemTag, string functionCall)
        {
            Raise("IFdtActiveX", "OpenActiveXControlRequest", systemTag, detail: Short(functionCall));
            return Ui?.OpenActiveXRequest(systemTag, functionCall, false) ?? false;
        }
        public bool CloseActiveXControlRequest(string invokeId)
        {
            Raise("IFdtActiveX", "CloseActiveXControlRequest", invoke: invokeId);
            return Ui?.CloseActiveXRequest(invokeId) ?? false;
        }
        public bool OpenDialogActiveXControlRequest(string systemTag, string functionCall)
        {
            Raise("IFdtActiveX2", "OpenDialogActiveXControlRequest", systemTag, detail: Short(functionCall));
            return Ui?.OpenActiveXRequest(systemTag, functionCall, true) ?? false;
        }
        public bool OpenChannelActiveXControlRequest(string channelPath, string functionCall) { Raise("IFdtActiveX2", "OpenChannelActiveXControlRequest", detail: channelPath); return false; }
        public bool CloseChannelActiveXControlRequest(string invokeId) { Raise("IFdtActiveX2", "CloseChannelActiveXControlRequest", invoke: invokeId); return false; }
        public bool OpenDialogChannelActiveXControlRequest(string channelPath, string functionCall) { Raise("IFdtActiveX2", "OpenDialogChannelActiveXControlRequest", detail: channelPath); return false; }

        // ================================================================ IFdtBulkData (§4.9.7)
        public string GetProjectRelatedPath(string systemTag)
        {
            var s = FindSession(systemTag);
            string p = Path.Combine(DataRoot, Sanitize(ProjectName), s?.ClsidString ?? "unknown") + "\\";
            Directory.CreateDirectory(p);
            Raise("IFdtBulkData", "GetProjectRelatedPath", systemTag, detail: p);
            return p;
        }
        public string GetInstanceRelatedPath(string systemTag)
        {
            string p = Path.Combine(DataRoot, Sanitize(ProjectName), "instances", Sanitize(systemTag)) + "\\";
            Directory.CreateDirectory(p);
            Raise("IFdtBulkData", "GetInstanceRelatedPath", systemTag, detail: p);
            return p;
        }

        private static string Sanitize(string s) => string.Concat((s ?? "_").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    }
}
