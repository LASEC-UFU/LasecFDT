using System;
using System.Collections.Generic;
using System.Linq;
using LasecFDT.Core.Project;

namespace LasecFDT.Fdt1.Frame
{
    public sealed class ProbeOptions
    {
        /// <summary>Executa também IDtm::InitNew + Config + GetFunctions + persistência.</summary>
        public bool FullInit { get; set; }
        public int DeviceTypeIndex { get; set; }
        public int Lcid { get; set; } = 0x0409;
        public string UserLevel { get; set; } = "maintenance";
        /// <summary>Salva, recria o DTM, carrega o data set e salva de novo (prova de round-trip).</summary>
        public bool PersistenceRoundTrip { get; set; } = true;
        /// <summary>Mantém a 1ª instância viva durante o round-trip (duas instâncias do mesmo DTM no processo).</summary>
        public bool ConcurrentInstances { get; set; }
        public TimeSpan ReleaseTimeout { get; set; } = TimeSpan.FromSeconds(10);
    }

    public sealed class ProbeReport
    {
        public string Clsid { get; set; }
        public string SystemTag { get; set; }
        public DateTime StartedAt { get; set; } = DateTime.Now;
        public string HostProcess { get; set; } = (Environment.Is64BitProcess ? "x64" : "x86") + " / CLR " + Environment.Version;
        public bool Created { get; set; }
        public bool EnvironmentAccepted { get; set; }
        public bool Configured { get; set; }
        public bool ReleasedCleanly { get; set; }
        public string FinalState { get; set; }
        public string PersistenceKind { get; set; }
        public Dictionary<string, bool> Interfaces { get; set; }
        public List<string> MissingMandatoryDtmInterfaces { get; set; }
        public FdtXml.DtmInfo Information { get; set; }
        public string InformationXml { get; set; }
        public List<FdtXml.FunctionInfo> Functions { get; set; }
        public string DefaultFunctionId { get; set; }
        public Dictionary<string, string> ActiveXByFunction { get; set; }
        public PersistedDataSet SavedDataSet { get; set; }
        public string RoundTrip { get; set; }
        public List<StepRecord> Steps { get; set; }
        public List<string> Events { get; set; }
        public List<StepRecord> RoundTripSteps { get; set; }
    }

    /// <summary>
    /// Sonda de compatibilidade de um DTM, puramente offline (nunca chama SetCommunication).
    /// Segue a sequência de §6.13.1 e as transições permitidas pela tabela de §4.2.2.
    /// </summary>
    public static class DtmProbe
    {
        /// <summary>
        /// Interfaces que a tabela de §4.10.1 marca como obrigatórias para todo DTM 1.2.1 (coluna "Availability").
        /// DTMs 1.2 não implementam IDtm2/IDtmInformation2 etc. (§3.8.1) — por isso isto é diagnóstico, não critério de exclusão.
        /// </summary>
        public static readonly string[] MandatoryDtmInterfaces =
        {
            "IDtm", "IDtm2", "IDtmDocumentation", "IDtmDiagnosis", "IDtmInformation", "IDtmInformation2",
            "IDtmOnlineDiagnosis", "IDtmParameter", "IDtmSingleInstanceDataAccess", "IFdtEvents",
        };

        public static ProbeReport Run(FrameContainer frame, Guid clsid, ProbeOptions opt)
        {
            var report = new ProbeReport { Clsid = Core.FdtIds.ToFdtUuid(clsid) };
            var s = DtmSession.Create(frame, clsid);
            report.SystemTag = s.SystemTag;
            try
            {
                report.Created = s.ComObject != null;
                if (!report.Created) return report;
                report.Interfaces = new Dictionary<string, bool>(s.Interfaces);
                report.MissingMandatoryDtmInterfaces = MandatoryDtmInterfaces.Where(i => !s.Supports(i)).ToList();
                if (!s.Supports("IDtm")) return report;

                if (!s.PersistInitNew()) return report;
                report.PersistenceKind = s.PersistenceKind;
                report.EnvironmentAccepted = s.SetEnvironment();
                if (!report.EnvironmentAccepted) return report;
                s.SetLanguage(opt.Lcid);
                s.GetInformation();
                report.Information = s.Information;
                report.InformationXml = s.InformationXml;

                if (!opt.FullInit || s.Information == null || s.Information.DeviceTypes.Count == 0) return report;
                var dt = s.Information.DeviceTypes[Math.Min(opt.DeviceTypeIndex, s.Information.DeviceTypes.Count - 1)];
                if (!s.InitNew(dt.OuterXml)) return report;
                report.Configured = s.Config(opt.UserLevel);
                if (!report.Configured) return report;
                s.SetSystemGuiLabel(dt.Name + " (" + s.SystemTag + ")");
                report.Functions = s.GetFunctions("notSupported", out var def);
                report.DefaultFunctionId = def;

                // Mapeia quais funções têm UI ActiveX (não instancia nada — só pergunta o GUID).
                report.ActiveXByFunction = new Dictionary<string, string>();
                foreach (var f in report.Functions.Where(f => !f.Hidden))
                    report.ActiveXByFunction[f.FunctionId + " " + f.Name] =
                        s.GetActiveXGuid(FdtXml.FunctionCall(f.FunctionId, f.ApplicationId)) ?? "(nenhum)";

                report.SavedDataSet = s.PersistSave();
                if (opt.PersistenceRoundTrip && report.SavedDataSet != null)
                {
                    // Padrão: libera a 1ª instância antes de recarregar (§6.14.3 "reload … for another instance").
                    // ConcurrentInstances mantém as duas vivas — testa multi-instância no mesmo processo.
                    if (!opt.ConcurrentInstances)
                        report.ReleasedCleanly = s.Release(opt.ReleaseTimeout);
                    report.RoundTrip = RoundTrip(frame, clsid, report.SavedDataSet, opt, report);
                }
                return report;
            }
            finally
            {
                if (s.ComObject != null) report.ReleasedCleanly = s.Release(opt.ReleaseTimeout);
                else if (!report.Created) report.ReleasedCleanly = true; // nada a liberar
                report.FinalState = s.State.ToString();
                report.Steps = s.Steps;
                report.Events = s.Events.Select(e => e.ToString()).ToList();
            }
        }

        /// <summary>
        /// Restauração isolada (§6.13.2): este processo só cria a instância a partir de um data set salvo
        /// por OUTRO processo — o modelo "um ciclo de vida de instância por processo".
        /// </summary>
        public static ProbeReport RunLoad(FrameContainer frame, Guid clsid, PersistedDataSet saved, ProbeOptions opt)
        {
            var report = new ProbeReport { Clsid = Core.FdtIds.ToFdtUuid(clsid), Created = true };
            report.RoundTrip = RoundTrip(frame, clsid, saved, opt, report);
            report.Steps = report.RoundTripSteps;
            report.Configured = report.RoundTrip.StartsWith("ok");
            report.ReleasedCleanly = report.Steps.Any(s => s.Step.StartsWith("aguardar IDtmEvents::OnPreparedToRelease") && s.Ok);
            report.FinalState = "Released";
            return report;
        }

        /// <summary>§6.13.2 + §6.14.2: nova instância ← Load(data set) → Environment → Config → Save; compara os data sets.</summary>
        private static string RoundTrip(FrameContainer frame, Guid clsid, PersistedDataSet saved, ProbeOptions opt, ProbeReport report)
        {
            var s2 = DtmSession.Create(frame, clsid);
            try
            {
                if (s2.ComObject == null) return "falhou: CoCreateInstance";
                if (!s2.PersistLoad(saved)) return "falhou: Load";
                if (!s2.SetEnvironment()) return "falhou: Environment após Load";
                s2.SetLanguage(opt.Lcid);
                if (!s2.Config(opt.UserLevel)) return "falhou: Config após Load";
                var again = s2.PersistSave();
                if (again == null) return "falhou: Save após Load";
                bool same = Core.Json.Serialize(Normalize(again)) == Core.Json.Serialize(Normalize(saved));
                return same ? "ok: data set idêntico após Load→Save" : "ok: Load/Save aceitos; data set difere (pode conter carimbo de tempo)";
            }
            finally
            {
                s2.Release(opt.ReleaseTimeout);
                report.RoundTripSteps = s2.Steps;
            }
        }

        private static object Normalize(PersistedDataSet d) => new { d.Kind, d.StreamBase64, d.Properties };
    }
}
