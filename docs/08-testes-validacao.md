# 08 — Testes executados e resultados

Ambiente: Windows 11 Pro 10.0.26100 (x64), .NET Framework 4.8.1, SDK .NET 10.0.115, processos de teste **x86**. Data: 23–24/11/2026. Seções 1–6: testes **offline**. Seções 7–8 (comunicação, autorizada pelo usuário): canais reais, mas **sempre contra instrumento simulado** — canal simulado do Frame ou par de portas virtuais com0com (`COM20 ↔ CNCB0`); nenhum instrumento real acessado. O PACTware instalado **não foi executado nem modificado**. Único registro alterado: o do DTM do próprio usuário (`LasecHartCommDTM`), re-registrado com `regasm /codebase` a pedido dele (§11).

Classificação: **Implementado e validado** = comprovado por execução real · **Implementado, não validado** = sem DTM/condição para provar · **Planejado** · **Bloqueado**.

## 1. Testes automatizados (`dotnet test tests/LasecFDT.Tests`)

**52 de 52 aprovados**, 0 avisos de compilação (execução sequencial — `AssemblyInfo.cs` desliga o paralelismo do xUnit: DTMs reais não toleram instâncias simultâneas no mesmo processo, e os testes de porta virtual disputam o mesmo par).

| Grupo | Testes | O que provam |
|---|---|---|
| TypeLib | 8 | LIBID/versão 1.20100; IIDs conferem com o IDL da spec; as 49 interfaces do interop têm os mesmos IIDs da type library; `FrameContainer` implementa as 9 interfaces obrigatórias do Frame |
| FdtXml | 5 | Documentos gerados são XML válido com raiz `FDT`; leitura dos exemplos oficiais (Information, Functions, UserMessage) |
| Persistência | 4 | `PropertyBagStore` round-trip de 10 tipos VARIANT; `E_INVALIDARG` para propriedade inexistente; `IStream` gerenciado; gravação atômica de projeto |
| Descoberta | 2 | Leitura de cabeçalho PE; só classes `CATID_FDT_DTM/BTM` e concordância 25/25 com `ICatInformation` |
| DTM real | 5 | LD301 ciclo completo + round-trip + 2 `OnPreparedToRelease`; TT301 por PropertyBag; CodeWrights expõe canal HART; CLSID inexistente → `0x80040154` controlado; criação fora de STA rejeitada |
| Comunicação (sem DTM) | 8 | Validador XDR (MSXML 3) reproduz o erro `0xC00CE015` visto no TT301; todo XML emitido pelo Frame/simulador é válido contra os schemas; guarda `Blocked` não deixa nada passar; `ReadOnly` só repassa leituras conhecidas; simulador conecta/transaciona/desconecta de forma assíncrona; topologia `GetParentNodes/GetChildNodes`; **enlace HART real** pela porta virtual (cmd 0 curto, cmd 13 longo, checksum, endereço alheio ignorado) |
| Comunicação (DTM real) | 3 | TT301 no canal simulado: conecta, XML válido, libera na ordem da spec; TT301 com `Blocked`: tenta conectar e nada chega ao canal; **cadeia real** CodeWrights (COM20) → com0com → simulador de fio (CNCB0) → TT301 online, só comandos de leitura |
| DTM do usuário (LasecHartCommDTM) | 3 | Todo XML do DTM válido contra os schemas XDR; persistência por property bag (vtable) ida e volta; **HART-IP UDP com gateway WirelessHART simulado**: conexão a sub-dispositivo pelo endereço longo com resposta atrasada (33), transação, desconexão síncrona, erro `noConnection`, varredura do Frame (gateway + 2 sub-dispositivos, `OnProgress`); **cadeia real** DTM do usuário (COM20) → com0com → simulador de fio → TT301 online |
| Host da estação (thin client) | 6 | Arrendamentos entre estações (dono informado, liberação ao encerrar); chave de porta; **duas estações** na mesma planta com Host e workers próprios: projeto somente leitura na 2ª, porta recusada com o nome da 1ª, catálogo reaproveitado, política `Full` sem confirmação recusada, reabertura pelo data set com o mesmo systemTag, auditoria das duas; **busca de instrumentos** (análise do DTMTopologyScanSchema, candidatos por fabricante/tipo, instrumento simulado encontrado e associado ao TT301, "já no projeto"); **parâmetros pelo Frame** (lista, comparação com restauração, gravação só com modo completo + confirmação) (§12) |
| Worker + supervisor | 8 | Worker sem NXCOMPAT, com DEP ativo e emulação de thunks; ciclo completo e persistência **entre workers** (data set idêntico); **UI do TT301** no worker (abre, renderiza, fecha, saída 0); UI da CodeWrights pelo **ProgID**; `InvokeFunctionRequest` com resultado por evento; queda de worker isolada e detectada; **cadeia entre processos** (CodeWrights num worker, TT301 em outro, proxy COM do canal, callback de volta, UI do filho com canal remoto); **queda do worker do Comm DTM** (filho sobrevive, ligação desfeita, servidor de porta órfão encerrado) |

Os testes com DTM real são marcados `[DtmFact]` e são **pulados** (não falham) em máquinas sem o DTM.

## 2. Descoberta (`lasecfdt discover`) — Implementado e validado

- 25 classes em `CATID_FDT_DTM` (vista 32), 0 na vista 64, 0 BTMs; **25/25 iguais** ao `ICatInformation`.
- Fabricantes: Smar (23), CodeWrights (1, Comm DTM HART em OCX VB6), Lasec (1, .NET).
- Detectado e reportado (23/11): `LasecHartCommDTM` (do usuário) registrado com `CodeBase` inexistente — corrigido em 24/11 (§11); binários sem recurso de versão; DLLs instaladas por MSI anunciado.
- Catálogo: [generated/dtm-catalog.json](generated/dtm-catalog.json).

## 3. Instanciação e ciclo de vida offline (`lasecfdt probe-all --init`) — Implementado e validado

Cada DTM em processo próprio: `CoCreateInstance → QI → IPersistXXX::InitNew → Environment → SetLanguage → GetInformation → InitNew(deviceType) → Config → GetFunctions → GetActiveXGuid → Save → PrepareToRelease → OnPreparedToRelease → Release`; depois, **em outro processo**, `Load → Environment → Config → Save` e comparação do data set.

| Resultado | DTMs |
|---|---|
| Ciclo completo + recarga em outro processo com **data set idêntico** | **24 / 24** com binário presente |
| Falha controlada (`0x80070002`, assembly .NET ausente) | `JosueLab.LasecHartCommDTM.CommDtm` em 23/11 (assembly fora do caminho registrado). Em 24/11, após a correção, ciclo completo OK e round-trip idêntico (§11) |

Persistência: 24 expõem `IPersistStreamInit`; 5 (`Smar.*DTM.1` e CodeWrights) expõem `IPersistPropertyBag`. Todos FDT **1.2** (nenhum implementa `IDtm2`). Relatórios: `docs/generated/probe/all/` (`*.json`, `*.reload.json`, `*.log`, `_summary.json`).

### Estabilidade com várias instâncias no mesmo processo

| DTM | 1 instância/processo | 2 simultâneas | Sequenciais (liberar e recriar) |
|---|---|---|---|
| LD301 (e demais `DTMCore`) | ok | ok | ok |
| SmarDF116CommunicationDTM | ok | **crash 0xC0000005** ao liberar | **crash** ao liberar a 2ª |
| SmarDF100DTM | ok | ok | **crash** no `CoCreateInstance` da 2ª |

Conclusão: arquitetura com **um processo por instância de DTM** (doc 06).

## 4. Persistência de projeto (`lasecfdt project`) — Implementado e validado

Projeto com LD301 (StreamInit, 7844 bytes), TT301 (PropertyBag, 1 propriedade) e CodeWrights HART (PropertyBag, 18 propriedades) criado em um processo e verificado em outro: **3/3 restaurados, data sets idênticos**. Arquivo: [generated/sample-project.json](generated/sample-project.json).

## 5. Interface gráfica dos DTMs — Implementado e validado (24 de 25)

Ferramenta principal: `lasecfdt ui-matrix` (caminho de produção: supervisor + um worker por DTM). Critério: GUID/ProgID do controle → criação e ativação → `IDtmActiveXControl::Init = TRUE` → controle renderizado (`PrintWindow`, cores distintas na área do controle) → `PrepareToRelease` → `OnApplicationClosed` → `IDtm::PrepareToRelease` → `OnPreparedToRelease` → worker encerra com código 0.

| Resultado | Qtde | DTMs |
|---|---|---|
| UI aberta, renderizada e fechada pelo protocolo; worker sai com 0 | **24** | toda a família `*DTM.DTMCore.1` (16), **`Smar.DT301DTM.1`, `LD400`, `TP301`, `TT301`**, **DF100**, **DF116**, **CodeWrights HART** (configuração), **LasecHartCommDTM** (controle .NET) |
| Instalação incompleta: falta `COMCT232.OCX` (Common Controls-2 do VB5) no sistema | 1 | RD500H — ⛔ bloqueado (reparar a instalação do DTM; não instalado por regra do projeto) |

Capturas: `docs/generated/ui/worker-matrix/*.png` e `_summary.json` (ex.: [TT301](generated/ui/worker-matrix/Smar.TT301DTM.1.png), [CodeWrights](generated/ui/worker-matrix/CWHartFdt.clsDTM.png)).

Evolução: 16/25 na primeira execução → 22/25 com as correções de hospedagem → 23/25 com o fallback por ProgID → 24/25 com o DTM do usuário corrigido (§11).

### Diagnóstico da família `Smar.*DTM.1` (e DF100/DF116)

Ferramenta: `NativeExceptionTrap` (tratador vetorado de exceções; decodifica exceções C++ do MSVC e `_com_error`), opção `--trap-native` do TestHost.

| Passo | Evidência | Conclusão |
|---|---|---|
| Estrutura do controle | DLL ATL/VC6 (`SmarTT301DTM.dll`, MSVCP60) que hospeda um OCX **VB6** (`PrjMainTT301DTM.ocx`, MSVBVM60, MSCOMCTL, TABCTL32) | Casca ATL 3.0 + UI VB6 |
| Ordem `Init` antes da ativação | `0xE06D7363 C++ _com_error HRESULT=0x80004003` (E_POINTER) | O ponteiro do controle VB6 interno ainda é nulo: ele só é criado na ativação |
| Ordem ativação antes do `Init` | `0xC0000005`, endereço **fora de qualquer módulo** (heap) | Execução de thunk ATL bloqueada por DEP: o executável .NET é NXCOMPAT, o que desliga a emulação de thunks ATL |
| Cópia do host sem NXCOMPAT + ordem ativação → `Init` | UI aberta, 1585 cores, fechada pelo protocolo | **Causa confirmada** |
| Encerramento | `ExitProcess` e `OleUninitialize`+`ExitProcess` → `0xC0000005`; `TerminateProcess` → 0 | Defeito na descarga das DLLs legadas; o host encerra com `TerminateProcess` após liberar tudo |
| Hipóteses anteriores | schemas de protocolo; canal de comunicação; DTM online | Refutadas (§7) |

Solução (doc 06 §3): executável sem NXCOMPAT + `SetProcessDEPPolicy(PROCESS_DEP_ENABLE)` (DEP **ativo**, com emulação de thunks ATL — verificado: `DEP=ativo, emulação de thunks ATL=ativa, permanente=True`), ordem §6.13.3 como padrão, `TerminateProcess` no encerramento.

### CodeWrights HART: UI pelo ProgID

`GetActiveXGuid` devolve vazio para todas as funções; `IDtm::InvokeFunctionRequest` devolve FALSE; o canal não tem `IFdtChannelActiveXInformation`. `GetActiveXProgId` devolve o ProgID (`CWHartFdt.ctrlConfiguration` etc.), registrado no sistema — o Frame resolve o CLSID e hospeda normalmente (§4.3.3.2). É nessa tela (interface, porta serial, mestre, preâmbulos, timeout) que se configura o modem real.

## 6. Compatibilidade entre arquiteturas — Parcial

- x86: validado (todos os DTMs).
- x64: **não validado** — não há DTM 64 bits instalado. O código de descoberta já trata a vista 64 e o redirecionamento `System32/Sysnative`.
- DTM .NET Framework: descoberta validada; instanciação não validada (assembly ausente).

## 7. Comunicação aninhada (Fase 2) — Implementado e validado com instrumento simulado

Ferramenta: `LasecFDT.TestHost --auto <DTM> --parent <sim|ProgID do Comm DTM> [--parent-prop Port=COM20] [--hart-sim CNCB0] --policy blocked|readonly|full`, mais os testes `CommTests`/`RealCommTests`.

| Cenário | Resultado |
|---|---|
| TT301 + canal simulado do Frame, `Blocked` | `SetCommunication` aceito; o TT301 chama `ConnectRequest` **dentro** de `SetCommunication`; o guarda recusa; nada chega ao canal |
| TT301 + canal simulado, `Full` | Conecta, `OnOnlineStateChanged(online=True)`; `PrepareToReleaseCommunication` → `OnPreparedToReleaseCommunication` → `ReleaseCommunication` → *configured* |
| 1ª tentativa: resposta com atributo `tag` | DTM reportou `LoadXMLDocument (ParseError) 0xC00CE015`; o schema exige **`fdt:tag`** (atributo de FDTDataTypesSchema). Corrigido e coberto por teste com MSXML |
| CodeWrights com `Port=COM0` (padrão do data set) | `SetCommunication(NULL)` → FALSE, "Error on connection to COM port because it is busy or invalid": **o Comm DTM abre a porta já ao virar raiz da cadeia** |
| **LD301 → CodeWrights (`Port=COM20`) → com0com → simulador de fio (`CNCB0`)**, `ReadOnly` | Topologia `ValidateAddChild/OnAddChild` ✔; UI do LD301 **aberta com canal**; o DTM conecta (cmd 0 curto) e lê cmds 0, 20, 13, 12 em quadro longo; tenta o **cmd 173** (específico Smar) → **recusado pelo guarda**; fechamento na ordem da spec, incluindo `OnRemoveChild`, exit 0 |
| TT301 pela mesma cadeia real | Online pela cadeia real (teste automatizado, 3/3 execuções estáveis). A UI, que falhava, abre após a correção de hospedagem (§5) — também com canal remoto (§8) |

### Problemas encontrados e resolvidos

| Problema | Causa | Correção |
|---|---|---|
| TestHost travava após `OnConnectResponse` | O LD301 chama `ConnectRequest` de uma **thread de trabalho**; objetos .NET são ágeis por padrão, então o guarda executava nessa thread e a resposta entrava em deadlock | `StandardOleMarshalObject` em `FrameContainer`, guarda, relay e simulador (doc 06 §5) |
| `System.IO.Ports.SerialPort` recusa `CNCB0` | Só aceita nomes `COMx` | `Win32SerialPort` (CreateFile/SetCommState) |
| Suíte travava | xUnit rodava classes em paralelo → várias instâncias de DTM simultâneas no processo | Paralelismo desligado |
| `COM20` "busy" após matar um processo travado | A CodeWrights usa servidores **fora do processo** (`CWFdtDrvMan.EXE`, `CWFdtHARTPort.exe`); o servidor de porta ficou órfão segurando a COM | Encerrado o processo órfão criado pelo teste. O supervisor agora faz isso sozinho (§8) |

## 8. Worker + supervisor (processo por instância) — Implementado e validado

| Cenário | Resultado |
|---|---|
| `lasecfdt ui-matrix` (25 DTMs, um worker cada) | 24/25 UIs; todos os workers que hospedaram UI saíram com código 0 (§5) |
| Persistência entre workers | LD301 salvo no worker A e recarregado no worker B: data set idêntico |
| Queda de worker (morto à força) | Chamada pendente falha com `WorkerDied`; evento `died`; os outros workers seguem atendendo |
| **Cadeia entre processos** (`lasecfdt chain`) | CodeWrights (worker A, `COM20`) → proxy COM do canal → LD301/TT301 (worker B) → instrumento simulado (`CNCB0`); LD301: UI aberta, cmds 0/20/13/12 lidos, cmd 173 recusado pelo guarda `ReadOnly`; desligamento e liberação pelo protocolo; exit 0 |
| Queda do worker do Comm DTM com a porta aberta | Ligação marcada quebrada; filho recebe `ReleaseCommunication` e continua íntegro (salva e libera); `CWFdtHARTPort.exe` órfão encerrado pelo supervisor |
| Estabilidade | Testes do worker repetidos 3× seguidas: 6/6 nas três; sem processos residuais |

### Problemas encontrados e resolvidos

| Problema | Causa | Correção |
|---|---|---|
| Worker não respondia | A janela oculta nunca fica visível, então `Load` não disparava e a leitura do pipe não começava | Handle criado e leitura iniciada no construtor |
| Worker travava ao abrir a UI do TT301 | Evento (`LockDataSet`) escrito no pipe **pela STA**; a escrita bloqueou, o DTM reentrou com `OnFunctionChanged` e a segunda escrita esperou a primeira (pilha capturada pelo cão de guarda) | Fila de escrita com thread dedicada (`JsonLineChannel`) |
| Captura "vazia" no worker | Worker em segundo plano não toma o primeiro plano; `CopyFromScreen` copiava outra janela | `PrintWindow(PW_RENDERFULLCONTENT)` |
| Exceção sem tratamento no processo de teste | Simulador de fio: escrita parcial quando o mestre some | Falhas de E/S registradas e o simulador continua |

## 9. Teste com hardware real — ⛔ Bloqueado (sem hardware)

`lasecfdt ports` (somente leitura; nenhuma porta aberta) nesta máquina:

| Porta | Dispositivo |
|---|---|
| COM20 ↔ CNCB0 | par virtual com0com (bancada de simulação) |
| COM13 | Bluetooth "Serial Padrão" ligado a **JBL Tune 720BT** (fone) |
| COM14 | Bluetooth "Serial Padrão" de entrada (sem dispositivo remoto) |

Não há modem HART (USB/serial) nem instrumento. O procedimento para quando houver está em [10-teste-com-hardware.md](10-teste-com-hardware.md).

## 10. Limitações destes testes

- Uma única máquina, DTMs de dois fabricantes (predomínio Smar).
- Comunicação testada apenas contra instrumento **simulado**; nenhum modem/instrumento real. Comandos específicos do fabricante não são simulados (resposta 64).
- `IDtmApplication`, `IFdtActiveX` (pedido pelo DTM) e `InvokeFunctionRequest` aceito não exercitados por falta de DTM que os use (`IDtm2` agora exercitado pelo LasecHartCommDTM).
- **HART-IP** validado só contra o simulador do próprio projeto (`HartIpSimulator`): o layout do cabeçalho e do início de sessão confere com o dissector do Wireshark, mas cliente e servidor foram escritos aqui; falta um gateway WirelessHART real.
- Métrica "cores distintas" indica renderização, não corretude funcional da tela.

## 11. Lasec HART Communication DTM (Comm DTM .NET do usuário) — Implementado e validado com instrumento simulado

Código: `C:\Source Local\LasecHartCommDTM` (repositório do usuário). Reescrito em 24/11/2026 como Comm DTM FDT 1.2 equivalente ao "HART Communication" da CodeWrights, com HART-IP para acesso sem fio (gateways WirelessHART). Registrado nesta máquina com `RegAsm` de 32 bits (`/codebase`, a partir de `src\LasecHartCommDTM\bin\Release\net48`).
et48`).

| Teste | Resultado |
|---|---|
| `probe --init` | QI: `IDtm`, `IDtm2`, `IDtmInformation`, `IDtmChannel`, `IDtmParameter`, `IDtmActiveXInformation`, `IFdtEvents`, `IPersistPropertyBag`, `IPersistStreamInit`; ciclo completo; `OnPreparedToRelease`; round-trip do data set idêntico (20 propriedades) |
| Schemas XDR | `GetInformation`, `GetFunctions`, `GetParameters`, `GetChannelParameters`, `GetSupportedProtocols`, respostas de conexão/transação/erro e varredura (`DTMTopologyScanSchema`): todos válidos |
| `chain` serial (COM20 → com0com → CNCB0) | LD301 e TT301 online; UI do LD301 aberta; cmds 0 (curto), 0/20/13/12 (longo) respondidos; 173 recusado pelo guarda `ReadOnly`; desligado e liberado pelo protocolo |
| `chain --hartip-sim udp` e `tcp` | LD301 online por HART-IP; 9 PDUs; sessão aberta e fechada; desligado e liberado |
| Gateway WirelessHART simulado (teste automatizado) | sub-dispositivo pelo endereço longo com resposta atrasada (33 → reenvio → 0); varredura com gateway + 2 sub-dispositivos (cmds 74/84) |
| UI (`ui-matrix --only`, funções 1/10/20/100) | abre, renderiza, `OnApplicationClosed`, libera, worker sai com 0 |
| `HartEngine.Tests` (no repositório do DTM) | 11/11: quadros, parser com lixo/eco/burst/fragmentos, checksum, casamento de resposta, packed ASCII, cmd 84, propriedades, HART-IP UDP com troca de porta e resposta atrasada, servidor ausente |

### Problemas encontrados e resolvidos

| Problema | Causa | Correção |
|---|---|---|
| DTM não carregava (`0x80070002`) | Registro apontava para `C:\SourceCode\…`, pasta inexistente | Re-registro a partir do repositório atual |
| Interfaces "FDT" sem efeito | O código implementava interfaces próprias (`ICommChannel`, `IDtm` caseiro) e métodos FDT vazios; `GetInformation` não seguia o DTMInformationSchema; sem canal, sem persistência, sem UI | Reescrita sobre o interop oficial `Jigfdt.fdt100` |
| `InvalidCastException` em `IPersistPropertyBag` | Frame .NET recebe o objeto gerenciado; interfaces OLE de cada lado são tipos .NET distintos | Chamadas pela vtable dos dois lados (doc 06 §3) |
| LD301 não ficava online | O DTM Smar exige `release`/`build` em `FDTVersion` do `parentInformation` de `OnConnectResponse2` | `release="0" build="0"` |
| Desligamento levava 20 s e falhava | O LD301 espera `OnDisconnectResponse` **dentro** de `PrepareToReleaseCommunication` sem atender chamadas COM de fora; a CodeWrights responde de forma síncrona, antes de retornar de `DisconnectRequest` (medido) | Mesmo comportamento da CodeWrights |
| UI .NET: `ArgumentException` ao fechar | `AxHost` chama `ReleaseComObject` no controle | Controle .NET hospedado direto (doc 06 §3) |
| Caminho do canal `HART` | §4.5.1.2 exige `<systemTag>/<id>` | `<systemTag>/HART` |

## 12. Interface Flutter e Host por estação (thin clients) — Implementado e validado na máquina local

Arquitetura e decisões: [doc 11](11-ui-thinclient.md). Instrumento sempre simulado (`lasecfdt sim --serial CNCB0`, par com0com).

| Cenário | Como | Resultado |
|---|---|---|
| Fluxo completo pela UI | `lasecfdt_ui.exe` (build release) pilotado por cliques reais e capturas de tela | Projeto "Caldeira 2" criado; LasecHartCommDTM adicionado; **COM20 escolhida na tela do próprio DTM** (encaixada na UI) e aplicada; LD301 adicionado sob o modem; conexão somente leitura → online; tela ActiveX do LD301 encaixada em aba ao lado da tela do Comm DTM ("Connected"); tema claro/escuro; salvar |
| Encerramento | Fechar a janela com DTM conectado e duas telas abertas | 2 s; desconexão e liberação pelo protocolo; nenhum processo restante; `Locks\` vazio; auditoria com project.new, node.add, node.connect, project.save |
| Reabrir | Reabrir o projeto na UI e conectar | Nós sem processo até o uso; Comm DTM carregado do data set com COM20; LD301 online |
| Segunda estação | Host com `LASECFDT_STATION=TC-02` na mesma planta, com a UI aberta e conectada | Projeto da UI aberto somente leitura (salvar recusado); catálogo do cache gerado pela outra estação; **"A porta COM20 está em uso pela estação NOTE-CASA-JO (…)"**; arrendamentos das duas estações visíveis |
| Pipes entre o Dart e o Host | `HostClient` (Dart) com várias chamadas seguidas | Corrigido: o Dart cria os pipes do filho em modo overlapped; o console do .NET Framework os lê com `ReadFile` síncrono e perdia a 2ª mensagem. O Host abre stdin/stdout como `FileStream` assíncrono (cai para o síncrono com pipes anônimos) |
| Regressão das telas | `lasecfdt ui-matrix` depois da mudança da janela do worker | 24 de 25 (igual; RD500H sem `COMCT232.OCX`) |
| Suíte | `dotnet test` | 49 de 49 |

Não validado: servidor de terminal real com 8 sessões e thin clients (as duas estações dos testes estavam na mesma sessão do Windows), porta COM redirecionada por RDP.

### 12.1 Busca de instrumentos e parâmetros pela UI

| Cenário | Resultado |
|---|---|
| "Buscar instrumentos" no Modem HART COM20 | Primeira vez: 23 DTMs descritos, com progresso na tela; o instrumento simulado TT-201 (endereço 0, 3E02000001, Smar tipo 2) é sugerido como TT301 "mesmo fabricante e tipo"; adicionado e conectado |
| "Comparar com o instrumento" no TT-201 (somente leitura) | 4 diferenças de 78 (tag, descritor, data, mensagem); aviso de que o DTM informou falha e de que o comando 162 foi recusado pela política; "o projeto foi restaurado" |
| Simulador | Passou a aceitar as escritas universais 17 (mensagem) e 18 (tag, descritor, data), guardando e devolvendo os dados como um instrumento real |
| Suíte | 52 de 52 |

