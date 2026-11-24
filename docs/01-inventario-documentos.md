# 01 — Inventário de documentos

## 1. Conteúdo original da pasta (preservado, não alterado)

| Arquivo | Tamanho | Conteúdo |
|---|---|---|
| `FDTSpecV12+Addendum(2162_V12+2162_Add).zip` | 5,0 MB | Especificação FDT 1.2 (PNO, mai/2001) + Adendo (2002), type library, merge modules, schemas XML |
| `FDTSpecV121+Addendum.zip` | 3,6 MB | Especificação FDT 1.2.1 (FDT-JIG, mar/2005) + Anexo PROFIBUS 1.0, type library, **interop assembly .NET**, merge modules, schemas |

## 2. Estrutura extraída (`specs/`)

Os ZIPs foram extraídos sem modificar os originais. O ZIP 1.2.1 contém dois ZIPs internos, também extraídos.

```
specs/
├── FDT-1.2/                          ← 2162_V12 + Adendo
│   ├── 2162_V12.pdf                  Especificação FDT 1.2 (PNO Order 2.162)
│   ├── 2162_Add.pdf                  Adendo à 1.2 (correções e esclarecimentos)
│   ├── FDT 100/FDT100.dll            Type library 1.2
│   ├── Merge Modules/*.MSM           FDT_100.MSM, FDT_XMLSchemata.MSM
│   └── XML-Files/                    Schemas XDR + exemplos 1.2 (inclui os schemas HART)
├── FDT-1.2.1/
│   ├── _outer/                       ZIPs internos (originais)
│   ├── spec/
│   │   ├── FDT-JIG-FDT-Specification-V1.2.1.pdf   ← REFERÊNCIA PRINCIPAL (385 páginas)
│   │   ├── License_Conditions.pdf
│   │   ├── Bin/FDT100.dll            Type library FDT 1.2.1 (LIBID 036D1471…, versão 1.20100)
│   │   ├── Bin/Interop/Jigfdt.fdt100.dll           Interop assembly .NET oficial (v1.2.1.0, assinado)
│   │   ├── Bin/Interop/policy.1.2.Jigfdt.fdt100.dll Publisher policy 1.2.0.x → 1.2.1.0
│   │   ├── Redistributable/*.msm     FDT_100, FDT_InteropAssembly, FDT_XMLSchemata
│   │   └── XML/{Schemas,Examples}    40 schemas XDR + ~60 instâncias de exemplo
│   └── profibus-annex/               Anexo "FDT for PROFIBUS V1.0" (PDF + schemas + exemplos)
├── text/                             Conversão PDF→texto (apenas para pesquisa local)
└── FDT-1.2.1-IDL-extracted.txt       Cap. 11 (IDL) em texto, para consulta
```

## 3. Relevância de cada item para o LasecFDT

| Item | Relevância | Uso no projeto |
|---|---|---|
| Spec 1.2.1 (PDF) | **Crítica** | Contratos, máquina de estados (§4.2.2), sequências (Cap. 6), instalação (Cap. 7) |
| `Bin/FDT100.dll` (type library) | **Crítica** | Fonte autoritativa de IIDs e assinaturas; lida em tempo de execução (`FdtInterfaceTable`), sem GUIDs digitados à mão. Catálogo gerado: [generated/fdt100-typelib.md](generated/fdt100-typelib.md) |
| `Jigfdt.fdt100.dll` (interop) | **Crítica** | Referenciado pelo motor C#; contém as 49 interfaces FDT e as de persistência COM |
| Schemas XDR (`XML/Schemas`) | **Crítica** | Copiados para `FDTSchemas\` e expostos aos DTMs por `IFdtContainer::GetXmlSchemaPath` (§7.1.4) |
| Exemplos XML | Alta | Modelos para os documentos que o Frame gera (`frameInfo`, `userInfo`, `functionCall`…) e base dos testes |
| Spec 1.2 + Adendo | Média | Categorias COM extras (Channel, Communication, Presentation, Fieldbus — §6.1.2 da 1.2), schemas HART (versão 1.2) |
| Anexo PROFIBUS 1.0 | Média | Schemas e identificação PROFIBUS (futuro) |
| Merge modules (`*.msm`) | Média | Instalação conforme §7.1.4 (MSI obrigatório para FDT100.dll); usar no instalador do LasecFDT |
| `License_Conditions.pdf` | **Alta (jurídica)** | Ver seção 4 |

## 4. Restrições de licença (importantes)

`License_Conditions.pdf` (1.2.1, Endress+Hauser em nome do FDT-JIG):

- Uso permitido **exclusivamente para desenvolver e suportar produtos compatíveis** com a especificação (2.1) — é exatamente o caso do LasecFDT.
- **Proibido publicar, comercializar ou distribuir a especificação** (2.3). Consequência prática: `specs/` (PDFs, textos extraídos) **não deve ir para repositório público** nem para o instalador. O `.gitignore` do projeto exclui `specs/text`.
- Os *merge modules* e o interop assembly são explicitamente "Redistributable" (pasta e ReadMe), destinados a instaladores.
- Proibido descompilar/extrair partes (2.2). A leitura da type library com `LoadTypeLibEx`/`ITypeInfo` é o uso normal de COM (não é descompilação); os documentos gerados citam seções em vez de reproduzir longos trechos.

## 5. O que **não** está na pasta (lacunas documentais)

| Faltando | Impacto |
|---|---|
| **Anexo HART para FDT 1.2.1** (schemas `FDTHARTCommunicationSchema` etc. atualizados, `036D1498…` como subpasta) | Só temos a versão 1.2 dos schemas HART. A comunicação HART aninhada (Etapa futura) precisa da versão correta |
| Anexo Foundation Fieldbus, IO-Link, Modbus | Sem suporte a esses protocolos |
| Especificação FDT 2.x (IEC 62453-42 / .NET) e FDT 3.x | Arquitetura FDT 2 apenas planejada |
| Guias de teste de conformidade (dtmINSPECTOR / FDT certification) | Validação de conformidade formal indisponível |
| Exemplos de código de Frame | Nenhum código-fonte de referência foi fornecido |

Detalhes e caminhos para obtenção: [09-lacunas-tecnicas.md](09-lacunas-tecnicas.md).
