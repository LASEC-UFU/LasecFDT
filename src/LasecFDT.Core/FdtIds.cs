using System;
using System.Collections.Generic;

namespace LasecFDT.Core
{
    /// <summary>
    /// Identificadores definidos pelas especificações FDT. Nenhum valor aqui foi inventado:
    /// cada constante cita a fonte. Os IIDs das interfaces NÃO ficam aqui — são lidos em tempo
    /// de execução da type library oficial FDT100.dll (ver TypeLibInspector / FdtInterfaceTable).
    /// </summary>
    public static class FdtIds
    {
        /// <summary>LIBID da type library Fdt100 (FDT 1.2.1 §11, cabeçalho do IDL). Versão COM 1.20100 = FDT 1.2.1.0 (§3.4).</summary>
        public static readonly Guid LibIdFdt100 = new Guid("036D1471-387B-11D4-86E1-00E0987270B9");

        // ---- Component Categories (FDT 1.2.1 §7.1.2) ----
        public static readonly Guid CatidFdtDtm = new Guid("036D1490-387B-11D4-86E1-00E0987270B9");
        public static readonly Guid CatidFdtDevice = new Guid("036D1491-387B-11D4-86E1-00E0987270B9");
        public static readonly Guid CatidFdtModule = new Guid("036D1492-387B-11D4-86E1-00E0987270B9");
        public static readonly Guid CatidFdtBtm = new Guid("036D1690-387B-11D4-86E1-00E0987270B9");

        // ---- Categorias adicionais da FDT 1.2 (§6.1.2 da spec 1.2; nomes confirmados no registro) ----
        public static readonly Guid CatidFdtChannel = new Guid("036D1493-387B-11D4-86E1-00E0987270B9");
        public static readonly Guid CatidFdtCommunication = new Guid("036D1494-387B-11D4-86E1-00E0987270B9");
        public static readonly Guid CatidFdtPresentationActiveX = new Guid("036D1495-387B-11D4-86E1-00E0987270B9");
        public static readonly Guid CatidFdtIncludedPresentation = new Guid("036D1496-387B-11D4-86E1-00E0987270B9");

        // ---- Bus categories / protocolIds (spec 1.2 §6.1.2; exemplos XML 1.2.1; anexo PROFIBUS) ----
        public static readonly Guid BusProfibusDpV0 = new Guid("036D1497-387B-11D4-86E1-00E0987270B9");
        public static readonly Guid BusHart = new Guid("036D1498-387B-11D4-86E1-00E0987270B9");
        public static readonly Guid BusProfibusDpV1 = new Guid("036D1499-387B-11D4-86E1-00E0987270B9");

        /// <summary>Prefixo comum dos GUIDs FDT-JIG (036D14xx/036D15xx/036D16xx-387B-11D4-86E1-00E0987270B9).</summary>
        public const string JigGuidSuffix = "-387B-11D4-86E1-00E0987270B9";

        /// <summary>Papel lógico associado a cada categoria de objeto.</summary>
        public static readonly IReadOnlyDictionary<Guid, string> RoleByCategory = new Dictionary<Guid, string>
        {
            { CatidFdtDtm, "Dtm" },
            { CatidFdtDevice, "Device" },
            { CatidFdtModule, "Module" },
            { CatidFdtBtm, "Btm" },
            { CatidFdtChannel, "Channel" },
            { CatidFdtCommunication, "Communication" },
            { CatidFdtPresentationActiveX, "PresentationActiveX" },
            { CatidFdtIncludedPresentation, "IncludedPresentation" },
        };

        /// <summary>Barramento associado a cada categoria "FDT FIELDBUS …" conhecida.</summary>
        public static readonly IReadOnlyDictionary<Guid, string> FieldbusByCategory = new Dictionary<Guid, string>
        {
            { BusProfibusDpV0, "PROFIBUS DPV0" },
            { BusHart, "HART" },
            { BusProfibusDpV1, "PROFIBUS DPV1" },
        };

        /// <summary>GUID na forma FdtUUIDString (sem chaves, §8.2). Comparação não sensível a maiúsculas.</summary>
        public static string ToFdtUuid(Guid g) => g.ToString("D").ToUpperInvariant();
    }
}
