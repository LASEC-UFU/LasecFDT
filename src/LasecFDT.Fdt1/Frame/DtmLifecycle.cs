using System;
using System.Collections.Generic;
using System.Linq;
using LasecFDT.Core.Project;

namespace LasecFDT.Fdt1.Frame
{
    /// <summary>Sequências de alto nível sobre <see cref="DtmSession"/> (usadas pelo TestHost, CLI e testes).</summary>
    public static class DtmLifecycle
    {
        public static InvalidOperationException Fail(DtmSession s, string what) =>
            new InvalidOperationException($"Falha em {what}: {s.Steps.LastOrDefault(x => !x.Ok)?.Error ?? "(sem detalhe)"}");

        /// <summary>Nova instância até "configured" (§6.13.1 + IDtm::Config). Em falha, libera a instância e lança.</summary>
        public static DtmSession CreateConfigured(FrameContainer frame, Guid clsid, string label = null, int deviceTypeIndex = 0, int lcid = 0x0409, string systemTag = null)
        {
            var s = DtmSession.Create(frame, clsid, string.IsNullOrEmpty(systemTag) ? null : systemTag);
            try
            {
                if (s.ComObject == null || !s.PersistInitNew() || !s.SetEnvironment()) throw Fail(s, "inicialização");
                s.SetLanguage(lcid);
                var info = s.GetInformation();
                if (info == null || info.DeviceTypes.Count <= deviceTypeIndex) throw Fail(s, "GetInformation");
                if (!s.InitNew(info.DeviceTypes[deviceTypeIndex].OuterXml) || !s.Config()) throw Fail(s, "InitNew/Config");
                if (label != null) s.SetSystemGuiLabel(label + " " + s.SystemTag);
                s.PrivateDialogEnabled(true);
                return s;
            }
            catch { s.Release(TimeSpan.FromSeconds(10)); throw; }
        }

        /// <summary>Instância existente até "configured" (§6.13.2): IPersistXXX::Load → Environment → Config.</summary>
        public static DtmSession LoadConfigured(FrameContainer frame, Guid clsid, PersistedDataSet data, int lcid = 0x0409, string systemTag = null)
        {
            var s = DtmSession.Create(frame, clsid, string.IsNullOrEmpty(systemTag) ? null : systemTag);
            try
            {
                if (s.ComObject == null || !s.PersistLoad(data) || !s.SetEnvironment()) throw Fail(s, "carga do data set");
                s.SetLanguage(lcid);
                if (s.GetInformation() == null || !s.Config()) throw Fail(s, "Config");
                s.PrivateDialogEnabled(true);
                return s;
            }
            catch { s.Release(TimeSpan.FromSeconds(10)); throw; }
        }

        /// <summary>
        /// DTM configurado por propriedades do seu próprio data set (IPersistPropertyBag): instância nova → Save →
        /// altera as propriedades → Load em outra instância. Usado para apontar um Comm DTM a uma porta
        /// (ex.: CodeWrights HART: Port=COM20) sem a UI de configuração do canal.
        /// </summary>
        public static DtmSession CreateWithProperties(FrameContainer frame, Guid clsid, IDictionary<string, string> props, Action<string> log = null,
                                                      int deviceTypeIndex = 0, int lcid = 0x0409, string systemTag = null)
        {
            PersistedDataSet ds;
            var first = CreateConfigured(frame, clsid, null, deviceTypeIndex, lcid);
            try { ds = first.PersistSave(); } finally { first.Release(TimeSpan.FromSeconds(10)); }
            if (ds?.Properties == null) throw new InvalidOperationException("O DTM não usa IPersistPropertyBag; não é possível alterar propriedades do data set.");
            foreach (var kv in props)
            {
                var p = ds.Properties.FirstOrDefault(x => string.Equals(x.Name, kv.Key, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"Propriedade '{kv.Key}' não existe no data set do DTM.");
                log?.Invoke($"Data set: {p.Name} = {p.Value} → {kv.Value}");
                p.Value = kv.Value;
            }
            return LoadConfigured(frame, clsid, ds, lcid, systemTag);
        }

        /// <summary>Só a informação estática (§6.13.1 até GetInformation, sem InitNew) e liberação. Para catálogos.</summary>
        public static FdtXml.DtmInfo Describe(FrameContainer frame, Guid clsid, int lcid = 0x0409)
        {
            var s = DtmSession.Create(frame, clsid);
            try
            {
                if (s.ComObject == null || !s.PersistInitNew() || !s.SetEnvironment()) throw Fail(s, "inicialização");
                s.SetLanguage(lcid);
                return s.GetInformation() ?? throw Fail(s, "GetInformation");
            }
            finally { s.Release(TimeSpan.FromSeconds(10)); }
        }

        public static Guid ResolveClsid(string spec) =>
            Guid.TryParse(spec.Trim('{', '}'), out var g) ? g : Type.GetTypeFromProgID(spec, true).GUID;
    }
}
