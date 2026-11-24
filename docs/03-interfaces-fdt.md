# 03 — Catálogo das interfaces FDT 1.2.1

**Fontes:** IIDs e assinaturas extraídos da type library oficial `specs/FDT-1.2.1/spec/Bin/FDT100.dll` (LIBID `{036D1471-387B-11D4-86E1-00E0987270B9}`, versão `1.20100`) pela ferramenta `lasecfdt typelib` — listagem completa, no formato IDL, em **[generated/fdt100-typelib.md](generated/fdt100-typelib.md)**. Responsabilidades, obrigatoriedade e estados vêm da especificação 1.2.1 (seções indicadas). O teste `IidsDaTypeLib_ConferemComOIdlDaSpec` confere os IIDs contra o IDL impresso no Cap. 11, e `InteropAssembly_UsaOsMesmosIidsDaTypeLib` confere as 49 interfaces do interop `Jigfdt.fdt100.dll`.

Convenções comuns a todas (§4.10, §8):
- Todas são **duais** (`IDispatch`), métodos retornam `HRESULT`; o resultado funcional vem em `[out, retval]`.
- `FdtXmlDocument`, `FdtXPath`, `FdtUUIDString` são *aliases* de `BSTR` (GUIDs `036D1473`, `036D1474`, `036D1472`). `FdtUUIDString` é um GUID sem chaves, comparado sem diferenciar maiúsculas (§8.2).
- `invokeId`: GUID gerado pelo chamador (`CoCreateGuid`) para correlacionar *Request*/*Response* assíncronos (§8.1).
- `systemTag`: identificador da instância **no projeto do Frame**; o DTM não deve persisti-lo (§4.3.1.2, §8.1).
- Obrigatoriedade: tabela de §4.10.1 ("Task Related FDT Interfaces").

## 1. Visão geral

| Interface | IID | Implementada por | Obrigatória? (§4.10.1) | Seção |
|---|---|---|---|---|
| IDtm | 036D1481-387B-11D4-86E1-00E0987270B9 | DTM | **Sim** | 4.3.1 |
| IDtm2 | 51E1F44B-D6A1-423D-B11F-AD38EDE78047 | DTM | Sim para DTM 1.2.1 (DTMs 1.2 não têm) | 4.3.2 |
| IDtmActiveXInformation | 036D1480-387B-11D4-86E1-00E0987270B9 | DTM | Opcional (obrig. p/ DTM com UI ActiveX) | 4.3.3 |
| IDtmApplication | 036D147E-387B-11D4-86E1-00E0987270B9 | DTM | Opcional (obrig. p/ DTM com UI própria) | 4.3.4 |
| IDtmChannel | 036D1489-387B-11D4-86E1-00E0987270B9 | DTM | Opcional (obrig. Gateway/Comm DTM) | 4.3.5 |
| IDtmDocumentation | 036D147C-387B-11D4-86E1-00E0987270B9 | DTM | Sim | 4.3.6 |
| IDtmDiagnosis | 036D1476-387B-11D4-86E1-00E0987270B9 | DTM | Sim | 4.3.7 |
| IDtmImportExport | 036D148E-387B-11D4-86E1-00E0987270B9 | DTM | Opcional | 4.3.8 |
| IDtmInformation | 036D147F-387B-11D4-86E1-00E0987270B9 | DTM | **Sim** | 4.3.9 |
| IDtmInformation2 | C2934CC6-B72D-4611-890F-1C6531D1F8EB | DTM | Sim (1.2.1) | 4.3.10 |
| IDtmOnlineDiagnosis | 036D1477-387B-11D4-86E1-00E0987270B9 | DTM | Sim | 4.3.11 |
| IDtmOnlineParameter | 036D1483-387B-11D4-86E1-00E0987270B9 | DTM | Opcional | 4.3.12 |
| IDtmParameter | 036D147D-387B-11D4-86E1-00E0987270B9 | DTM | Sim | 4.3.13 |
| IFdtCommunicationEvents | 036D1485-387B-11D4-86E1-00E0987270B9 | DTM (callback de comunicação) | Opcional (obrig. p/ quem comunica) | 4.3.14 |
| IFdtCommunicationEvents2 | 7A0AEF6A-A9E7-4673-8F45-01B8AC28F55D | DTM | Opcional | 4.3.15 |
| IFdtEvents | 036D1478-387B-11D4-86E1-00E0987270B9 | DTM | Sim | 4.3.16 |
| IDtmHardwareIdentification | 9A1DD233-987C-43D1-9424-DA5C1FC6F292 | DTM | Opcional | 4.3.17 |
| IDtmSingleDeviceDataAccess | D67240E4-664B-44B0-B692-A1D1ED3FB8F8 | DTM | Opcional | 4.3.18 |
| IDtmSingleInstanceDataAccess | 84F9A19A-7E38-40B5-A311-60B14F30C258 | DTM | Sim (1.2.1) | 4.3.19 |
| IDtmActiveXControl | 036D1486-387B-11D4-86E1-00E0987270B9 | Controle ActiveX do DTM | **Sim** (para o controle) | 4.4.1 |
| IFdtChannel | 036D1488-387B-11D4-86E1-00E0987270B9 | Canal | Sim (canal) | 4.5.1 |
| IFdtChannelActiveXInformation | F2BD2970-13FA-470C-A28C-6A5969A04037 | Canal | Opcional | 4.5.2 |
| IFdtCommunication | 039ECFC4-9CA8-44E6-944D-B37F288A34D8 | Canal de comunicação | Obrig. canal de Comm/Gateway DTM | 4.5.3 |
| IFdtChannelSubTopology | 036D1484-387B-11D4-86E1-00E0987270B9 | Canal | Obrig. Comm/Gateway | 4.5.4 |
| IFdtChannelSubTopology2 | 6359FBF1-D373-4202-90E8-E696C37739D4 | Canal | Obrig. Comm/Gateway (1.2.1) | 4.5.5 |
| IFdtChannelScan | 64A4310D-F9EE-411D-B4F9-51D3360DF359 | Canal | Obrig. Comm/Gateway (1.2.1) | 4.5.6 |
| IFdtFunctionBlockData | 036D1475-387B-11D4-86E1-00E0987270B9 | Canal | Obrig. Comm/Gateway | 4.5.7 |
| IFdtChannelCollection | E4F31A10-45BF-11D4-BBB3-0060080993FF | DTM (coleção) | Retorno de `GetChannels` | IDL |
| IFdtChannelActiveXControl | 036D148B-387B-11D4-86E1-00E0987270B9 | Controle do canal | Sim (controle) | 4.6.1 |
| IFdtChannelActiveXControl2 | 73757F49-F3A6-41F7-BEA0-1A3E59D69D5B | Controle do canal | Sim (1.2.1) | 4.6.2 |
| IBtm / IBtmInformation / IBtmParameter | 96341E37… / 87DCC81C… / 43D592CC… | BTM | Sim (BTM) | 4.7 |
| IBtmActiveXControl | 0E0418B4-9BC6-4A28-B980-5D3E7F457E4F | Controle do BTM | Sim (BTM) | 4.8.1 |
| **IFdtContainer** | 036D1487-387B-11D4-86E1-00E0987270B9 | **Frame** | **Sim** | 4.9.8 |
| **IDtmEvents** | F15BA42E-BBF1-42ED-8009-7F664A002CFB | **Frame** | **Sim** | 4.9.1 |
| **IDtmEvents2** | 494FFD2B-6E58-4E42-80DB-85EBDF6E2CF5 | **Frame** | **Sim** | 4.9.2 |
| IDtmScanEvents | 515590B9-5177-474A-9310-708A3E785B2B | Frame | Opcional | 4.9.3 |
| **IDtmAuditTrailEvents** | 036D1479-387B-11D4-86E1-00E0987270B9 | **Frame** | **Sim** | 4.9.4 |
| IFdtActiveX | 5959F485-2C51-4A55-80A7-DD3C45D8BAF2 | Frame | Opcional ("must" se o Frame tem GUI, §4.9.5) | 4.9.5 |
| IFdtActiveX2 | 1922C2DE-4EE7-4085-878A-80AC6C6728AD | Frame | Opcional | 4.9.6 |
| IFdtBulkData | 036D148D-387B-11D4-86E1-00E0987270B9 | Frame | Opcional | 4.9.7 |
| **IFdtDialog** | 15C19931-6161-11D4-A0A9-005004011A04 | **Frame** | **Sim** | 4.9.9 |
| **IFdtTopology** | 036D147A-387B-11D4-86E1-00E0987270B9 | **Frame** | **Sim** | 4.9.10 |
| IDtmRedundancyEvents | 04808A4C-90C3-45A7-B69E-034A2FA8314D | Frame | Opcional | 4.9.11 |
| **IDtmSingleDeviceDataAccessEvents** | 2357691C-E69A-4E0A-A8AA-EB7F1D080CEF | **Frame** | **Sim** | 4.9.12 |
| **IDtmSingleInstanceDataAccessEvents** | EBC093F1-4F7A-4208-A209-C172E54AB185 | **Frame** | **Sim** | 4.9.13 |
| **IFdtBtmTopology** | 18250F40-73FB-4C22-A0F1-DB2A11B3FE8D | **Frame** | **Sim** | 4.9.14 |

Interfaces COM **padrão** que fazem parte do contrato (não estão na type library FDT como interfaces próprias):

| Interface | IID | Quem | Uso |
|---|---|---|---|
| IPersistStreamInit | 7FD52380-4E07-101B-AE2D-08002B2EC713 | DTM | Persistência (§2.7.1) — um dos dois é obrigatório |
| IPersistPropertyBag | 37D84F60-42CB-11CE-8135-00AA004BB851 | DTM | Persistência (§2.7.1) |
| IPropertyBag / IErrorLog | 55272A00-… / 3127CA40-… | Frame | Armazenamento passado ao DTM |
| IStream | 0000000C-0000-0000-C000-000000000046 | Frame | Stream para `IPersistStreamInit` e `IDtmImportExport` |
| ICatInformation | 0002E013-0000-0000-C000-000000000046 | Windows | Descoberta por categoria (§7.1.5) |

(IIDs de persistência conferidos com as declarações `Jigfdt.COMInterop.UCOMIPersist*` do interop oficial.)

## 2. Interfaces do DTM usadas pelo Frame

### IDtm — interface principal (§4.3.1)
Inicialização, contexto e liberação. Métodos (dispid): `Environment(systemTag, IFdtContainer*)` 0x1, `InitNew(deviceType)` 0x2, `Config(userInfo)` 0x3, `SetCommunication(IFdtCommunication*)` 0x4, `PrepareToRelease()` 0x5, `PrepareToReleaseCommunication()` 0x6, `ReleaseCommunication()` 0x7, `PrepareToDelete()` 0x8, `SetLanguage(long lcid)` 0x9, `GetFunctions(operationState) → FdtXmlDocument` 0xa, `InvokeFunctionRequest(invokeId, functionCall)` 0xb, `PrivateDialogEnabled(VARIANT_BOOL)` 0xc. Todos exceto `GetFunctions` retornam `VARIANT_BOOL`.

| Método | Documento XML | Estado de entrada → saída | Observação |
|---|---|---|---|
| Environment | — | new/existing created → new enabled/running | Passa o ponteiro do Frame (back pointer) |
| InitNew | DTMInitSchema (contém um `fdt:DtmDeviceType` vindo de GetInformation) | new enabled → running | Seleciona o tipo de equipamento |
| Config | FDTUserInformationSchema | running → configured | Papel e direitos do usuário |
| SetCommunication | — | configured → communication set | `NULL` para DTM raiz (§4.3.1.11) |
| PrepareToRelease | — | → zombie, confirmado por `IDtmEvents::OnPreparedToRelease` | Assíncrono |
| GetFunctions | entrada FDTOperationPhaseSchema, saída DTMFunctionsSchema | running+ | Base dos menus |
| InvokeFunctionRequest | DTMFunctionCallSchema | configured+ | Função sem UI; fim via `OnInvokedFunctionFinished` |

**Sequência:** §6.13.1. **Testado:** sim, 24 DTMs reais.

### IDtm2 (§4.3.2)
`Environment2(systemTag, container, frameInfo)` substitui `Environment` (frameInfo em DTMEnvironmentSchema declara a versão do Frame); `SetSystemGuiLabel(xml)`. O Frame 1.2.1 **deve** preferi-la se existir (§3.3). Nenhum DTM instalado a implementa (todos FDT 1.2); caminho implementado, não validado.

### IDtmInformation (§4.3.9)
`GetInformation() → FdtXmlDocument` (DTMInformationSchema): versão FDT, fabricante, versão, **lista de `DtmDeviceType`** (cada um com barramentos, idiomas, variantes, ícones, documentos e, opcionalmente, `DtmSchemaPaths`). Válida a partir de *new enabled*. Base do catálogo (§7.1.6). **Testado.**

### IDtmActiveXInformation (§4.3.3)
`GetActiveXGuid(functionCall) → FdtUUIDString`, `GetActiveXProgId(functionCall) → BSTR`. Devolve o CLSID do controle que implementa a função pedida; vazio se a função não tem UI ActiveX. Válida a partir de *configured*. **Testado** (23 de 24 DTMs devolvem GUID para ao menos uma função).

### IDtmApplication (§4.3.4)
`StartApplication(invokeId, functionCall, windowTitle)`, `ExitApplication(invokeId)`. UI não embutível, de janela própria do DTM; fim sinalizado por `OnApplicationClosed(invokeId)` (§6.6). Implementado, nenhum DTM instalado a expõe.

### IDtmParameter (§4.3.13) / IDtmSingleInstanceDataAccess (§4.3.19)
`GetParameters(xpath)` / `SetParameters(xpath, xml)` com DTMParameterSchema ("parâmetros públicos", §2.6.1); `GetItemList/Read/Write` com DTMItemListSchema (acesso item a item, 1.2.1). Base para pesquisa de parâmetros na UI futura. Planejado.

### IDtmChannel (§4.3.5) → IFdtChannelCollection → IFdtChannel (§4.5.1)
`GetChannels()` devolve a coleção (`Item(VARIANT*)`, `Count()`, `_NewEnum`). Cada `IFdtChannel` dá `GetChannelPath`, `Get/SetChannelParameters(xpath, protocolId[, xml])`. Num Comm DTM, o mesmo objeto canal implementa `IFdtCommunication`. **Comprovado por QI** no CodeWrights HART (`IDtmChannel`, `IFdtChannel`, `IFdtCommunication`, `IFdtChannelSubTopology`).

### IFdtCommunication (§4.5.3) e IFdtCommunicationEvents(2) (§4.3.14–15)
Canal: `ConnectRequest(callback, invokeId, protocolId, fieldbusFrame)`, `TransactionRequest(invokeId, frame)`, `DisconnectRequest(invokeId, frame)`, `Abort(frame)`, `GetSupportedProtocols()`, `SequenceBegin/Start/End`. Callback do DTM filho: `OnConnectResponse`, `OnTransactionResponse`, `OnDisconnectResponse`, `OnAbort` (e `OnConnectResponse2` com a versão do pai). Os frames XML são do schema do protocolo (ex.: FDTHARTCommunicationSchema). Planejado.

### IPersistStreamInit / IPersistPropertyBag (§2.7, §6.13–6.14)
`InitNew()` (nova instância), `Load(...)` (instância existente), `Save(..., fClearDirty)`. **Testado**: os 24 DTMs expõem `IPersistStreamInit`; 5 expõem também `IPersistPropertyBag` (preferida pelo Frame, como no diagrama de §6.13.1). Os dois caminhos passaram no round-trip idêntico entre processos.

## 3. Interfaces do controle ActiveX

### IDtmActiveXControl (§4.4.1)
`Init(invokeId, functionCall, IDtm*) → VARIANT_BOOL`; `PrepareToRelease() → VARIANT_BOOL`. O Frame cria o controle com o CLSID de `GetActiveXGuid`, embute e chama `Init` (§6.13.3). Para fechar: `PrepareToRelease` → o DTM chama `IDtmEvents::OnApplicationClosed(invokeId)` → o Frame libera (§6.13.4). Se `Init` retorna FALSE o Frame deve liberar o controle; se `PrepareToRelease` retorna FALSE o controle continua aberto. **Testado** (ver doc 08).

## 4. Interfaces implementadas pelo Frame (LasecFDT: `FrameContainer`)

Todas no **mesmo objeto COM** passado em `Environment`; o DTM obtém as de tarefa por `QueryInterface` (§2.4.3, §4.2.1).

| Interface | Métodos | Comportamento no LasecFDT (fase atual) |
|---|---|---|
| IFdtContainer | `SaveRequest`, `LockDataSet`, `UnlockDataSet`, `GetXmlSchemaPath` | Marca a sessão como "salvar pendente"; bloqueio sempre concedido (mono-usuário); devolve `…\FDTSchemas\` com barra final (§4.9.8.1) |
| IDtmEvents | 15 métodos (`OnParameterChanged`, `OnErrorMessage`, `OnProgress`, `OnUploadFinished`, `OnDownloadFinished`, `OnApplicationClosed`, `OnFunctionChanged`, `OnChannelFunctionChanged`, `OnPrint`, `OnNavigation`, `OnOnlineStateChanged`, `OnPreparedToRelease`, `OnPreparedToReleaseCommunication`, `OnInvokedFunctionFinished`, `OnScanResponse`) — **dispid 0x7 não existe** na type library | Todos registrados como eventos; `OnPreparedToRelease` e `OnApplicationClosed` dirigem o ciclo de vida |
| IDtmEvents2 | `OnStateChanged(systemTag, xml)` | Registrado |
| IDtmAuditTrailEvents | `OnStartTransaction`, `OnAuditTrailEvent`, `OnEndTransaction` | Registrado; retorna TRUE |
| IFdtDialog | `UserDialog(systemTag, userMessage) → xml` | UI: MessageBox; sem UI: devolve a mensagem com `resultMessage=messageDefault`, `resultStatus=systemResponse` (FDTUserMessageSchema) |
| IFdtTopology | `GetParentNodes`, `GetChildNodes`, `CreateChild`, `DeleteChild`, `GetDtmForSystemTag`, `GetDtmInfoList`, `MoveChild`, `ReleaseDtmForSystemTag` | Topologia plana: listas vazias válidas (DTMSystemTagListSchema); `GetDtmForSystemTag` devolve DTMs abertos |
| IFdtBtmTopology | equivalente para BTMs | Sem BTMs: vazio/FALSE |
| IDtmSingleDeviceDataAccessEvents | `OnItemListResponse`, `OnDeviceItemListChanged`, `OnReadResponse`, `OnWriteResponse` | Registrado |
| IDtmSingleInstanceDataAccessEvents | `OnInstanceItemListChanged` | Registrado |
| IFdtActiveX / IFdtActiveX2 | `Open/CloseActiveXControlRequest`, versões de diálogo e de canal | Encaminhado à UI (`IFrameUi`); host de testes ainda recusa abrir por pedido do DTM |
| IFdtBulkData | `GetProjectRelatedPath`, `GetInstanceRelatedPath` | Diretórios em `%LOCALAPPDATA%\LasecFDT\BulkData\…\` |

Não implementadas (opcionais): `IDtmScanEvents`, `IDtmRedundancyEvents`.

## 5. Lacunas deste catálogo

- As tabelas de obrigatoriedade/estado foram lidas de imagens do PDF (fonte de símbolos não extraível); conferidas visualmente nas págs. 43–45 e 157–158.
- O conteúdo dos **schemas XML de protocolo** (HART 1.2.1) não está na documentação fornecida (ver doc 09).
