using System;
using System.Globalization;
using System.Threading;
using LasecFDT.Fdt1.Comm;

namespace LasecFDT.Cli
{
    internal static partial class Program
    {
        /// <summary>
        /// lasecfdt sim (--serial PORTA | --hartip udp|tcp[:PORTA]) [--mfr N] [--type N] [--tag T] [--subdevices N] [--delayed]
        /// Instrumento HART simulado que fica atendendo até Ctrl+C (ou até o stdin fechar): para testar a UI e os Comm DTMs
        /// sem hardware. Serial: use o outro lado de um par virtual (com0com, ex. CNCB0 ↔ COM20).
        /// </summary>
        private static int CmdSim(string[] args)
        {
            string serial = Opt(args, "--serial"), hartip = Opt(args, "--hartip");
            if (serial == null && hartip == null) return Usage();
            byte mfr = byte.Parse(Opt(args, "--mfr") ?? "62", CultureInfo.InvariantCulture);
            byte type = byte.Parse(Opt(args, "--type") ?? "2", CultureInfo.InvariantCulture);
            var dev = new HartDevice { ManufacturerId = mfr, DeviceTypeId = type };
            if (Opt(args, "--tag") != null) dev.Tag = Opt(args, "--tag").ToUpperInvariant();

            var stop = new ManualResetEventSlim();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; stop.Set(); };
            new Thread(() => { try { while (Console.In.Read() >= 0) { } } catch { } stop.Set(); }) { IsBackground = true }.Start();

            if (serial != null)
            {
                using (var wire = new HartWireSimulator(serial, dev))
                {
                    HartWireSimulator.Trace = m => Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff ") + m);
                    wire.Start();
                    Console.WriteLine($"Instrumento simulado em {serial}: fabricante {mfr}, tipo {type}, tag {dev.Tag}, polling 0. Ctrl+C encerra.");
                    stop.Wait();
                    Console.WriteLine($"{wire.FramesReceived} quadros recebidos, {wire.FramesAnswered} respondidos.");
                }
                return 0;
            }

            var parts = hartip.Split(':');
            bool tcp = parts[0].Equals("tcp", StringComparison.OrdinalIgnoreCase);
            int port = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 5094;
            int subs = int.Parse(Opt(args, "--subdevices") ?? "0", CultureInfo.InvariantCulture);
            var list = new System.Collections.Generic.List<HartDevice>();
            for (int i = 0; i < subs; i++) list.Add(new HartDevice { ManufacturerId = mfr, DeviceTypeId = type, DeviceId = (uint)(0x100 + i), Tag = $"WHART{i + 1:00}" });
            using (var hip = new HartIpSimulator(port, tcp, dev, list) { DelayedResponses = Array.IndexOf(args, "--delayed") >= 0 })
            {
                HartIpSimulator.Trace = m => Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff ") + m);
                hip.Start();
                Console.WriteLine($"Servidor HART-IP simulado em 127.0.0.1:{hip.Port}/{(tcp ? "tcp" : "udp")} ({subs} sub-dispositivo(s)). Ctrl+C encerra.");
                stop.Wait();
                Console.WriteLine($"{hip.SessionsOpened} sessão(ões), {hip.PdusAnswered} PDUs respondidos.");
            }
            return 0;
        }
    }
}
