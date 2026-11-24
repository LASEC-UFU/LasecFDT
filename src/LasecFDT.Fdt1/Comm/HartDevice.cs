using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LasecFDT.Fdt1.Comm
{
    /// <summary>
    /// Instrumento HART simulado — camada de comandos (independente do transporte).
    /// Usado pelo canal simulado do Frame (<see cref="HartSimulatedChannel"/>) e pelo simulador de fio
    /// (<see cref="HartWireSimulator"/>), que responde por uma porta serial virtual (ex.: com0com).
    ///
    /// Layouts de comandos conforme a especificação HART pública (rev. 5) — a especificação HART NÃO faz
    /// parte da documentação do projeto; os layouts são de conhecimento público e só afetam o simulador.
    /// Nenhum código aqui acessa hardware real.
    /// </summary>
    public sealed class HartDevice
    {
        public byte ManufacturerId { get; set; }
        public byte DeviceTypeId { get; set; }
        public byte PreambleCount { get; set; } = 5;
        public byte UniversalRevision { get; set; } = 5;
        public byte DeviceRevision { get; set; } = 1;
        public byte SoftwareRevision { get; set; } = 1;
        public byte HardwareRevision { get; set; } = 1;
        public byte PollingAddress { get; set; }
        public uint DeviceId { get; set; } = 0x000001;

        public string Tag { get; set; } = "LASECSIM";
        public string Descriptor { get; set; } = "LASECFDT SIM";
        public string Message { get; set; } = "INSTRUMENTO SIMULADO LASECFDT";
        public byte PvUnit { get; set; } = 32;      // °C (tabela de unidades HART)
        public float Pv { get; set; } = 25.0f;
        public float LoopCurrent { get; set; } = 12.0f;
        public float PercentRange { get; set; } = 50.0f;

        /// <summary>Respostas fixas (dados em hex) por comando — sobrepõem o comportamento interno.</summary>
        public Dictionary<int, string> FixedResponses { get; } = new Dictionary<int, string>();
        /// <summary>Data do comando 13 (dia, mês, ano−1900): 01/01/2026.</summary>
        public byte[] Date { get; set; } = { 1, 1, 126 };
        /// <summary>Comandos de escrita aceitos (17, 18) — para os testes conferirem o que foi gravado.</summary>
        public List<int> Writes { get; } = new List<int>();
        public List<string> Log { get; } = new List<string>();

        /// <summary>Instrumento com fabricante/tipo do DtmDeviceType (atributos manufacturerId/deviceTypeId, FDTDataTypesSchema).</summary>
        public static HartDevice FromDeviceType(string dtmDeviceTypeXml)
        {
            var d = new HartDevice();
            try
            {
                var e = LasecFDT.Fdt1.Frame.FdtXml.Load(dtmDeviceTypeXml).DocumentElement;
                if (byte.TryParse(e.GetAttribute("manufacturerId"), out var m)) d.ManufacturerId = m;
                if (byte.TryParse(e.GetAttribute("deviceTypeId"), out var t)) d.DeviceTypeId = t;
            }
            catch { }
            return d;
        }

        /// <summary>Executa um comando. Retorna (response code, field device status, dados).</summary>
        public (byte ResponseCode, byte DeviceStatus, byte[] Data) Execute(int command, byte[] request)
        {
            byte[] data; byte rc = 0;
            if (FixedResponses.TryGetValue(command, out var hex)) data = FromHex(hex);
            else
                switch (command)
                {
                    case 0: data = Command0(); break;
                    case 1: data = new[] { PvUnit }.Concat(F(Pv)).ToArray(); break;
                    case 2: data = F(LoopCurrent).Concat(F(PercentRange)).ToArray(); break;
                    case 3: data = F(LoopCurrent).Concat(new[] { PvUnit }).Concat(F(Pv)).ToArray(); break;
                    case 12: data = PackedAscii(Message, 32); break;
                    case 13: data = PackedAscii(Tag, 8).Concat(PackedAscii(Descriptor, 16)).Concat(Date).ToArray(); break;
                    // Escritas universais: o instrumento guarda e devolve os dados recebidos (eco), como um instrumento real.
                    case 17 when request != null && request.Length >= 24:
                        Message = UnpackAscii(request.Take(24).ToArray()).TrimEnd(); data = request.Take(24).ToArray(); Writes.Add(command); break;
                    case 18 when request != null && request.Length >= 21:
                        Tag = UnpackAscii(request.Take(6).ToArray()).TrimEnd(); Descriptor = UnpackAscii(request.Skip(6).Take(12).ToArray()).TrimEnd();
                        Date = request.Skip(18).Take(3).ToArray(); data = request.Take(21).ToArray(); Writes.Add(command); break;
                    default: data = new byte[0]; rc = 64; break; // 64 = comando não implementado
                }
            Log.Add($"cmd {command} req={ToHex(request)} → rc={rc} data={ToHex(data)}");
            return (rc, 0, data);
        }

        /// <summary>Endereço único (long frame): byte0 = fabricante (6 bits), tipo, ID (3 bytes).</summary>
        public byte[] UniqueAddress() => new byte[] { (byte)(ManufacturerId & 0x3F), DeviceTypeId, (byte)(DeviceId >> 16), (byte)(DeviceId >> 8), (byte)DeviceId };

        /// <summary>Comando 0 (Read Unique Identifier), rev. 5: 254, fabr., tipo, preâmb., rev. univ., rev. disp., rev. sw, hw(5b)+sinal(3b), flags, ID(3).</summary>
        public byte[] Command0() => new byte[]
        {
            254, ManufacturerId, DeviceTypeId, PreambleCount, UniversalRevision, DeviceRevision, SoftwareRevision,
            (byte)(HardwareRevision << 3), 0, (byte)(DeviceId >> 16), (byte)(DeviceId >> 8), (byte)DeviceId,
        };

        // ---------------------------------------------------------------- codificação

        /// <summary>IEEE-754 big-endian (ordem de bytes HART).</summary>
        public static byte[] F(float v) { var b = BitConverter.GetBytes(v); Array.Reverse(b); return b; }

        /// <summary>Packed ASCII HART: 6 bits por caractere (ASCII 0x20–0x5F), 4 caracteres em 3 bytes.</summary>
        public static byte[] PackedAscii(string s, int chars)
        {
            s = (s ?? "").ToUpperInvariant().PadRight(chars).Substring(0, chars);
            var outp = new List<byte>();
            for (int i = 0; i < chars; i += 4)
            {
                int v = 0;
                for (int k = 0; k < 4; k++) v = (v << 6) | ((i + k < s.Length ? s[i + k] : ' ') & 0x3F);
                outp.Add((byte)(v >> 16)); outp.Add((byte)(v >> 8)); outp.Add((byte)v);
            }
            return outp.ToArray();
        }

        public static string UnpackAscii(byte[] b)
        {
            var sb = new StringBuilder();
            for (int i = 0; i + 2 < b.Length; i += 3)
            {
                int v = (b[i] << 16) | (b[i + 1] << 8) | b[i + 2];
                for (int k = 3; k >= 0; k--) { int c = (v >> (6 * k)) & 0x3F; sb.Append((char)(c < 0x20 ? c + 0x40 : c)); }
            }
            return sb.ToString();
        }

        public static string ToHex(byte[] b) => b == null ? "" : string.Concat(b.Select(x => x.ToString("X2")));
        public static byte[] FromHex(string h)
        {
            h = (h ?? "").Replace(" ", "");
            return Enumerable.Range(0, h.Length / 2).Select(i => Convert.ToByte(h.Substring(i * 2, 2), 16)).ToArray();
        }
    }
}
