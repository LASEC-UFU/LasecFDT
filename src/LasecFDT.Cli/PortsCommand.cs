using System;
using System.Linq;
using System.Management;
using Microsoft.Win32;

namespace LasecFDT.Cli
{
    internal static partial class Program
    {
        /// <summary>
        /// Lista as portas seriais (HKLM\HARDWARE\DEVICEMAP\SERIALCOMM) e o dispositivo Plug and Play de cada uma.
        /// Somente leitura: nenhuma porta é aberta (abrir uma porta com modem HART pode energizar o laço/RTS).
        /// </summary>
        private static int CmdPorts()
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM"))
            {
                var ports = k?.GetValueNames().Select(n => (Device: n, Port: k.GetValue(n) as string)).OrderBy(x => x.Port).ToList();
                if (ports == null || ports.Count == 0) { Console.WriteLine("Nenhuma porta serial."); return 0; }
                var pnp = new ManagementObjectSearcher("SELECT Name, DeviceID, Manufacturer, Service FROM Win32_PnPEntity WHERE Name LIKE '%(COM%' OR Name LIKE '%CNC%'")
                    .Get().Cast<ManagementObject>().ToList();
                foreach (var (device, port) in ports)
                {
                    var m = pnp.FirstOrDefault(o => ((string)o["Name"] ?? "").Contains("(" + port + ")"));
                    string name = (string)m?["Name"] ?? "?", id = (string)m?["DeviceID"] ?? "?", mfr = (string)m?["Manufacturer"] ?? "?";
                    string kind = id.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase) ? "Bluetooth"
                                : id.StartsWith("COM0COM", StringComparison.OrdinalIgnoreCase) ? "virtual (com0com)"
                                : id.StartsWith("USB", StringComparison.OrdinalIgnoreCase) || id.StartsWith("FTDIBUS", StringComparison.OrdinalIgnoreCase) ? "USB (candidato a modem HART)"
                                : id.StartsWith("ACPI", StringComparison.OrdinalIgnoreCase) ? "porta física da placa" : "outro";
                    Console.WriteLine($"{port,-7} {kind,-30} {name}  [{mfr}]");
                    Console.WriteLine($"        {device}  {id}");
                }
            }
            return 0;
        }
    }
}
