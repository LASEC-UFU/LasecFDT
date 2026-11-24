using System;
using System.IO;
using System.Linq;
using LasecFDT.Core;
using LasecFDT.Core.Project;

namespace LasecFDT.Fdt1.Frame
{
    /// <summary>
    /// Gerenciamento mínimo de projeto (Etapa D): criar instâncias de DTM, salvar seus data sets
    /// (IPersistXXX::Save, §6.14.2) num arquivo JSON e restaurá-las (IPersistXXX::Load, §6.13.2).
    /// </summary>
    public sealed class ProjectService
    {
        private readonly FrameContainer _frame;
        public ProjectService(FrameContainer frame) { _frame = frame; }

        public static LasecProject LoadFile(string path) => Json.Deserialize<LasecProject>(File.ReadAllText(path));

        public static void SaveFile(LasecProject p, string path)
        {
            p.ModifiedUtc = DateTime.UtcNow;
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, Json.Serialize(p));      // gravação atômica: nunca deixa o projeto truncado
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }

        /// <summary>§6.13.1: nova instância → InitNew/Environment/GetInformation/InitNew(deviceType)/Config → Save.</summary>
        public DeviceInstance AddDevice(LasecProject project, Guid clsid, int deviceTypeIndex, string displayName, Action<DtmSession> onSession = null)
        {
            var s = DtmSession.Create(_frame, clsid);
            try
            {
                onSession?.Invoke(s);
                Require(s.ComObject != null, s, "CoCreateInstance");
                Require(s.PersistInitNew(), s, "IPersistXXX::InitNew");
                Require(s.SetEnvironment(), s, "Environment");
                s.SetLanguage(0x0409);
                var info = s.GetInformation();
                Require(info != null && info.DeviceTypes.Count > 0, s, "GetInformation");
                var dt = info.DeviceTypes[Math.Min(deviceTypeIndex, info.DeviceTypes.Count - 1)];
                Require(s.InitNew(dt.OuterXml), s, "IDtm::InitNew");
                Require(s.Config(), s, "IDtm::Config");
                var data = s.PersistSave();
                Require(data != null, s, "IPersistXXX::Save");
                var dev = new DeviceInstance
                {
                    SystemTag = s.SystemTag,
                    DisplayName = displayName ?? dt.Name,
                    DtmClsid = s.ClsidString,
                    DtmVersion = info.Version,
                    DeviceTypeXml = dt.OuterXml,
                    DataSet = data,
                };
                project.Devices.Add(dev);
                return dev;
            }
            finally { s.Release(TimeSpan.FromSeconds(10)); }
        }

        /// <summary>§6.13.2: abre uma instância existente a partir do data set salvo e a deixa em "configured".</summary>
        public DtmSession OpenDevice(DeviceInstance dev)
        {
            var s = DtmSession.Create(_frame, new Guid(dev.DtmClsid), dev.SystemTag);
            if (s.ComObject == null) return s;
            if (s.PersistLoad(dev.DataSet) && s.SetEnvironment())
            {
                s.SetLanguage(0x0409);
                s.Config();
            }
            return s;
        }

        /// <summary>Salva o data set atual de uma sessão aberta no dispositivo do projeto.</summary>
        public bool SaveDevice(DtmSession s, DeviceInstance dev)
        {
            var data = s.PersistSave();
            if (data == null) return false;
            dev.DataSet = data;
            return true;
        }

        private static void Require(bool ok, DtmSession s, string step)
        {
            if (ok) return;
            var last = s.Steps.LastOrDefault(x => !x.Ok);
            throw new InvalidOperationException($"Falha em {step}: {last?.Error ?? "(sem detalhe)"}");
        }
    }
}
