# 05 — Projetos e SDKs existentes

Pesquisa feita em 23/11/2026: API pública de busca do GitHub (repositórios) e busca na web. A busca de **código** do GitHub exige autenticação e não foi feita.

## 1. Resultado em uma frase

**Não foi encontrado nenhum Frame Application FDT 1.x de código aberto, nem biblioteca aberta de host de DTM.** O ecossistema é dominado por componentes comerciais (FDT Group / M&M Software) e pelo PACTware (gratuito, mas fechado). A base técnica reutilizável e legal é a que já está na pasta: **type library, interop assembly e schemas oficiais do FDT-JIG**.

## 2. GitHub

| Busca (repositórios) | Resultados relevantes |
|---|---|
| `FDT DTM`, `iec 62453`, `Fdt100`, `IFdtContainer`, `DTM HART fdt`, `fdt container dtm`, `field device tool dtm` | **0** |
| `fdt frame` | 3, nenhum relacionado (layout iOS) |
| `IDtm` | 49, nenhum relacionado (DTMF, topic models) |
| `pactware` | 6 (abaixo) |

| Repositório | Licença | Linguagem | O que é | Reutilizável? |
|---|---|---|---|---|
| [wetcon/OPCUA4PACTware](https://github.com/wetcon/OPCUA4PACTware) | "Other" (sem SPDX) | C# | **Plug-in do PACTware** 5/6 que expõe DTMs via OPC UA; usa `DtmSingleInstanceDataAccess`, `DtmParameter`, `DtmSingleDeviceDataAccess`, `FdtCommunication.TransactionRequest` | **Não como Frame**: depende do PACTware hospedar os DTMs. Útil apenas como referência de uso das interfaces de acesso a dados; licença precisa ser lida antes de copiar qualquer trecho |
| leor279/OPCUA4PACTware-master | sem licença | C# | Cópia do anterior | Não |
| ININDII-UFU/* (3 repos) | sem licença | Python/C++ | Material didático que usa o PACTware | Não |
| josuemoraisgh/II2P07_LD301_Comissionamento_PACTware | sem licença | — | Material de comissionamento com PACTware | Não |

## 3. Componentes comerciais / institucionais

| Fonte | O que oferece | Compatibilidade | Situação para o LasecFDT |
|---|---|---|---|
| **M&M Software — fdtCONTAINER component** | "Common component" oficial para Frame Applications (FDT 1.2.x e 2.x); M&M também tem a especificação 1.2.1 online e ferramentas (dtmINSPECTOR) | FDT 1.2.1 / 2.x | Licença comercial; não comprar sem decisão do usuário |
| **FDT Group / FieldComm Group — Common Components** (FDT 2.x Frame/DTM, FDT 3.0 DTM e Server) | Bibliotecas oficiais .NET para FDT 2.1 e FDT 3.0; venda na loja do FieldComm Group, **contrato obrigatório** e taxa de "Product Service Program" de 25–30% | FDT 2.x / 3.0 | Necessário para FDT 2.x na prática (ver doc 06); bloqueado por licença |
| **PACTware Consortium** | Frame gratuito e fechado; membros do consórcio acessam a plataforma | FDT 1.2.x / 2.x | Não é dependência; o próprio objetivo é substituí-lo |
| **FDT-JIG (pasta do projeto)** | FDT100.dll, `Jigfdt.fdt100.dll` (interop .NET), merge modules, schemas | FDT 1.2/1.2.1 | **Usado.** Redistribuível via merge modules; especificação não redistribuível |

Assemblies FDT 2.x encontrados nesta máquina (instalados pelo PACTware 5.0): `Fdt.Interfaces`, `Fdt.Datatypes.*`, `Fdt.*Transformers` em `C:\Windows\assembly\GAC_MSIL`. São componentes licenciados do FDT Group — **não** devem ser referenciados nem redistribuídos pelo LasecFDT sem licença própria.

## 4. Diferença importante

- **Automatizar o PACTware** (plug-ins, OPC UA, scripts) = o PACTware continua sendo o Frame.
- **Frame independente** (objetivo do LasecFDT) = implementar `IFdtContainer` & cia. e hospedar o DTM no nosso processo. Foi o que se fez aqui, sem nenhum código de terceiros além do interop oficial do FDT-JIG.

## 5. Fontes

- [M&M FDT 1.2.1 Online Specification — The FDT Frame Application](https://products.mm-software.com/fdtspec/Specification//Fundamentals/TheFDTFrameApplication.htm)
- [M&M — fdtCONTAINER component](https://mm-software.com/en/products/fdtcontainer-component)
- [M&M adds FDT 2 to fdtCONTAINER](https://www.automation.com/article/mm-software-adds-fdt-2-to-fdtcontainer)
- [FieldComm Group Store — DTM Common Component (FDT 3.0)](https://store.fieldcommgroup.org/products/dtm-cc)
- [FieldComm Group Store — FDT Server Common Component](https://store.fieldcommgroup.org/products/fdt-server-cc)
- [Control Engineering — IIoT development tools receive standard update](https://www.controleng.com/iiot-development-tools-receive-standard-update/)
- [Automation World — FDT/DTM solutions (menção ao consórcio PACTware)](https://www.automationworld.com/home/article/13315343/fdtdtm-solutions-offer-great-promise-for-automation-suppliers)
- [GitHub — wetcon/OPCUA4PACTware](https://github.com/wetcon/OPCUA4PACTware)
