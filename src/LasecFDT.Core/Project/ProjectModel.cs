using System;
using System.Collections.Generic;

namespace LasecFDT.Core.Project
{
    /// <summary>
    /// Projeto LasecFDT (formato próprio, JSON). Cada instância guarda o CLSID do DTM, o device type
    /// usado em IDtm::InitNew e o data set opaco produzido por IPersistXXX::Save (FDT 1.2.1 §2.7, §6.14).
    /// O conteúdo do data set só é conhecido pelo DTM (§2.6.1) — o Frame apenas o armazena.
    /// </summary>
    public sealed class LasecProject
    {
        public string FormatVersion { get; set; } = "2";
        public string Name { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
        public List<DeviceInstance> Devices { get; set; } = new List<DeviceInstance>();
    }

    public sealed class DeviceInstance
    {
        /// <summary>systemTag: identificador único da instância no projeto (§8.1). Não é a tag HART.</summary>
        public string SystemTag { get; set; }
        public string DisplayName { get; set; }
        public string DtmClsid { get; set; }
        public string DtmProgId { get; set; }
        public string DtmVersion { get; set; }
        /// <summary>Elemento fdt:DtmDeviceType escolhido (usado em IDtm::InitNew).</summary>
        public string DeviceTypeXml { get; set; }
        public PersistedDataSet DataSet { get; set; }

        // ---- topologia (formato 2; ausentes em projetos antigos = nós soltos na raiz)

        /// <summary>systemTag do nó pai (Comm/Gateway DTM); null = ligado diretamente ao PC (raiz).</summary>
        public string ParentTag { get; set; }
        /// <summary>Caminho do canal do pai ao qual este nó está ligado (§4.5.1.2).</summary>
        public string ChannelPath { get; set; }
        /// <summary>"Communication", "Gateway" ou "Device" (derivado das categorias do DTM ao adicionar).</summary>
        public string Role { get; set; }
        public string DtmName { get; set; }
        public string DtmVendor { get; set; }
        public int DeviceTypeIndex { get; set; }
        public string DeviceTypeName { get; set; }
    }

    /// <summary>Data set opaco de um DTM, conforme a interface de persistência que ele implementa.</summary>
    public sealed class PersistedDataSet
    {
        /// <summary>"PropertyBag" (IPersistPropertyBag) ou "StreamInit" (IPersistStreamInit).</summary>
        public string Kind { get; set; }
        /// <summary>Conteúdo do IStream (IPersistStreamInit), em base64.</summary>
        public string StreamBase64 { get; set; }
        /// <summary>Propriedades gravadas no IPropertyBag (IPersistPropertyBag).</summary>
        public List<PersistedProperty> Properties { get; set; }
        public DateTime SavedUtc { get; set; } = DateTime.UtcNow;
    }

    public sealed class PersistedProperty
    {
        public string Name { get; set; }
        /// <summary>Tipo .NET do VARIANT gravado (String, Int32, Boolean, Double, Byte[]…).</summary>
        public string Type { get; set; }
        /// <summary>Valor como texto invariante; Byte[] em base64.</summary>
        public string Value { get; set; }
    }
}
