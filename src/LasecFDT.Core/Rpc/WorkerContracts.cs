using System.Collections.Generic;
using LasecFDT.Core.Project;

namespace LasecFDT.Core.Rpc
{
    /// <summary>Nomes dos métodos do worker FDT 1.x (um processo = uma instância de DTM).</summary>
    public static class WorkerMethods
    {
        public const string Hello = "hello";                         // → WorkerInfo
        public const string Describe = "describe";                   // DescribeParams → DtmDescription (GetInformation sem InitNew; libera em seguida)
        public const string Create = "create";                       // CreateDtmParams → DtmSummary (nova instância até "configured")
        public const string Load = "load";                           // LoadParams → DtmSummary (instância existente até "configured")
        public const string Save = "save";                           // → PersistedDataSet
        public const string GetFunctions = "getFunctions";           // → FunctionDto[]
        public const string SetCommunicationNull = "setCommunicationNull"; // DTM raiz da cadeia (§4.3.1.11) → bool
        public const string GetChannels = "getChannels";             // → ChannelDto[]
        public const string MarshalChannel = "marshalChannel";       // ChannelRefParams → MarshaledChannel (OBJREF do canal)
        public const string AddChild = "addChild";                   // ChildParams → bool (ValidateAddChild + OnAddChild)
        public const string RemoveChild = "removeChild";             // ChildParams → bool (ValidateRemoveChild + OnRemoveChild)
        public const string ConnectChannel = "connectChannel";       // ConnectChannelParams → ConnectResult (SetCommunication(guarda))
        public const string ConnectSimulator = "connectSimulator";   // SimulatorParams → ConnectResult (canal HART simulado do Frame)
        public const string ReleaseCommunication = "releaseCommunication"; // → bool
        public const string Scan = "scan";                           // ScanParams → ScanResult (IFdtChannelSubTopology::ScanRequest → IDtmEvents::OnScanResponse)
        public const string GetParameters = "getParameters";         // ParameterPathParams → string (IDtmParameter::GetParameters, DTMParameterSchema)
        public const string SetParameters = "setParameters";         // SetParametersParams → bool (IDtmParameter::SetParameters, offline)
        public const string Upload = "upload";                       // OnlineParameterParams → OnlineParameterResult (IDtmOnlineParameter::UploadRequest → OnUploadFinished)
        public const string Download = "download";                   // OnlineParameterParams → OnlineParameterResult (DownloadRequest → OnDownloadFinished)
        public const string OnlineCompare = "onlineCompare";         // → string (IDtmOnlineDiagnosis::Compare, DTMOnlineCompareSchema)
        public const string DeviceStatus = "deviceStatus";           // → string (IDtmOnlineDiagnosis::GetDeviceStatus, DTMDeviceStatusSchema)
        public const string GetCommLog = "getCommLog";               // → CommRecordDto[]
        public const string WaitOnline = "waitOnline";               // WaitParams → bool
        public const string OpenUi = "openUi";                       // OpenUiParams → UiResult
        public const string InvokeFunction = "invokeFunction";       // InvokeParams → InvokeResult (IDtm::InvokeFunctionRequest, §4.3.1.5)
        public const string WaitFunction = "waitFunction";           // WaitFunctionParams → bool (OnInvokedFunctionFinished/OnApplicationClosed)
        public const string GetChannelFunctions = "getChannelFunctions"; // ChannelRefParams → FunctionDto[] (§4.5.2.3)
        public const string OpenChannelUi = "openChannelUi";         // ChannelUiParams → UiResult (UI do canal, §4.6/§6.24)
        public const string CloseUi = "closeUi";                     // → bool
        public const string Screenshot = "screenshot";               // ScreenshotParams → int (cores distintas)
        public const string Release = "release";                     // → bool (PrepareToRelease → OnPreparedToRelease → Release)
        public const string Shutdown = "shutdown";                   // libera tudo e encerra o processo
        public const string Event = "event";                         // notificação worker → supervisor (WorkerEvent)
    }

    public sealed class WorkerInfo
    {
        public int ProcessId { get; set; }
        public string Architecture { get; set; }
        public string Dep { get; set; }
        public string Version { get; set; }
        public string SchemaPath { get; set; }
    }

    public sealed class DescribeParams
    {
        public string Dtm { get; set; }
        public int Lcid { get; set; } = 0x0409;
    }

    /// <summary>Informação estática de um DTM (IDtmInformation::GetInformation): base do catálogo de tipos de dispositivo.</summary>
    public sealed class DtmDescription
    {
        public string Clsid { get; set; }
        public string FdtVersion { get; set; }
        public string Name { get; set; }
        public string Vendor { get; set; }
        public string Version { get; set; }
        public string Date { get; set; }
        public List<DeviceTypeDto> DeviceTypes { get; set; } = new List<DeviceTypeDto>();
    }

    public sealed class DeviceTypeDto
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public string Vendor { get; set; }
        public string Version { get; set; }
        public List<string> BusCategories { get; set; } = new List<string>();
        /// <summary>Identidade do instrumento declarada pelo DTM (HART: código do fabricante e do tipo). Base da
        /// associação automática na busca de instrumentos.</summary>
        public int? ManufacturerId { get; set; }
        public int? DeviceTypeId { get; set; }
    }

    public sealed class CreateDtmParams
    {
        public string Dtm { get; set; }                  // ProgID ou CLSID
        /// <summary>systemTag desejado (o Frame atribui um novo se vazio).</summary>
        public string SystemTag { get; set; }
        public int DeviceTypeIndex { get; set; }
        public int Lcid { get; set; } = 0x0409;
        /// <summary>Propriedades do data set (PropertyBag) a aplicar antes da carga (ex.: Port=COM20).</summary>
        public Dictionary<string, string> Properties { get; set; }
    }

    public sealed class LoadParams
    {
        public string Dtm { get; set; }
        public string SystemTag { get; set; }
        public PersistedDataSet DataSet { get; set; }
        public int Lcid { get; set; } = 0x0409;
    }

    public sealed class DtmSummary
    {
        public string SystemTag { get; set; }
        public string Clsid { get; set; }
        public string State { get; set; }
        public string FdtVersion { get; set; }
        public string Name { get; set; }
        public string Vendor { get; set; }
        public string Version { get; set; }
        public string DeviceTypeXml { get; set; }
        public string PersistenceKind { get; set; }
        public List<string> Interfaces { get; set; }
    }

    public sealed class FunctionDto
    {
        public string FunctionId { get; set; }
        public string Name { get; set; }
        public string ApplicationId { get; set; }
        public bool Enabled { get; set; }
        public bool Hidden { get; set; }
    }

    public sealed class ChannelDto
    {
        public string Path { get; set; }
        public bool IsCommunicationChannel { get; set; }
        public bool HasSubTopology { get; set; }
        public bool HasActiveX { get; set; }
        public string SupportedProtocolsXml { get; set; }
    }

    public sealed class ChannelRefParams { public string ChannelPath { get; set; } }

    /// <summary>Referência COM serializada (OBJREF de CoMarshalInterface, MSHCTX_LOCAL) — vale para um único unmarshal.</summary>
    public sealed class MarshaledChannel
    {
        public string ChannelPath { get; set; }
        public string ObjRefBase64 { get; set; }
    }

    public sealed class ChildParams
    {
        public string ChannelPath { get; set; }
        public string ChildTag { get; set; }
    }

    public sealed class ConnectChannelParams
    {
        public string ParentTag { get; set; }
        public string ChannelPath { get; set; }
        public string ObjRefBase64 { get; set; }
        public string Policy { get; set; } = "Blocked";
    }

    public sealed class SimulatorParams
    {
        public string Policy { get; set; } = "Full";
    }

    public sealed class ConnectResult
    {
        public bool Accepted { get; set; }
        public string State { get; set; }
        public string Error { get; set; }
    }

    public sealed class WaitParams { public int TimeoutMs { get; set; } = 10000; }

    public sealed class CommRecordDto
    {
        public string Time { get; set; }
        public string Direction { get; set; }
        public string Method { get; set; }
        public bool Forwarded { get; set; }
        public string Note { get; set; }
        public string Xml { get; set; }
    }

    public sealed class OpenUiParams
    {
        public string FunctionId { get; set; }
        /// <summary>HWND (decimal) da janela dona — ex.: a janela Flutter. 0 = sem dono.</summary>
        public long OwnerHwnd { get; set; }
        public bool Visible { get; set; } = true;
        /// <summary>Janela sem moldura nem barra de tarefas, para ser encaixada na área de conteúdo da UI (quem posiciona é a UI).</summary>
        public bool Embedded { get; set; }
    }

    public sealed class InvokeParams { public string FunctionId { get; set; } }

    public sealed class InvokeResult
    {
        public bool Started { get; set; }
        public string InvokeId { get; set; }
        public string FunctionName { get; set; }
        public string Error { get; set; }
    }

    public sealed class WaitFunctionParams
    {
        public string InvokeId { get; set; }
        public int TimeoutMs { get; set; } = 30000;
    }

    public sealed class ChannelUiParams
    {
        public string ChannelPath { get; set; }
        public string FunctionId { get; set; }
        public long OwnerHwnd { get; set; }
        public bool Visible { get; set; } = true;
        public bool Embedded { get; set; }
    }

    public sealed class UiResult
    {
        public bool Opened { get; set; }
        public string FunctionId { get; set; }
        public string FunctionName { get; set; }
        public string ControlClsid { get; set; }
        public long WindowHandle { get; set; }
        public string Error { get; set; }
    }

    public sealed class ScreenshotParams { public string Path { get; set; } }

    public sealed class ScanParams
    {
        public string ChannelPath { get; set; }
        public int TimeoutMs { get; set; } = 180000;
    }

    public sealed class ScanResult
    {
        public bool Accepted { get; set; }
        public bool Completed { get; set; }
        public string InvokeId { get; set; }
        /// <summary>Documento DTMTopologyScanSchema devolvido pelo DTM.</summary>
        public string Xml { get; set; }
        public string Error { get; set; }
        public long DurationMs { get; set; }
    }

    public sealed class ParameterPathParams
    {
        /// <summary>FdtXPath no documento de IDtmParameter::GetParameters. Vazio = documento inteiro (ver ParameterRootPath).</summary>
        public string Path { get; set; }
    }

    public sealed class SetParametersParams
    {
        public string Path { get; set; }
        public string Xml { get; set; }
    }

    public sealed class OnlineParameterParams
    {
        /// <summary>FdtXPath; null = raiz do documento de parâmetros (todos os parâmetros do instrumento, §4.3.12).</summary>
        public string Path { get; set; }
        public int TimeoutMs { get; set; } = 300000;
    }

    public sealed class OnlineParameterResult
    {
        public bool Accepted { get; set; }
        public bool Completed { get; set; }
        public bool Success { get; set; }
        public string InvokeId { get; set; }
        public string Path { get; set; }
        public long DurationMs { get; set; }
        /// <summary>IDtmEvents::OnErrorMessage recebidos durante a operação.</summary>
        public List<string> Messages { get; set; } = new List<string>();
        public string Error { get; set; }
    }

    /// <summary>Evento assíncrono do worker (eventos do Frame vindos do DTM, tráfego de comunicação, pedidos de UI).</summary>
    public sealed class WorkerEvent
    {
        public string Kind { get; set; }          // "frame" | "comm" | "ui"
        public string Interface { get; set; }
        public string Method { get; set; }
        public string SystemTag { get; set; }
        public string InvokeId { get; set; }
        public string Detail { get; set; }
    }
}
