using System;
using System.IO;
using System.Linq;
using LasecFDT.Core;
using LasecFDT.Core.TypeLib;
using LasecFDT.Fdt1.Com;

namespace LasecFDT.Cli
{
    internal static partial class Program
    {
        [STAThread] // DTMs FDT 1.x são objetos COM de apartamento (ThreadingModel=Apartment)
        private static int Main(string[] args)
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            if (args.Length == 0) return Usage();
            try
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "typelib": return CmdTypeLib(args);
                    case "discover": return CmdDiscover(args);
                    case "probe": return CmdProbe(args);
                    case "probe-all": return CmdProbeAll(args);
                    case "project": return CmdProject(args);
                    case "ui": return CmdUi(args);
                    case "chain": return CmdChain(args);
                    case "ports": return CmdPorts();
                    case "ui-matrix": return CmdUiMatrix(args);
                    case "sim": return CmdSim(args);
                    case "params": return CmdParams(args);
                    default: return Usage();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ERRO: " + ex);
                return 1;
            }
        }

        private static int Usage()
        {
            Console.WriteLine("lasecfdt typelib <arquivo> [--out <prefixo>]   Gera JSON+Markdown de uma type library");
            Console.WriteLine("lasecfdt discover [--out <arquivo.json>]       Catálogo de DTMs instalados (somente leitura)");
            Console.WriteLine("lasecfdt probe <clsid|progid> [--init] [--dt N] [--lcid N] [--out r.json]");
            Console.WriteLine("                                              Instancia o DTM offline (sem comunicação) e registra cada chamada");
            Console.WriteLine("lasecfdt probe-all [--init] [--timeout s] [--outdir dir]");
            Console.WriteLine("                                              Sonda todos os DTMs, cada um em processo isolado");
            Console.WriteLine("lasecfdt project new <arq.json> <nome>");
            Console.WriteLine("lasecfdt project add <arq.json> <clsid|progid> [--dt N] [--name X]   Nova instância + Save");
            Console.WriteLine("lasecfdt project verify <arq.json> [--tag T]   Load→Environment→Config→Save de cada instância");
            Console.WriteLine("lasecfdt project list <arq.json>");
            Console.WriteLine("lasecfdt ui-matrix [--outdir dir]              Abre/captura/fecha a UI de todos os DTMs, cada um no seu worker");
            Console.WriteLine("lasecfdt ports                                 Portas seriais e o dispositivo de cada uma (sem abrir nenhuma)");
            Console.WriteLine("lasecfdt ui <clsid|progid> [--function ID] [--channel first|<id>]");
            Console.WriteLine("                                              Abre a UI do DTM (ou do canal) num worker isolado; aguarda fechar a janela");
            Console.WriteLine("lasecfdt chain <DTM filho> --parent <sim|Comm DTM> [--parent-prop K=V]... [--hart-sim PORTA] [--hartip-sim udp|tcp[:PORTA]]");
            Console.WriteLine("               [--policy blocked|readonly|full] [--ui] [--hold s] [--confirm-full]");
            Console.WriteLine("                                              Comunicação aninhada entre workers (canal por proxy COM)");
            Console.WriteLine("lasecfdt params <DTM> [--path XPATH] [--out arq.xml]  Parâmetros offline do DTM (IDtmParameter::GetParameters)");
            Console.WriteLine("lasecfdt sim (--serial PORTA | --hartip udp|tcp[:PORTA]) [--mfr N] [--type N] [--tag T] [--subdevices N] [--delayed]");
            Console.WriteLine("                                              Instrumento HART simulado atendendo até Ctrl+C (testes da UI sem hardware)");
            return 2;
        }

        private static string Opt(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        private static int CmdDiscover(string[] args)
        {
            var catalog = Fdt1.Discovery.DtmDiscovery.Scan();
            string json = Json.Serialize(catalog);
            string outFile = Opt(args, "--out");
            if (outFile != null) File.WriteAllText(outFile, json); else Console.WriteLine(json);

            Console.Error.WriteLine($"DTMs/BTMs: {catalog.Dtms.Count} | objetos FDT relacionados: {catalog.RelatedFdtObjects.Count} | processo {catalog.ScannerProcessArch}");
            foreach (var d in catalog.Dtms)
                Console.Error.WriteLine($"  {d.Clsid} [{d.RegistryView}] {d.ProgId,-40} {string.Join("+", d.Roles),-22} {string.Join(",", d.Fieldbuses),-6} {d.Server?.CompanyName} {d.Server?.FileVersion} {(d.Issues.Count > 0 ? "!" + d.Issues.Count : "")}");
            var cc = catalog.CategoryManagerCrossCheck;
            Console.Error.WriteLine($"ICatInformation (vista {cc.ProcessView}): {cc.ClassesFound} classes; faltando no scan: {cc.MissingFromRegistryScan.Count}; faltando no CCM: {cc.MissingFromCategoryManager.Count} {cc.Error}");
            return 0;
        }

        internal static Guid ResolveClsid(string idOrProgId)
        {
            if (Guid.TryParse(idOrProgId.Trim('{', '}'), out var g)) return g;
            var t = Type.GetTypeFromProgID(idOrProgId, false);
            if (t == null) throw new ArgumentException("ProgID não registrado: " + idOrProgId);
            return t.GUID;
        }

        private static int CmdProbe(string[] args)
        {
            if (args.Length < 2) return Usage();
            var clsid = ResolveClsid(args[1]);
            var opt = new Fdt1.Frame.ProbeOptions
            {
                FullInit = Array.IndexOf(args, "--init") >= 0,
                DeviceTypeIndex = int.TryParse(Opt(args, "--dt"), out var dt) ? dt : 0,
                Lcid = int.TryParse(Opt(args, "--lcid"), out var l) ? l : 0x0409,
                ConcurrentInstances = Array.IndexOf(args, "--concurrent") >= 0,
                PersistenceRoundTrip = Array.IndexOf(args, "--no-roundtrip") < 0,
            };
            var frame = new Fdt1.Frame.FrameContainer { ProjectName = "LasecFDT-Probe" };
            frame.EventRaised += e => Console.Error.WriteLine("  [evento] " + e);
            if (Array.IndexOf(args, "--trace") >= 0) Fdt1.Frame.DtmSession.Trace = m => { Console.Error.WriteLine("  [trace] " + m); Console.Error.Flush(); };
            string loadFrom = Opt(args, "--load");
            var report = loadFrom != null
                ? Fdt1.Frame.DtmProbe.RunLoad(frame, clsid, Json.Deserialize<Fdt1.Frame.ProbeReport>(File.ReadAllText(loadFrom)).SavedDataSet, opt)
                : Fdt1.Frame.DtmProbe.Run(frame, clsid, opt);

            foreach (var s in report.Steps) Console.Error.WriteLine("  " + s);
            if (report.RoundTripSteps != null)
            {
                Console.Error.WriteLine("  -- round-trip de persistência --");
                foreach (var s in report.RoundTripSteps) Console.Error.WriteLine("  " + s);
            }
            Console.Error.WriteLine($"Resultado: created={report.Created} environment={report.EnvironmentAccepted} configured={report.Configured} " +
                                    $"releasedCleanly={report.ReleasedCleanly} final={report.FinalState} roundTrip={report.RoundTrip}");
            string json = Json.Serialize(report);
            string outFile = Opt(args, "--out");
            if (outFile != null) File.WriteAllText(outFile, json); else Console.WriteLine(json);
            return report.Created ? 0 : 3;
        }

        /// <summary>
        /// Executa "probe" para cada DTM do catálogo em um processo filho: um DTM que trava ou derruba o
        /// processo não afeta os demais (mesmo princípio de isolamento proposto para o host de produção).
        /// </summary>
        private static int CmdProbeAll(string[] args)
        {
            string outDir = Opt(args, "--outdir") ?? "probe-results";
            int timeout = int.TryParse(Opt(args, "--timeout"), out var t) ? t : 90;
            bool init = Array.IndexOf(args, "--init") >= 0;
            Directory.CreateDirectory(outDir);
            var catalog = Fdt1.Discovery.DtmDiscovery.Scan(readTypeLibs: false);
            string exe = System.Reflection.Assembly.GetEntryAssembly().Location;
            var summary = new System.Collections.Generic.List<object>();

            foreach (var d in catalog.Dtms)
            {
                if (d.RequiredHostArch != (Environment.Is64BitProcess ? "x64" : "x86"))
                {
                    summary.Add(new { d.Clsid, d.ProgId, outcome = "skipped", reason = "arquitetura " + d.RequiredHostArch });
                    continue;
                }
                string name = d.ProgId ?? d.Clsid;
                string outFile = Path.Combine(outDir, name + ".json");
                var r1 = RunChild(exe, $"probe {d.Clsid} {(init ? "--init --no-roundtrip" : "")} --out \"{outFile}\"", Path.Combine(outDir, name + ".log"), timeout);
                string r2 = "-";
                if (init && r1.outcome == "ok" && File.Exists(outFile) && Json.Deserialize<Fdt1.Frame.ProbeReport>(File.ReadAllText(outFile)).SavedDataSet != null)
                {
                    string reload = Path.Combine(outDir, name + ".reload.json");
                    var rr = RunChild(exe, $"probe {d.Clsid} --load \"{outFile}\" --out \"{reload}\"", Path.Combine(outDir, name + ".reload.log"), timeout);
                    r2 = rr.outcome == "ok" && File.Exists(reload) ? Json.Deserialize<Fdt1.Frame.ProbeReport>(File.ReadAllText(reload)).RoundTrip : rr.outcome;
                }
                Console.WriteLine($"{r1.outcome,-14} {r1.ms,6} ms  {name,-40} recarga em outro processo: {r2}");
                summary.Add(new { d.Clsid, d.ProgId, outcome = r1.outcome, ms = r1.ms, reloadInNewProcess = r2, report = Path.GetFileName(outFile) });
            }
            File.WriteAllText(Path.Combine(outDir, "_summary.json"), Json.Serialize(summary));
            return 0;
        }

        private static int CmdProject(string[] args)
        {
            if (args.Length < 3) return Usage();
            string sub = args[1], file = args[2];
            var frame = new Fdt1.Frame.FrameContainer();
            var svc = new Fdt1.Frame.ProjectService(frame);
            switch (sub)
            {
                case "new":
                {
                    var p = new Core.Project.LasecProject { Name = args.Length > 3 ? args[3] : Path.GetFileNameWithoutExtension(file) };
                    Fdt1.Frame.ProjectService.SaveFile(p, file);
                    Console.WriteLine("Projeto criado: " + file);
                    return 0;
                }
                case "add":
                {
                    var p = Fdt1.Frame.ProjectService.LoadFile(file);
                    frame.ProjectName = p.Name;
                    var dev = svc.AddDevice(p, ResolveClsid(args[3]), int.TryParse(Opt(args, "--dt"), out var dt) ? dt : 0, Opt(args, "--name"));
                    dev.DtmProgId = args[3].Contains("-") ? null : args[3];
                    Fdt1.Frame.ProjectService.SaveFile(p, file);
                    Console.WriteLine($"Adicionado {dev.SystemTag} {dev.DisplayName} ({dev.DataSet.Kind}, {(dev.DataSet.StreamBase64?.Length ?? 0)} b64 chars, {dev.DataSet.Properties?.Count ?? 0} props)");
                    return 0;
                }
                case "list":
                {
                    var p = Fdt1.Frame.ProjectService.LoadFile(file);
                    Console.WriteLine($"{p.Name}: {p.Devices.Count} dispositivo(s)");
                    foreach (var d in p.Devices) Console.WriteLine($"  {d.SystemTag} {d.DisplayName,-20} {d.DtmProgId ?? d.DtmClsid} {d.DataSet?.Kind} salvo {d.DataSet?.SavedUtc:u}");
                    return 0;
                }
                case "verify":
                {
                    var p = Fdt1.Frame.ProjectService.LoadFile(file);
                    frame.ProjectName = p.Name;
                    string onlyTag = Opt(args, "--tag");
                    int failures = 0;
                    foreach (var d in p.Devices.Where(x => onlyTag == null || x.SystemTag == onlyTag))
                    {
                        var before = Json.Serialize(new { d.DataSet.Kind, d.DataSet.StreamBase64, d.DataSet.Properties });
                        var s = svc.OpenDevice(d);
                        bool ok = s.State == Fdt1.Frame.DtmState.Configured && svc.SaveDevice(s, d);
                        s.Release(TimeSpan.FromSeconds(10));
                        var after = Json.Serialize(new { d.DataSet.Kind, d.DataSet.StreamBase64, d.DataSet.Properties });
                        foreach (var st in s.Steps) Console.Error.WriteLine("    " + st);
                        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {d.SystemTag} {d.DisplayName}: restaurado={ok} dataSetIgual={before == after}");
                        if (!ok) failures++;
                    }
                    Fdt1.Frame.ProjectService.SaveFile(p, file);
                    return failures == 0 ? 0 : 4;
                }
                default: return Usage();
            }
        }

        private static (string outcome, long ms) RunChild(string exe, string arguments, string logFile, int timeoutSeconds)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true,
            };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                var errTask = p.StandardError.ReadToEndAsync();
                var outTask = p.StandardOutput.ReadToEndAsync();
                bool exited = p.WaitForExit(timeoutSeconds * 1000);
                if (!exited) { try { p.Kill(); } catch { } }
                p.WaitForExit(5000);
                File.WriteAllText(logFile, errTask.Result + outTask.Result);
                string outcome = !exited ? "timeout" : p.ExitCode == 0 ? "ok" : p.ExitCode == -1073741819 ? "crash 0xC0000005" : "exit " + p.ExitCode;
                return (outcome, sw.ElapsedMilliseconds);
            }
        }

        private static int CmdTypeLib(string[] args)
        {
            if (args.Length < 2) return Usage();
            var model = TypeLibInspector.Load(Path.GetFullPath(args[1]));
            model.SourcePath = Path.GetFileName(args[1]);
            string prefix = Opt(args, "--out");
            if (prefix == null) { Console.WriteLine(Json.Serialize(model)); return 0; }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(prefix)));
            File.WriteAllText(prefix + ".json", Json.Serialize(model));
            File.WriteAllText(prefix + ".md", TypeLibMarkdown.Render(model, "Type library " + model.Name + " (FDT IDL binária)"));
            Console.WriteLine($"{model.Types.Count} tipos -> {prefix}.json / .md");
            return 0;
        }
    }
}
