# 07 — Plano de implementação

Legenda de estado (critério do doc 08): ✅ implementado e validado · 🟡 implementado, não validado · ⬜ planejado · ⛔ bloqueado.

## Fase 0 — Fundamentos (concluída nesta execução)

| Item | Estado |
|---|---|
| Inventário e extração da documentação; catálogo de interfaces gerado da type library | ✅ |
| Descoberta de DTMs (registro 32/64 + `ICatInformation`), catálogo JSON | ✅ |
| `FrameContainer` com as 9 interfaces obrigatórias do Frame + 3 opcionais | ✅ (exercitado por DTMs reais) |
| Máquina de estados do DTM, sessão em STA, rastro de chamadas | ✅ |
| Persistência `IPersistStreamInit` e `IPersistPropertyBag`, projeto JSON | ✅ (round-trip entre processos) |
| UI ActiveX (abrir/fechar pelo protocolo) | ✅ em 23 de 25 DTMs (doc 08 §5) |
| `IDtm2::Environment2`, `IDtmApplication` | 🟡 (nenhum DTM instalado usa) |

## Fase 1 — Motor de produção (núcleo concluído)

| Item | Estado |
|---|---|
| Worker FDT 1.x (`LasecFDT.Worker.Fdt1.exe`, net48 x86): JSON-RPC por named pipe — create, load, save, getFunctions, openUi, closeUi, release, comunicação, eventos | ✅ |
| Supervisor: um worker por instância, timeout por chamada (mata worker preso), detecção de queda, limpeza de servidores de porta órfãos | ✅ |
| Hospedagem de controles ATL 3.0/VB6 (sem NXCOMPAT + DEP com emulação de thunks, ordem §6.13.3, encerramento sem descarga de DLLs) | ✅ (família `Smar.*DTM.1`, DF100, DF116) |
| UI por ProgID (§4.3.3.2), UI de canal (§4.5.2/§4.6), `InvokeFunctionRequest` (§4.3.1.5) | ✅ ProgID (CodeWrights); 🟡 canal e InvokeFunction (sem DTM que os aceite) |
| Ferramentas: `lasecfdt ui`, `ui-matrix`, `chain`, `ports` | ✅ |
| Base de políticas por DTM (`dtm-quirks.json`) | ⬜ (hoje as regras medidas valem para todos) |
| Catálogo persistente com `DtmDeviceType` e atualização por `ModificationFlag` | ✅ cache da planta, compartilhado entre estações (doc 11) |
| `IFdtActiveX::OpenActiveXControlRequest` (DTM abrindo telas extras) | 🟡 evento emitido; decisão de UX na Fase 4 |
| Executável de serviço do supervisor para a UI | ✅ `LasecFDT.Host` (um por estação; doc 11) |

## Fase 2 — Comunicação (núcleo concluído nesta execução; autorizada pelo usuário)

| Item | Estado |
|---|---|
| Topologia real (`IFdtTopology` pais/filhos; `IFdtChannelSubTopology::ValidateAddChild/OnAddChild/OnRemoveChild`) | ✅ (CodeWrights real) |
| Comunicação aninhada no mesmo processo (`SetCommunication`, `PrepareToReleaseCommunication`, `ReleaseCommunication`, ordem de liberação) | ✅ |
| Guarda de comunicação com políticas `Blocked` / `ReadOnly` / `Full` e auditoria de XML | ✅ |
| Canal HART simulado pelo Frame; simulador HART no nível do fio (com0com) | ✅ |
| Validação XDR de todo XML emitido (MSXML 3) | ✅ |
| Canal entre workers (Device DTM e Comm DTM em processos diferentes): proxy COM do canal (OBJREF) | ✅ |
| Configurar o Comm DTM pela tela do fabricante (CodeWrights: porta, mestre, preâmbulos) | ✅ (UI pelo ProgID); também por propriedades do data set |
| Escrita/download em instrumento | ⬜ só com autorização explícita; testar antes com o simulador (`Full`) |
| Teste com modem e instrumento reais | ⛔ sem hardware nesta máquina; procedimento em [doc 10](10-teste-com-hardware.md) |
| Família `Smar.*DTM.1`: UI | ✅ (causa era hospedagem ActiveX/DEP, não comunicação) |

## Fase 3 — Acesso a dados sem UI

`IDtmParameter`, `IDtmSingleInstanceDataAccess` (offline) e `IDtmSingleDeviceDataAccess` (online) → pesquisa de parâmetros, comparação, relatórios. `IDtmDocumentation` (XML+XSL) para impressão.

## Fase 4 — Interface Flutter e thin clients (núcleo concluído — doc 11)

| Item | Estado |
|---|---|
| `LasecFDT.Host`: backend por estação (stdin/stdout), workspace com topologia, workers sob demanda, descarregamento de ociosos, teto por estação | ✅ |
| Planta compartilhada sem serviço: projetos, catálogo, arrendamentos de porta/projeto, auditoria, preferências por estação | ✅ (duas estações no mesmo servidor, teste automatizado) |
| UI Flutter (`ui/lasecfdt_app`): tema claro/escuro do Gestor-UFU, projeto, topologia, catálogo, eventos, estação/planta | ✅ (pilotada por cliques reais contra instrumento simulado) |
| Telas dos DTMs encaixadas em abas (janela dona + `SetWindowPos` assíncrono), escondidas sob diálogos | ✅ (LasecHartCommDTM .NET e Smar LD301 ActiveX) |
| Busca de instrumentos (*topology scan*) com atribuição de DTM | ✅ (doc 11 §10) |
| Parâmetros pelo Frame: ver, comparar projeto × instrumento, ler (com restaurar), gravar com confirmação | ✅ mecanismo (doc 11 §11); sucesso completo só com instrumento real |
| Níveis de usuário; diálogos/telas extras pedidos pelo DTM | ⬜ |
| Validação num servidor de terminal real com 8 sessões | ⛔ sem servidor RDS nesta máquina |

## Fase 5 — Instalador

MSI com os merge modules oficiais (`FDT_100.msm`, `FDT_XMLSchemata.msm`, `FDT_InteropAssembly.msm`) conforme §7.1.4; schemas por protocolo em subpastas `<protocolId>\` (§6.4).

## Fase 6 — FDT 2.x

Worker net48 com o Frame FDT 2.x. ⛔ Depende de licença dos Common Components / especificação FDT 2.x.

## Riscos

| Risco | Mitigação |
|---|---|
| DTMs com defeitos que derrubam o processo | Processo por instância (validado) |
| DTMs que dependem de comportamentos específicos do PACTware | Base de políticas; testes A/B com várias ordens; rastro completo |
| Schemas de protocolo ausentes/antigos | Obter anexos oficiais; implementar §6.4 |
| Licença da especificação | Não redistribuir `specs/`; documentação própria cita seções |
