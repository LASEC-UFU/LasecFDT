using System;
using System.Collections.Generic;

namespace LasecFDT.Core.Catalog
{
    /// <summary>
    /// Catálogo de DTMs instalados, construído a partir das categorias de componente COM
    /// (FDT 1.2.1 §7.1.2, §7.1.5, §7.1.6). Serializado como JSON (docs/generated/dtm-catalog.json).
    /// </summary>
    public sealed class DtmCatalog
    {
        public DateTime GeneratedAtUtc { get; set; }
        public string MachineName { get; set; }
        public bool OsIs64Bit { get; set; }
        public string ScannerProcessArch { get; set; }
        /// <summary>HKLM\SOFTWARE\FDT\DTMCatalogUpdates\ModificationFlag (§7.1.7) — permite detectar mudanças.</summary>
        public string CatalogModificationFlag { get; set; }
        public string CatalogModificationComment { get; set; }
        public FdtTypeLibStatus FdtTypeLibrary { get; set; }
        public List<RegisteredCategory> FdtCategories { get; set; } = new List<RegisteredCategory>();
        /// <summary>Classes registradas em CATID_FDT_DTM ou CATID_FDT_BTM.</summary>
        public List<DtmCatalogEntry> Dtms { get; set; } = new List<DtmCatalogEntry>();
        /// <summary>Classes com categorias FDT mas sem CATID_FDT_DTM/BTM (canais, apresentações…).</summary>
        public List<DtmCatalogEntry> RelatedFdtObjects { get; set; } = new List<DtmCatalogEntry>();
        /// <summary>Resultado da verificação cruzada via ICatInformation (Component Categories Manager).</summary>
        public CategoryManagerCrossCheck CategoryManagerCrossCheck { get; set; }
    }

    public sealed class FdtTypeLibStatus
    {
        public bool Registered { get; set; }
        public List<string> Versions { get; set; } = new List<string>();
        public string Win32Path { get; set; }
        public string Win64Path { get; set; }
    }

    public sealed class RegisteredCategory
    {
        public string Catid { get; set; }
        public string Name { get; set; }
        public string RegistryView { get; set; }
    }

    public sealed class CategoryManagerCrossCheck
    {
        public string ProcessView { get; set; }
        public int ClassesFound { get; set; }
        public List<string> MissingFromRegistryScan { get; set; } = new List<string>();
        public List<string> MissingFromCategoryManager { get; set; } = new List<string>();
        public string Error { get; set; }
    }

    public sealed class DtmCatalogEntry
    {
        public string Clsid { get; set; }
        public string Name { get; set; }
        public string ProgId { get; set; }
        public string VersionIndependentProgId { get; set; }
        /// <summary>"32" ou "64": vista do registro em que a classe está registrada.</summary>
        public string RegistryView { get; set; }
        public List<RegisteredCategory> Categories { get; set; } = new List<RegisteredCategory>();
        /// <summary>Papéis derivados das categorias: Dtm, Device, Module, Btm, Channel, Communication, PresentationActiveX, IncludedPresentation.</summary>
        public List<string> Roles { get; set; } = new List<string>();
        /// <summary>Barramentos derivados das categorias "FDT FIELDBUS …".</summary>
        public List<string> Fieldbuses { get; set; } = new List<string>();
        public ComServerInfo Server { get; set; }
        public TypeLibRef TypeLib { get; set; }
        /// <summary>Interfaces declaradas pela coclass na type library do DTM (análise estática).</summary>
        public List<InterfaceRef> DeclaredInterfaces { get; set; }
        public string DeclaredInterfacesNote { get; set; }
        /// <summary>Arquitetura de processo exigida para hospedar este servidor in-process: "x86" ou "x64".</summary>
        public string RequiredHostArch { get; set; }
        public List<string> Issues { get; set; } = new List<string>();
    }

    public sealed class ComServerInfo
    {
        /// <summary>InprocServer32 ou LocalServer32.</summary>
        public string Kind { get; set; }
        public string RegisteredPath { get; set; }
        public string ResolvedPath { get; set; }
        public bool FileExists { get; set; }
        public string ThreadingModel { get; set; }
        public bool MsiAdvertised { get; set; }
        public string PeMachine { get; set; }
        public bool IsManaged { get; set; }
        public string ManagedAssembly { get; set; }
        public string ManagedClass { get; set; }
        public string ManagedRuntimeVersion { get; set; }
        public string ManagedCodeBase { get; set; }
        public bool? ManagedCodeBaseExists { get; set; }
        public string CompanyName { get; set; }
        public string ProductName { get; set; }
        public string FileDescription { get; set; }
        public string FileVersion { get; set; }
        public string ProductVersion { get; set; }
    }

    public sealed class TypeLibRef
    {
        public string LibId { get; set; }
        public string Version { get; set; }
        public string Path { get; set; }
        public bool FileExists { get; set; }
    }

    public sealed class InterfaceRef
    {
        public string Name { get; set; }
        public string Iid { get; set; }
        public bool IsFdt { get; set; }
        public bool IsSource { get; set; }
    }
}
