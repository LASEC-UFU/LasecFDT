# LasecFDT

Frame Application FDT independente, para substituir o PACTware: **motor FDT 1.2/1.2.1** e **interface moderna** (Flutter, temas claro e escuro no padrão do Gestor-UFU), feita para rodar num **servidor de terminal com várias estações (thin clients)**. Sem PACTware, ele:
- descobre, instancia, configura e persiste DTMs reais;
- abre a interface original deles (24 dos 25 instalados), encaixada em abas na UI;
- monta a **comunicação aninhada** (Device DTM → canal de Comm DTM);
- separa as estações do servidor (processos, telas, preferências, logs) e coordena o que elas compartilham (projetos, catálogo, portas, auditoria).

Cada estação tem a sua UI e o seu `LasecFDT.Host`; cada instância de DTM roda no seu próprio processo (worker). Arquitetura: [doc 11](docs/11-ui-thinclient.md).

## Estrutura

| Caminho | Conteúdo |
|---|---|
| `docs/01…11-*.md` | Estudo, arquitetura, plano, testes, lacunas, procedimento com hardware e UI/thin clients (11). Comece pelo [08](docs/08-testes-validacao.md) e pelo [09](docs/09-lacunas-tecnicas.md) |
| `docs/generated/` | Saídas reais: catálogo de interfaces da type library, catálogo de DTMs, relatórios de sonda, capturas de UI (`ui/worker-matrix`) |
| `src/LasecFDT.Core` | Modelos, JSON e contratos JSON-RPC (`Rpc/`) (netstandard2.0) |
| `src/LasecFDT.Fdt1` | Motor FDT 1.x (net48 x86): descoberta, `FrameContainer` (com topologia), `DtmSession`, persistência, host ActiveX (DTM, canal, ProgID), diagnóstico de exceções nativas |
| `src/LasecFDT.Fdt1/Comm` | Comunicação: `CommunicationGuard` (políticas Blocked/ReadOnly/Full), canal HART simulado, simulador HART de fio (com0com) |
| `src/LasecFDT.Worker.Fdt1` | Worker: um processo por instância de DTM (sem NXCOMPAT, DEP com emulação de thunks ATL) |
| `src/LasecFDT.Supervisor` | Supervisor dos workers: timeouts, quedas, comunicação entre processos (proxy COM do canal), limpeza de servidores de porta órfãos |
| `src/LasecFDT.Host` | Backend de uma estação (filho da UI, stdin/stdout): projeto e topologia, workers sob demanda, catálogo e arrendamentos da planta, auditoria |
| `ui/lasecfdt_app` | UI Flutter (Windows): projeto, topologia, telas dos DTMs em abas, catálogo, eventos, estação/planta |
| `src/LasecFDT.Cli` | `lasecfdt.exe`, ferramentas de linha de comando |
| `src/LasecFDT.TestHost` | Aplicação de diagnóstico (WinForms, um processo) |
| `tests/LasecFDT.Tests` | xUnit: unidade + integração com DTMs instalados e workers reais (52 testes) |
| `lib/fdt-jig` | `FDT100.dll` e `Jigfdt.fdt100.dll` oficiais (redistribuíveis) |
| `specs/` | Especificações extraídas. **Uso local, não redistribuir** |

## Compilar e testar

```powershell
dotnet build LasecFDT.sln
dotnet test tests\LasecFDT.Tests
cd ui\lasecfdt_app; flutter build windows --release     # UI: build\windows\x64\runner\Release\lasecfdt_ui.exe
```

A UI acha o Host em `engine\` ao lado dela (instalação), em `LASECFDT_HOST`, ou na saída de compilação da solução (desenvolvimento). Pasta comum da planta: `%ProgramData%\LasecFDT` (ou `LASECFDT_PLANT`). Para testar a UI sem hardware, deixe um instrumento simulado atendendo na outra ponta do par virtual:

```powershell
src\LasecFDT.Cli\bin\Debug\net48\lasecfdt.exe sim --serial CNCB0      # o Comm DTM usa COM20
```

## Comandos

```powershell
$cli = "src\LasecFDT.Cli\bin\Debug\net48\lasecfdt.exe"
& $cli discover --out catalog.json                       # catálogo de DTMs (somente leitura)
& $cli ui SmarLD301DTM.DTMCore.1                         # UI do DTM num worker isolado (feche a janela para encerrar)
& $cli ui CWHartFdt.clsDTM                               # tela de configuração do Comm DTM HART (porta, mestre...)
& $cli ui-matrix --outdir ui-matrix                      # abre/captura/fecha a UI de todos os DTMs (um worker cada)
& $cli ui-matrix --only <ProgID> --function 1 --outdir x # só um DTM / uma função
& $cli sim --serial CNCB0                                # instrumento HART simulado (Ctrl+C encerra); --hartip udp[:porta] p/ gateway
& $cli params Smar.TT301DTM.1                            # parâmetros offline do DTM (IDtmParameter::GetParameters)
& $cli ports                                             # portas seriais e dispositivos (sem abrir nenhuma)
& $cli probe SmarLD301DTM.DTMCore.1 --init --trace       # ciclo de vida offline de um DTM (no próprio processo)
& $cli probe-all --init --outdir probe                   # todos os DTMs, cada um em processo isolado
& $cli project new p.json "Meu projeto"
& $cli project add p.json SmarLD301DTM.DTMCore.1 --name PT-101
& $cli project verify p.json                             # restaura em novo processo e compara
& $cli typelib lib\fdt-jig\FDT100.dll --out fdt100        # catálogo de interfaces
```

### Comunicação (sempre contra instrumento simulado nos exemplos)

```powershell
# Canal HART simulado pelo próprio Frame (sem hardware)
& $cli chain Smar.TT301DTM.1 --parent sim --policy full
# Comm DTM real da CodeWrights num worker (porta virtual COM20) e o DTM do instrumento em outro;
# instrumento simulado na outra ponta do par com0com (CNCB0)
& $cli chain SmarLD301DTM.DTMCore.1 --parent CWHartFdt.clsDTM --parent-prop Port=COM20 --hart-sim CNCB0 --policy readonly --ui
# O mesmo com o Comm DTM do usuário (LasecHartCommDTM): modem serial na porta virtual, ou HART-IP local (sem fio)
& $cli chain SmarLD301DTM.DTMCore.1 --parent JosueLab.LasecHartCommDTM.CommDtm --parent-prop Port=COM20 --hart-sim CNCB0 --policy readonly --ui
& $cli chain SmarLD301DTM.DTMCore.1 --parent JosueLab.LasecHartCommDTM.CommDtm --hartip-sim udp --policy readonly
```

`LASECFDT_LOGDIR=<pasta>` grava o log de cada worker (inclui a pilha da STA quando uma chamada demora).

Políticas do guarda de comunicação:
- `blocked` (padrão): nada chega ao canal.
- `readonly`: só passam comandos HART de leitura conhecidos.
- `full`: tudo passa. **Só com autorização explícita** quando houver instrumento real. Com um Comm DTM real e sem simulador, exige `--confirm-full`.

Para o teste com modem e instrumento reais, veja o [doc 10](docs/10-teste-com-hardware.md).
