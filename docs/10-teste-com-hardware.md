# 10 — Procedimento de teste com modem HART e instrumento reais

Estado: ⛔ **bloqueado por falta de hardware** nesta máquina (doc 08 §9). Tudo o que o procedimento usa já está implementado e foi validado contra o instrumento simulado (doc 08 §7–8).

## Pré-requisitos

| Item | Observação |
|---|---|
| Modem HART (USB ou serial) com driver instalado | Deve aparecer em `lasecfdt ports` como **USB (candidato a modem HART)** ou porta física |
| Instrumento HART **de bancada** | Fonte 24 V e resistor de carga de 250 Ω no laço. Evite instrumento em processo: escrita altera o processo |
| DTM do instrumento instalado | Conferir `manufacturerId`/`deviceTypeId` do DtmDeviceType (`lasecfdt probe <DTM> --init`) com o instrumento |
| Autorização explícita para escrita | Só necessária para a etapa 4 |

## Etapas

1. **Identificar a porta, sem abri-la.**
   ```powershell
   lasecfdt ports
   ```

2. **Conferir a configuração do Comm DTM pela tela do fabricante.** Isso é opcional. A tela mostra interface, porta, mestre, preâmbulos e timeout. Hoje a configuração escolhida nessa tela não é gravada em projeto; isso fica para a Fase 4. Para o teste, use a etapa 3.
   ```powershell
   lasecfdt ui CWHartFdt.clsDTM
   ```

3. **Primeira conexão, somente leitura.**
   ```powershell
   lasecfdt chain <ProgID do DTM do instrumento> --parent CWHartFdt.clsDTM --parent-prop Port=COMx --policy readonly --ui --hold 60
   ```
   O guarda `readonly` repassa apenas comandos HART de leitura conhecidos. Tudo o que for recusado aparece em "Tráfego auditado pelo guarda". Resultado esperado:
   - "Filho online: True";
   - comandos 0, 13 e 12 (ou outros de leitura) respondidos;
   - comandos específicos do fabricante recusados. Algumas telas online podem ficar incompletas por causa disso.

   O mesmo teste com o **Comm DTM do usuário** no lugar da CodeWrights:
   ```powershell
   lasecfdt chain <DTM do instrumento> --parent JosueLab.LasecHartCommDTM.CommDtm --parent-prop Port=COMx --policy readonly --ui --hold 60
   ```
   Para modem RS-232 que precisa de RTS controlado pelo PC, o padrão (`RtsControl=Toggle`, `DtrEnabled=True`) já serve; para modem USB/Bluetooth que controla a portadora sozinho, acrescente `--parent-prop RtsControl=Off`. Modem Bluetooth: use a porta COM de **saída** do pareamento e, se a resposta demorar, `--parent-prop ResponseTimeoutMs=3000`.

   **Sem fio (gateway WirelessHART ou instrumento HART-IP):**
   ```powershell
   lasecfdt chain <DTM do instrumento> --parent JosueLab.LasecHartCommDTM.CommDtm --parent-prop Interface=HartIpUdp --parent-prop HostAddress=<IP do gateway> --policy readonly --ui --hold 60
   ```
   Pela rede, o DTM do instrumento precisa conectar pelo **endereço longo** do sub-dispositivo (o endereço de polling 0 é o próprio gateway). Esse endereço vem da varredura (aba "Instrumentos" da tela do DTM, ou varredura de topologia do Frame).

4. **Escrita, somente com autorização explícita.**
   ```powershell
   lasecfdt chain <DTM> --parent CWHartFdt.clsDTM --parent-prop Port=COMx --policy full --confirm-full --ui --hold 300
   ```
   Sem `--confirm-full`, o comando se recusa a usar `full` com um Comm DTM real.

## O que registrar

- A saída completa do comando, incluindo o tráfego auditado pelo guarda.
- O log do worker, se houver falha (`SupervisorOptions.LogDirectory`).
- Respostas de comandos específicos do fabricante. Elas podem ser gravadas em `HartDevice.FixedResponses` para enriquecer o simulador.

## Em caso de problema

| Sintoma | Ação |
|---|---|
| "Error on connection to COM port because it is busy or invalid" | Porta errada ou ocupada. Conferir `lasecfdt ports`. Um `CWFdtHARTPort.exe` órfão também segura a porta; o supervisor o encerra se o worker cair |
| Filho não fica online | Conferir alimentação e resistor do laço, e o endereço de polling (curto = 0 por padrão) |
| Gateway HART-IP não responde | Conferir IP/porta (5094), firewall do Windows e se o gateway exige HART-IP com TLS (versão 2, não suportada) |
| Worker encerrado por timeout | A STA ficou presa no DTM. O log do worker traz a pilha (cão de guarda) |
