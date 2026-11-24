# 09 — Lacunas técnicas

## 1. Documentação

| Lacuna | Por que importa | Como obter |
|---|---|---|
| **Anexo HART para FDT 1.2.1** (schemas `FDTHARTCommunicationSchema`, `FDTHARTChannelParameterSchema`, ident/scan) | Comunicação HART aninhada; os schemas da pasta são da 1.2 e diferem dos atualmente instalados no PC | FDT Group / FieldComm Group (gratuito para membros/registrados; verificar licença) |
| Anexos FF, IO-Link, Modbus, PROFINET | Outros protocolos | FDT Group |
| Especificação **FDT 2.x** (IEC 62453-42) e Common Components | Host FDT 2.x | FDT Group / IEC (pago) — não comprar sem decisão |
| FDT 3.x | DTMs web | FDT Group |
| Guia de certificação / dtmINSPECTOR | Conformidade formal | M&M / FDT Group |
| Tabelas de estado e §4.10.1 em texto | Extraídas de imagem do PDF; podem ter erro de leitura de coluna | Conferir no PDF original (págs. 43–45, 157–158) |

## 2. Técnicas (o que falta para compatibilidade real)

| Lacuna | Situação | Próximo passo |
|---|---|---|
| Teste com **modem HART e instrumento reais** | ⛔ Sem hardware nesta máquina (`lasecfdt ports`: só com0com e Bluetooth de áudio) | Procedimento pronto: [10-teste-com-hardware.md](10-teste-com-hardware.md) |
| RD500H: UI não abre | Instalação incompleta: falta `COMCT232.OCX` (Common Controls-2 do VB5) | Reparar/reinstalar o DTM RD500H pelo instalador da Smar (não instalado aqui por regra do projeto) |
| Escrita/download em instrumento | Código pronto (`CommPolicy.Full`); bloqueado por padrão; `lasecfdt chain` exige `--confirm-full` em linha real | Validar primeiro contra o simulador; em campo só com autorização explícita |
| Comandos HART: lista de leitura do guarda `ReadOnly` | Baseada no conjunto público de comandos HART; a especificação HART não está na documentação | Conferir com a especificação HART (FieldComm Group) antes de uso em campo |
| Comandos específicos do fabricante no simulador | Respondem 64 (não implementado) | Gravar respostas reais de um instrumento (com autorização) e reproduzi-las em `HartDevice.FixedResponses` |
| `IFdtActiveX::OpenActiveXControlRequest` (DTM pedindo telas extras) | Worker emite evento `ui/openRequested` e responde FALSE | Decisão de UX da Fase 4 (Flutter) |
| UI de canal (`IFdtChannelActiveXInformation`) | Implementada; nenhum DTM instalado a expõe (a CodeWrights usa ProgID no próprio DTM) | Validar quando houver DTM com UI de canal |
| `IDtm::InvokeFunctionRequest` aceito | Implementado; único DTM testado (CodeWrights) recusa | Validar com DTM que tenha funções sem ActiveX |
| §6.4: coleta de schemas de protocolo informados pelos DTMs (`DtmSchemaPaths`) em subpastas `<protocolId>\` | Não implementada (nenhum DTM instalado os informa) | Quando houver DTM que os informe |
| `IDtm2` / DTMs 1.2.1 | `IDtm2::Environment2`/`SetSystemGuiLabel` exercitados com o LasecHartCommDTM; demais interfaces 1.2.1 sem DTM para validar | Obter DTM 1.2.1 de demonstração (verificar procedência) |
| DTMs 64 bits | Sem amostras | Worker x64: mesmo código com `PlatformTarget=x64` (o supervisor escolhe pelo catálogo) |
| DTM .NET Framework | ✅ `LasecHartCommDTM` (do usuário) hospedado e usado como Comm DTM na cadeia, serial e HART-IP (doc 08 §11) | — |
| HART-IP / WirelessHART contra equipamento real | Cliente e simulador escritos no projeto; cabeçalho e início de sessão conferidos com o dissector do Wireshark; valores de tipo/id de mensagem e o HART-IP v2 (TLS) não conferidos com a especificação | Testar com um gateway WirelessHART real (somente leitura primeiro, doc 10) e conferir com a especificação HART-IP da FieldComm |
| Modem HART real com o LasecHartCommDTM | RTS por software, DTR, paridade e tempos implementados; sem modem para medir | Doc 10, etapa 3, com `--parent JosueLab.LasecHartCommDTM.CommDtm` |
| Multiusuário / bloqueio real | Entre estações: projeto em edição fica somente leitura nas outras e porta em uso é recusada (arrendamentos, doc 11). Dentro do projeto, `LockDataSet` continua sempre concedido (uma estação por projeto) | Bloqueio por nó, se várias estações precisarem editar o mesmo projeto ao mesmo tempo |
| Serviço do supervisor para a UI Flutter | ✅ `LasecFDT.Host` (um por estação, stdin/stdout); doc 11 | — |
| Servidor de terminal real (8 sessões, thin clients, porta COM redirecionada por RDP) | Arquitetura implementada e testada com duas estações na mesma máquina (`LASECFDT_STATION`); chave de porta redirecionada (`\Device\RdpDr…`) não exercitada | Validar num servidor RDS com thin clients; medir memória e CPU com 8 sessões |
| Busca de instrumentos (*topology scan*) | ✅ Host + UI (doc 11 §10). O endereço encontrado não é escrito no DTM do instrumento (padrão 0) | Escrever `BusInformation/SlaveAddress` por `IDtmParameter::SetParameters` para *multidrop*, conferindo antes com um DTM real |
| Leitura/gravação completas de parâmetros pelo Frame | Mecanismo validado (doc 11 §11); o sucesso completo não foi visto porque os DTMs Smar usam comandos do fabricante que o simulador não implementa | Validar com instrumento real: ler primeiro (somente leitura e depois completo), gravar só com autorização |
| Pasta da planta com permissão para todas as contas | Sem permissão, cada estação cai no modo isolado (aviso na UI) | Instalador: `%ProgramData%\LasecFDT` com Modificar para Usuários (Fase 5) |
| Instalador MSI com merge modules | Planejado | Fase 5 |

## 3. Respostas objetivas às perguntas de sucesso

**1. Temos toda a documentação necessária para um Frame FDT 1.2.1 independente?**
Para o **núcleo offline** (descoberta, instanciação, persistência, UI ActiveX): **sim** — e foi demonstrado. Para **comunicação**, falta o **anexo HART 1.2.1** (e anexos dos demais protocolos). Para FDT 2.x/3.x, falta toda a especificação e os componentes.

**2. Conseguimos implementar um host FDT 1.2.1 sem SDK comercial?**
**Sim.** A type library e o interop assembly oficiais (redistribuíveis) bastam; o LasecFDT hospeda 25 DTMs reais (um deles em .NET) sem PACTware e sem SDK comercial.

**3. Existe código aberto reutilizável?** Não para o Frame (doc 05). Apenas o material oficial do FDT-JIG.

**4. Interfaces obrigatórias para abrir um DTM offline e sequência real:** no DTM, `IPersistStreamInit` ou `IPersistPropertyBag`, `IDtm` (ou `IDtm2`), `IDtmInformation`, `IDtmActiveXInformation`; no controle, `IDtmActiveXControl`; no Frame, o objeto com `IFdtContainer` + `IDtmEvents` (no mínimo `OnPreparedToRelease`/`OnApplicationClosed`), e as demais obrigatórias da §4.10.1 (os DTMs as consultam por QI). Sequência: doc 04 §3–7.

**5. Interfaces para comunicação com instrumentos** (implementadas e validadas com instrumento simulado, doc 08 §7): Device DTM recebe `IFdtCommunication` via `IDtm::SetCommunication` e implementa `IFdtCommunicationEvents(2)`; Communication/Gateway DTM fornece `IDtmChannel` → `IFdtChannel` + `IFdtCommunication` + `IFdtChannelSubTopology(2)` + `IFdtChannelScan`; o Frame fornece topologia (`IFdtTopology`), valida a ligação canal↔filho, e controla `SetCommunication`/`PrepareToReleaseCommunication`/`ReleaseCommunication`. Online: `IDtmOnlineParameter`, `IDtmOnlineDiagnosis`, `IDtmSingleDeviceDataAccess`.

**6. DTMs 32 e 64 bits?** Sim, com **processos separados por arquitetura** (DTM in-process só carrega em processo da mesma arquitetura). Validado em x86 (worker x86); x64 sem amostra.

**7. Integração com Flutter:** UI Flutter ↔ supervisor por JSON-RPC em named pipe ↔ workers x86 net48 (um por instância de DTM). Worker e supervisor **implementados e validados**; a UI do DTM já abre em janela própria do worker com dono configurável (`OpenUiParams.OwnerHwnd`), pronta para ficar "sobre" a janela Flutter. Falta só o executável de serviço que expõe o supervisor à UI. Doc 06 §3–4.

**8. Menor conjunto para abrir um DTM real:** descoberta por categoria → STA x86 net48 → `CoCreateInstance` → `IPersistXXX::InitNew` → `IDtm::Environment(tag, frame)` → `IDtmInformation::GetInformation` → `IDtm::InitNew(deviceType)` → `IDtm::Config(userInfo)` → `GetFunctions` → `GetActiveXGuid` → criar controle + `IDtmActiveXControl::Init` → hospedar em `AxHost`; Frame com `IFdtContainer.GetXmlSchemaPath` (schemas copiados) e `IDtmEvents`. Tudo isso está em `LasecFDT.Fdt1` (~2.200 linhas, incluindo descoberta e ferramentas de type library).

**Acréscimo medido (controles legados):** para DTMs com UI em ATL 3.0/VB6 (a maioria dos instalados), o executável que hospeda o controle precisa estar **sem NXCOMPAT** (DEP religado com emulação de thunks ATL), a ordem deve ser **ativar o controle → `IDtmActiveXControl::Init`** (§6.13.3), o CLSID pode vir de `GetActiveXProgId` quando `GetActiveXGuid` vem vazio, e o processo deve encerrar sem a descarga das DLLs legadas. Ver doc 06 §3 e doc 08 §5.
