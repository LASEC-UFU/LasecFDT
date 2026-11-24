using System;
using System.Collections.Generic;

namespace LasecFDT.Core.Rpc
{
    /// <summary>
    /// API do LasecFDT.Host (backend de UMA estação) para a UI. Transporte: JsonLineChannel sobre stdin/stdout do
    /// processo Host, que é filho da UI — canal anônimo, sem nome, impossível de abrir a partir de outra sessão.
    /// Notificações do Host para a UI: método <see cref="Event"/> (HostEventDto) e <see cref="NodeChanged"/> (NodeDto).
    /// </summary>
    public static class HostMethods
    {
        public const string Hello = "host.hello";                 // → HostInfo
        public const string Settings = "station.settings";        // StationSettings? (null = só ler) → StationSettings
        public const string Leases = "plant.leases";              // → LeaseInfo[] (todas as estações)
        public const string Stations = "plant.stations";          // → LeaseInfo[] (estações ativas)

        public const string CatalogList = "catalog.list";         // CatalogParams → CatalogDto
        public const string CatalogDescribe = "catalog.describe"; // DtmRef → DtmDescription (cache compartilhado da planta)

        public const string ProjectList = "project.list";         // → ProjectFileDto[]
        public const string ProjectNew = "project.new";           // ProjectRef → ProjectDto
        public const string ProjectOpen = "project.open";         // ProjectRef → ProjectDto
        public const string ProjectSave = "project.save";         // ProjectRef? (nome = salvar como) → ProjectDto
        public const string ProjectClose = "project.close";       // → bool
        public const string ProjectCurrent = "project.current";   // → ProjectDto (null se nenhum)

        public const string NodeAdd = "node.add";                 // NodeAddParams → NodeDto
        public const string NodeRemove = "node.remove";           // NodeRef → bool
        public const string NodeRename = "node.rename";           // NodeRenameParams → NodeDto
        public const string NodeFunctions = "node.functions";     // NodeRef → NodeFunctionsDto
        public const string NodeOpenUi = "node.openUi";           // NodeUiParams → UiResult (janela encaixada)
        public const string NodeCloseUi = "node.closeUi";         // NodeRef → bool
        public const string NodeConnect = "node.connect";         // NodeConnectParams → NodeDto
        public const string NodeDisconnect = "node.disconnect";   // NodeRef → NodeDto
        public const string NodeCommLog = "node.commLog";         // NodeRef → CommRecordDto[]
        public const string NodeUnload = "node.unload";           // NodeRef → NodeDto (salva o data set e encerra o worker)
        public const string NodeParameters = "node.parameters";   // NodeRef → ParameterSetDto (IDtmParameter::GetParameters)
        public const string NodeUpload = "node.upload";           // NodeRef → CompareResultDto (lê do instrumento e mantém; diferenças em relação ao projeto)
        public const string NodeCompare = "node.compare";         // NodeRef → CompareResultDto (lê, compara e restaura o projeto)
        public const string NodeRevert = "node.revert";           // NodeRef → NodeDto (volta ao data set de antes da última leitura)
        public const string NodeDownload = "node.download";       // NodeDownloadParams → OnlineParameterResult (grava no instrumento)
        public const string NodeScan = "node.scan";               // NodeScanParams → ScanResultDto (busca de instrumentos no canal do nó)

        public const string Event = "event";                      // notificação → HostEventDto
        public const string NodeChanged = "nodeChanged";          // notificação → NodeDto
    }

    public sealed class HostInfo
    {
        public string Version { get; set; }
        public StationInfo Station { get; set; }
        public string PlantDirectory { get; set; }
        /// <summary>true = pasta da planta compartilhada (servidor); false = modo isolado (sem permissão na pasta comum).</summary>
        public bool PlantShared { get; set; }
        public string SessionDirectory { get; set; }
        public HostLimits Limits { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public sealed class StationInfo
    {
        /// <summary>Identificador da estação: nome do thin client (WTSClientName) ou da máquina na sessão de console.</summary>
        public string Id { get; set; }
        public string ClientName { get; set; }
        public int SessionId { get; set; }
        public string User { get; set; }
        public string Machine { get; set; }
        /// <summary>Sessão remota (RDP/thin client): a UI reduz efeitos visuais.</summary>
        public bool IsRemote { get; set; }
    }

    public sealed class HostLimits
    {
        /// <summary>Máximo de workers (instâncias de DTM carregadas) desta estação.</summary>
        public int MaxWorkers { get; set; } = 16;
        /// <summary>Minutos sem uso até descarregar uma instância (data set salvo em memória antes). 0 = nunca.</summary>
        public int IdleUnloadMinutes { get; set; } = 10;
    }

    public sealed class StationSettings
    {
        public string Theme { get; set; } = "dark";          // "dark" | "light"
        /// <summary>null = automático (ligado em sessão remota).</summary>
        public bool? ReducedEffects { get; set; }
        public string LastProject { get; set; }
        public string UserLevel { get; set; } = "maintenance";
        public string Language { get; set; } = "pt-BR";
    }

    public sealed class LeaseInfo
    {
        public string Key { get; set; }
        public string Station { get; set; }
        public string User { get; set; }
        public int SessionId { get; set; }
        public int ProcessId { get; set; }
        public DateTime SinceUtc { get; set; }
        public string Detail { get; set; }
        public bool Mine { get; set; }
    }

    public sealed class CatalogParams { public bool Refresh { get; set; } }

    public sealed class CatalogDto
    {
        public DateTime GeneratedAtUtc { get; set; }
        public bool FromCache { get; set; }
        public string GeneratedBy { get; set; }
        public List<CatalogEntryDto> Dtms { get; set; } = new List<CatalogEntryDto>();
    }

    public sealed class CatalogEntryDto
    {
        public string Clsid { get; set; }
        public string ProgId { get; set; }
        public string Name { get; set; }
        public string Vendor { get; set; }
        public string Version { get; set; }
        /// <summary>Versão do arquivo do servidor COM (registro): chave do cache de descrições.</summary>
        public string FileVersion { get; set; }
        /// <summary>"Communication", "Gateway" ou "Device".</summary>
        public string Role { get; set; }
        public List<string> Roles { get; set; } = new List<string>();
        public List<string> Fieldbuses { get; set; } = new List<string>();
        public bool Hostable { get; set; }
        public List<string> Issues { get; set; } = new List<string>();
        /// <summary>Preenchido quando a descrição (tipos de dispositivo) já está no cache da planta.</summary>
        public List<DeviceTypeDto> DeviceTypes { get; set; }
    }

    public sealed class DtmRef { public string Clsid { get; set; } }

    public sealed class ProjectRef { public string Name { get; set; } }

    public sealed class ProjectFileDto
    {
        public string Name { get; set; }
        public DateTime ModifiedUtc { get; set; }
        public int Nodes { get; set; }
        /// <summary>Estação que está com o projeto aberto para edição (null = livre).</summary>
        public string LockedBy { get; set; }
    }

    public sealed class ProjectDto
    {
        public string Name { get; set; }
        public bool ReadOnly { get; set; }
        public string LockedBy { get; set; }
        public bool Dirty { get; set; }
        public DateTime ModifiedUtc { get; set; }
        public List<NodeDto> Nodes { get; set; } = new List<NodeDto>();
    }

    public sealed class NodeDto
    {
        public string Tag { get; set; }
        public string Name { get; set; }
        public string ParentTag { get; set; }
        public string ChannelPath { get; set; }
        public string Role { get; set; }
        public string DtmClsid { get; set; }
        public string DtmName { get; set; }
        public string Vendor { get; set; }
        public string DeviceType { get; set; }
        /// <summary>"offline" (sem worker), "loading", "loaded", "connecting", "online", "error".</summary>
        public string State { get; set; }
        public string Policy { get; set; }
        public bool UiOpen { get; set; }
        public int? WorkerPid { get; set; }
        public string Error { get; set; }
        public bool Dirty { get; set; }
    }

    public sealed class NodeRef { public string Tag { get; set; } }

    public sealed class NodeAddParams
    {
        public string Clsid { get; set; }
        public int DeviceTypeIndex { get; set; }
        public string ParentTag { get; set; }
        public string ChannelPath { get; set; }
        public string Name { get; set; }
        public Dictionary<string, string> Properties { get; set; }
    }

    public sealed class NodeRenameParams
    {
        public string Tag { get; set; }
        public string Name { get; set; }
    }

    public sealed class NodeFunctionsDto
    {
        public List<FunctionDto> Functions { get; set; } = new List<FunctionDto>();
        public List<ChannelFunctionsDto> Channels { get; set; } = new List<ChannelFunctionsDto>();
    }

    public sealed class ChannelFunctionsDto
    {
        public string Path { get; set; }
        public bool IsCommunicationChannel { get; set; }
        public List<FunctionDto> Functions { get; set; } = new List<FunctionDto>();
    }

    public sealed class NodeUiParams
    {
        public string Tag { get; set; }
        public string FunctionId { get; set; }
        public string ChannelPath { get; set; }
        public long OwnerHwnd { get; set; }
    }

    public sealed class NodeConnectParams
    {
        public string Tag { get; set; }
        /// <summary>"Blocked", "ReadOnly" (padrão) ou "Full".</summary>
        public string Policy { get; set; } = "ReadOnly";
        /// <summary>Obrigatório para "Full": o operador confirmou que pode escrever no instrumento.</summary>
        public bool ConfirmFull { get; set; }
    }

    public sealed class NodeScanParams
    {
        public string Tag { get; set; }
        /// <summary>Tempo máximo da busca (o Comm DTM varre os endereços com as próprias repetições e tempos).</summary>
        public int TimeoutMs { get; set; } = 180000;
    }

    /// <summary>Resultado da busca de instrumentos (DTMTopologyScanSchema) com os DTMs candidatos de cada um.</summary>
    public sealed class ScanResultDto
    {
        public string Tag { get; set; }
        public string ChannelPath { get; set; }
        public long DurationMs { get; set; }
        public List<ScannedDeviceDto> Devices { get; set; } = new List<ScannedDeviceDto>();
        public string Xml { get; set; }
    }

    public sealed class ScannedDeviceDto
    {
        public string Protocol { get; set; }
        public string DeviceTag { get; set; }
        public int? ManufacturerId { get; set; }
        public int? DeviceTypeId { get; set; }
        public int? ShortAddress { get; set; }
        /// <summary>Endereço longo HART em hexadecimal (fabricante, tipo, 3 bytes de ID).</summary>
        public string LongAddress { get; set; }
        /// <summary>Nome do fabricante deduzido do catálogo (DTM que declara o mesmo código), se houver.</summary>
        public string ManufacturerName { get; set; }
        /// <summary>Nó do projeto, já abaixo deste canal, com o mesmo nome (tag) do instrumento.</summary>
        public string ExistingNodeTag { get; set; }
        /// <summary>DTMs candidatos, do melhor para o pior. Match: "exact" (fabricante e tipo), "vendor" (mesmo fabricante), "protocol".</summary>
        public List<DtmCandidateDto> Candidates { get; set; } = new List<DtmCandidateDto>();
    }

    public sealed class DtmCandidateDto
    {
        public string Clsid { get; set; }
        public string Name { get; set; }
        public string Vendor { get; set; }
        public int DeviceTypeIndex { get; set; }
        public string DeviceTypeName { get; set; }
        public string Match { get; set; }
    }

    /// <summary>Parâmetros do DTM (documento DTMParameterSchema achatado em lista).</summary>
    public sealed class ParameterSetDto
    {
        public string Tag { get; set; }
        /// <summary>fdt:storageState / fdt:dataSetState informados pelo DTM.</summary>
        public string StorageState { get; set; }
        public string DataSetState { get; set; }
        public List<ParameterItemDto> Items { get; set; } = new List<ParameterItemDto>();
        public string Xml { get; set; }
    }

    public sealed class ParameterItemDto
    {
        /// <summary>Caminho legível (grupos/nome), único no documento.</summary>
        public string Path { get; set; }
        public string Name { get; set; }
        public string Group { get; set; }
        public string Value { get; set; }
        public bool Writable { get; set; }
    }

    public sealed class CompareResultDto
    {
        public string Tag { get; set; }
        /// <summary>true = os valores lidos do instrumento ficaram no DTM (Ler); false = o projeto foi restaurado (Comparar).</summary>
        public bool KeptDeviceValues { get; set; }
        public OnlineParameterResult Upload { get; set; }
        /// <summary>Veredito do próprio DTM (IDtmOnlineDiagnosis::Compare: equal, notEqual, noComparableData), se ele implementar.</summary>
        public string DtmVerdict { get; set; }
        public int Differences { get; set; }
        public List<CompareItemDto> Items { get; set; } = new List<CompareItemDto>();
        /// <summary>Comandos recusados pela política da conexão durante a leitura (ex.: somente leitura).</summary>
        public int RefusedCommands { get; set; }
        /// <summary>Motivo de cada recusa (ex.: "política ReadOnly: comando 173 não é leitura conhecida").</summary>
        public List<string> RefusedNotes { get; set; } = new List<string>();
        public bool CanRevert { get; set; }
    }

    public sealed class CompareItemDto
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public string Group { get; set; }
        public string ProjectValue { get; set; }
        public string DeviceValue { get; set; }
        public bool Different { get; set; }
        public bool Writable { get; set; }
    }

    public sealed class NodeDownloadParams
    {
        public string Tag { get; set; }
        /// <summary>Obrigatório: o operador confirmou a gravação no instrumento.</summary>
        public bool Confirm { get; set; }
    }

    public sealed class HostEventDto
    {
        public DateTime Time { get; set; } = DateTime.Now;
        /// <summary>"frame", "comm", "ui", "worker", "plant", "project", "catalog".</summary>
        public string Kind { get; set; }
        public string Tag { get; set; }
        public string Method { get; set; }
        public string Detail { get; set; }
        /// <summary>"info", "warning", "error".</summary>
        public string Level { get; set; } = "info";
    }
}
