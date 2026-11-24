using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LasecFDT.Core.Rpc;
using LasecFDT.Fdt1.Comm;
using LasecFDT.Supervisor;

namespace LasecFDT.Cli
{
    /// <summary>Comandos que usam o supervisor (um worker por instância de DTM).</summary>
    internal static partial class Program
    {
        private static Supervisor.Supervisor NewSupervisor()
        {
            // LASECFDT_LOGDIR: grava o log de cada worker (inclui a pilha da STA quando uma chamada demora).
            var sup = new Supervisor.Supervisor(WorkerLocator.Find(), new Supervisor.SupervisorOptions { LogDirectory = Environment.GetEnvironmentVariable("LASECFDT_LOGDIR") });
            sup.EventRaised += e =>
            {
                // Eventos do Frame e tráfego de comunicação ficam no resumo final; aqui só quedas, ligações e UI.
                if (e.Kind == "worker" && (e.WorkerEvent?.Kind == "frame" || e.WorkerEvent?.Kind == "comm")) return;
                Console.WriteLine($"  [{e.Kind}] {e}");
            };
            return sup;
        }

        /// <summary>lasecfdt ui &lt;DTM&gt; [--function ID]</summary>
        private static int CmdUi(string[] args)
        {
            if (args.Length < 2) return Usage();
            return Run(async () =>
            {
                using (var sup = NewSupervisor())
                {
                    var closed = new ManualResetEventSlim();
                    sup.EventRaised += e => { if (e.WorkerEvent?.Kind == "ui" && e.WorkerEvent.Method.StartsWith("closedBy")) closed.Set(); if (e.Kind == "died") closed.Set(); };
                    var i = await sup.CreateAsync(new CreateDtmParams { Dtm = args[1] });
                    Console.WriteLine($"DTM {i.Summary.Vendor} {i.Summary.Name} {i.Summary.Version} (FDT {i.Summary.FdtVersion}) no worker {i.Worker.ProcessId} — {i.Worker.Info.Dep}");
                    string channel = Opt(args, "--channel");
                    UiResult ui;
                    if (channel != null)
                    {
                        var chs = await sup.GetChannelsAsync(i);
                        var ch = channel == "first" ? chs.FirstOrDefault(c => c.HasActiveX) : chs.FirstOrDefault(c => c.Path.EndsWith(channel, StringComparison.OrdinalIgnoreCase));
                        if (ch == null) { Console.WriteLine("Canal com UI não encontrado. Canais: " + string.Join(", ", chs.Select(c => c.Path + (c.HasActiveX ? " [ui]" : "")))); await sup.ReleaseAsync(i); return 5; }
                        ui = await sup.OpenChannelUiAsync(i, new ChannelUiParams { ChannelPath = ch.Path, FunctionId = Opt(args, "--function") });
                    }
                    else ui = await sup.OpenUiAsync(i, new OpenUiParams { FunctionId = Opt(args, "--function") });
                    if (!ui.Opened) { Console.WriteLine("UI não abriu: " + ui.Error); await sup.ReleaseAsync(i); return 5; }
                    Console.WriteLine($"UI aberta: função {ui.FunctionId} '{ui.FunctionName}' (controle {ui.ControlClsid}). Feche a janela para encerrar.");
                    await Task.Run(() => closed.Wait());
                    bool ok = await sup.ReleaseAsync(i);
                    Console.WriteLine("Liberado pelo protocolo: " + ok);
                    return ok ? 0 : 5;
                }
            });
        }

        /// <summary>lasecfdt chain &lt;filho&gt; --parent &lt;sim|Comm DTM&gt; ...</summary>
        private static int CmdChain(string[] args)
        {
            if (args.Length < 2 || Opt(args, "--parent") == null) return Usage();
            string childDtm = args[1], parentSpec = Opt(args, "--parent"), simPort = Opt(args, "--hart-sim"), ipSim = Opt(args, "--hartip-sim");
            string policy = Opt(args, "--policy") ?? "blocked";
            bool realLine = !parentSpec.Equals("sim", StringComparison.OrdinalIgnoreCase) && simPort == null && ipSim == null;
            if (realLine && policy.Equals("full", StringComparison.OrdinalIgnoreCase) && Array.IndexOf(args, "--confirm-full") < 0)
            {
                Console.Error.WriteLine("Política 'full' com Comm DTM real e sem simulador pode ESCREVER em instrumento real.");
                Console.Error.WriteLine("Use --confirm-full somente com autorização explícita, ou --policy readonly.");
                return 2;
            }
            var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int k = 0; k + 1 < args.Length; k++)
                if (args[k] == "--parent-prop" && args[k + 1].Contains("="))
                    props[args[k + 1].Substring(0, args[k + 1].IndexOf('='))] = args[k + 1].Substring(args[k + 1].IndexOf('=') + 1);
            double hold = double.TryParse(Opt(args, "--hold"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var h) ? h : 3;

            HartWireSimulator wire = null;
            HartIpSimulator hip = null;
            try
            {
                if (ipSim != null)
                {
                    // --hartip-sim udp[:porta] | tcp[:porta]: servidor HART-IP local com o instrumento no endereço de polling 0.
                    var parts = ipSim.Split(':');
                    bool tcp = parts[0].Equals("tcp", StringComparison.OrdinalIgnoreCase);
                    int port = parts.Length > 1 ? int.Parse(parts[1]) : 0;
                    hip = new HartIpSimulator(port, tcp, new HartDevice { ManufacturerId = 62, DeviceTypeId = 2 });
                    HartIpSimulator.Trace = m => Console.WriteLine("  " + m);
                    hip.Start();
                    if (!props.ContainsKey("Interface")) props["Interface"] = tcp ? "HartIpTcp" : "HartIpUdp";
                    if (!props.ContainsKey("HostAddress")) props["HostAddress"] = "127.0.0.1";
                    if (!props.ContainsKey("HostPort")) props["HostPort"] = hip.Port.ToString();
                }
                if (simPort != null)
                {
                    wire = new HartWireSimulator(simPort, new HartDevice { ManufacturerId = 62, DeviceTypeId = 2 });
                    HartWireSimulator.Trace = m => Console.WriteLine("  " + m);
                    wire.Start();
                }
                return Run(async () =>
                {
                    using (var sup = NewSupervisor())
                    {
                        var child = await sup.CreateAsync(new CreateDtmParams { Dtm = childDtm });
                        Console.WriteLine($"Filho: {child} — {child.Summary.Vendor} {child.Summary.Name}");
                        if (hip != null) { hip.Gateway.ManufacturerId = Attr(child.Summary.DeviceTypeXml, "manufacturerId") ?? 62; hip.Gateway.DeviceTypeId = Attr(child.Summary.DeviceTypeXml, "deviceTypeId") ?? 2; }
                        if (wire != null) { wire.Device.ManufacturerId = Attr(child.Summary.DeviceTypeXml, "manufacturerId") ?? 62; wire.Device.DeviceTypeId = Attr(child.Summary.DeviceTypeXml, "deviceTypeId") ?? 2; }

                        DtmInstance parent = null;
                        ChannelLink link = null;
                        if (parentSpec.Equals("sim", StringComparison.OrdinalIgnoreCase))
                        {
                            await sup.ConnectSimulatorAsync(child, policy);
                            Console.WriteLine($"Canal HART simulado do Frame ligado ao filho (política {policy}).");
                        }
                        else
                        {
                            parent = await sup.CreateAsync(new CreateDtmParams { Dtm = parentSpec, Properties = props.Count > 0 ? props : null });
                            Console.WriteLine($"Pai:   {parent} — {parent.Summary.Vendor} {parent.Summary.Name}");
                            link = await sup.ConnectAsync(parent, child, policy);
                            Console.WriteLine($"Ligação entre processos: {parent.Worker.ProcessId}:{link.ChannelPath} → {child.Worker.ProcessId}:{child.SystemTag} (política {policy})");
                        }

                        bool online = await sup.WaitOnlineAsync(child, 10000);
                        Console.WriteLine("Filho online: " + online);
                        if (Array.IndexOf(args, "--ui") >= 0)
                        {
                            var ui = await sup.OpenUiAsync(child);
                            Console.WriteLine(ui.Opened ? $"UI aberta: {ui.FunctionName}" : "UI não abriu: " + ui.Error);
                        }
                        await Task.Delay(TimeSpan.FromSeconds(hold));

                        Console.WriteLine("Tráfego auditado pelo guarda:");
                        foreach (var r in await sup.GetCommLogAsync(child))
                            Console.WriteLine($"  {r.Time} {r.Direction} {r.Method}{(r.Forwarded ? "" : " [RECUSADO]")} {r.Note}");

                        if (link != null) Console.WriteLine("Desligado: " + await sup.DisconnectAsync(link));
                        bool ok = await sup.ReleaseAsync(child);
                        if (parent != null) ok &= await sup.ReleaseAsync(parent);
                        Console.WriteLine("Liberados pelo protocolo: " + ok);
                        if (hip != null) Console.WriteLine($"Servidor HART-IP simulado: {hip.SessionsOpened} sessão(ões), {hip.PdusAnswered} PDUs respondidos");
                        if (wire != null) Console.WriteLine($"Instrumento simulado: {wire.FramesReceived} quadros recebidos, {wire.FramesAnswered} respondidos; comandos: {string.Join(", ", wire.Device.Log.Select(l => l.Split(' ')[1]).Distinct())}");
                        return online ? 0 : 5;
                    }
                });
            }
            finally { wire?.Dispose(); hip?.Dispose(); }
        }

        /// <summary>lasecfdt ui-matrix [--outdir dir]: caminho de produção (supervisor + worker) para todos os DTMs.</summary>
        private static int CmdUiMatrix(string[] args)
        {
            string outDir = System.IO.Path.GetFullPath(Opt(args, "--outdir") ?? "ui-matrix");
            System.IO.Directory.CreateDirectory(outDir);
            var dtms = Fdt1.Discovery.DtmDiscovery.Scan(readTypeLibs: false).Dtms.Where(d => d.RequiredHostArch == "x86").ToList();
            string only = Opt(args, "--only"); // --only <ProgID ou CLSID>: só um DTM
            if (only != null) dtms = dtms.Where(d => string.Equals(d.ProgId, only, StringComparison.OrdinalIgnoreCase) || string.Equals(d.Clsid?.Trim('{', '}'), only.Trim('{', '}'), StringComparison.OrdinalIgnoreCase)).ToList();
            string fn = Opt(args, "--function");
            var rows = new List<Dictionary<string, object>>();
            return Run(async () =>
            {
                using (var sup = NewSupervisor())
                {
                    foreach (var d in dtms)
                    {
                        string name = d.ProgId ?? d.Clsid;
                        var row = new Dictionary<string, object> { ["dtm"] = name };
                        var sw = Stopwatch.StartNew();
                        DtmInstance i = null;
                        try
                        {
                            i = await sup.CreateAsync(new CreateDtmParams { Dtm = d.Clsid });
                            var ui = await sup.OpenUiAsync(i, new OpenUiParams { FunctionId = fn });
                            if (!ui.Opened)
                            {
                                // Sem UI no DTM: UI de canal (ex.: configuração da porta de um Comm DTM, §4.5.2)
                                foreach (var ch in (await sup.GetChannelsAsync(i)).Where(c => c.HasActiveX))
                                {
                                    var cu = await sup.OpenChannelUiAsync(i, new ChannelUiParams { ChannelPath = ch.Path });
                                    if (cu.Opened) { ui = cu; row["channel"] = ch.Path; break; }
                                    ui.Error += " | canal " + ch.Path + ": " + cu.Error;
                                }
                            }
                            row["uiOpened"] = ui.Opened;
                            if (ui.Opened)
                            {
                                row["function"] = ui.FunctionName;
                                row["colors"] = await sup.ScreenshotAsync(i, System.IO.Path.Combine(outDir, Sanitize(name) + ".png"));
                                row["closedCleanly"] = await sup.CloseUiAsync(i);
                            }
                            else row["error"] = ui.Error;
                            row["released"] = await sup.ReleaseAsync(i);
                            row["workerExit"] = i.Worker.Process.ExitCode;
                        }
                        catch (Exception ex)
                        {
                            row["error"] = ex.Message;
                            if (i != null) { row["workerCrashed"] = i.Worker.Crashed; try { await sup.ReleaseAsync(i); } catch { } }
                        }
                        row["ms"] = sw.ElapsedMilliseconds;
                        rows.Add(row);
                        Console.WriteLine($"{(Equals(row.GetValueOrDefault("uiOpened"), true) ? "UI " : "-- ")} {name,-42} cores={row.GetValueOrDefault("colors"),-5} fechou={row.GetValueOrDefault("closedCleanly"),-5} liberou={row.GetValueOrDefault("released"),-5} saída={row.GetValueOrDefault("workerExit")} {row.GetValueOrDefault("error")}");
                    }
                }
                System.IO.File.WriteAllText(System.IO.Path.Combine(outDir, "_summary.json"), Core.Json.Serialize(rows));
                int ok = rows.Count(r => Equals(r.GetValueOrDefault("uiOpened"), true) && Equals(r.GetValueOrDefault("closedCleanly"), true) && Equals(r.GetValueOrDefault("released"), true));
                Console.WriteLine($"UIs abertas, renderizadas e fechadas pelo protocolo: {ok} de {rows.Count}");
                return 0;
            });
        }

        private static string Sanitize(string s) => string.Concat(s.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        private static object GetValueOrDefault(this Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) ? v : null;

        private static byte? Attr(string xml, string name)
        {
            try { return byte.TryParse(Fdt1.Frame.FdtXml.Load(xml).DocumentElement.GetAttribute(name), out var b) ? b : (byte?)null; }
            catch { return null; }
        }

        private static int Run(Func<Task<int>> body)
        {
            try { return body().GetAwaiter().GetResult(); }
            catch (RpcException ex) { Console.Error.WriteLine($"ERRO ({ex.Code}): {ex.Message}"); return 1; }
        }
    }
}
