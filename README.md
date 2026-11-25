# LasecFDT

Frame Application FDT independente, para substituir o PACTware: **motor FDT 1.2/1.2.1** e **interface moderna** (Flutter, temas claro e escuro no padrão do Gestor-UFU), feita para rodar num **servidor de terminal com várias estações (thin clients)**. Sem PACTware, ele:
- descobre, instancia, configura e persiste DTMs reais;
- abre a interface original deles (24 dos 25 instalados), encaixada em abas na UI;
- monta a **comunicação aninhada** (Device DTM → canal de Comm DTM);
- separa as estações do servidor (processos, telas, preferências, logs) e coordena o que elas compartilham (projetos, catálogo, portas, auditoria).

Cada estação tem a sua UI e o seu `LasecFDT.Host`; cada instância de DTM roda no seu próprio processo (worker). Arquitetura: [doc 11](docs/11-ui-thinclient.md).

## ⚠️ Antes de usar: schemas HART

**Instalador:** [Releases](https://github.com/LASEC-UFU/LasecFDT/releases) → `LasecFDT-<versão>.msi`.

O LasecFDT **não pode distribuir os schemas XML do protocolo HART da FDT**, por causa da licença do FDT Group. Sem eles, os DTMs HART abrem e dizem que conectaram, mas **não trocam dados com o instrumento**. Escolha uma das opções:

| Opção | O que fazer |
|---|---|
| **1. PACTware no mesmo computador** (mais simples) | Instale o PACTware (gratuito, [www.pactware.com](http://www.pactware.com)). O LasecFDT encontra sozinho os schemas que ele instala. Não é preciso fazer mais nada. |
| **2. Especificação FDT da PROFIBUS** | Baixe a *FDT Interface Specification* (FDT 1.2, PNO 2.162) em <https://www.profibus.com/download/fdt-interface-specification>. O site pede cadastro. Na lista de downloads, baixe **`FDTSpecV12+Addendum(2162_V12+2162_Add).zip`** (o `FDTSpecV121+Addendum.zip` não serve: não traz os schemas HART). **Não precisa descompactar:** copie o .zip do jeito que veio (ou só os `.xml` da pasta `XML-Files` dele) para **`C:\ProgramData\LasecFDT\Schemas\`**. Essa pasta é criada pelo instalador e tem um `LEIA-ME.txt`. Feche e abra o LasecFDT. |
| **3. Pasta própria** (avançado) | Defina a variável de ambiente `LASECFDT_SCHEMA_PATH` com uma pasta completa de schemas (os base e os de protocolo, todos na raiz). Ela substitui as opções acima. |

A pasta `C:\ProgramData\LasecFDT\Schemas\` vale para todas as estações do servidor, aceita subpastas e arquivos `.zip`, e a desinstalação não a apaga. Detalhes: [doc 11 §8](docs/11-ui-thinclient.md).

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
| `tests/LasecFDT.Tests` | xUnit: unidade + integração com DTMs instalados e workers reais (55 testes) |
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

## Instalador e Release

- **Instalar:** baixe `LasecFDT-<versão>.msi` em *Releases* no GitHub e execute como administrador. No servidor de terminal, uma vez vale para todas as estações. Detalhes no [doc 11 §8](docs/11-ui-thinclient.md).
- **Atualizar:** ao abrir, o LasecFDT instalado consulta o GitHub e, se houver versão nova, pergunta se pode atualizar (baixa, fecha, instala e reabre). Num servidor, só atualiza com as outras estações fechadas. Os schemas HART de uma instalação de uso interno são copiados para `C:\ProgramData\LasecFDT\Schemas\` antes da troca. [doc 11 §13](docs/11-ui-thinclient.md).
- **Publicar uma versão:** GitHub → Actions → **Release LasecFDT** → *Run workflow* → versão (ex.: `v0.1.0`). O workflow compila, roda os testes que não dependem de hardware, gera o MSI e o zip portátil, cria a tag e publica o Release.
- **Gerar localmente:** `tools\release\build-release.ps1 -Version 0.1.0 -WixDir "<WiX 3.14>\bin"`. Saída em `artifacts\`.

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
