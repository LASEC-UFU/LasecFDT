using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace LasecFDT.Fdt1.Comm
{
    /// <summary>
    /// Instrumento HART simulado no nível de bytes do fio (camada de enlace HART), atendendo numa porta
    /// serial. Pensado para pares de portas virtuais (com0com, ex.: COM20 ↔ CNCB0): o Comm DTM real
    /// (ex.: CodeWrights HART) abre uma ponta como se fosse o modem; o simulador responde na outra.
    /// Nenhum instrumento real participa.
    ///
    /// Quadro HART (especificação pública de enlace; não faz parte da documentação do projeto):
    ///   preâmbulos 0xFF… | delimitador | endereço (1 byte curto / 5 bytes longo) | comando | contagem | dados | checksum (XOR)
    ///   delimitador: bits 0–2 = tipo (2 = STX mestre→escravo, 6 = ACK escravo→mestre), bit 7 = endereço longo.
    ///   endereço curto: bit 7 = mestre primário, bit 6 = burst, bits 0–5 = endereço de polling.
    ///   resposta: dados = [response code, field device status, dados do comando…].
    /// </summary>
    public sealed class HartWireSimulator : IDisposable
    {
        private readonly string _portName;
        private readonly int _baud;
        private readonly byte _parity;
        private Win32SerialPort _port;
        private readonly Thread _thread;
        private volatile bool _stop;
        private readonly List<byte> _buf = new List<byte>();

        /// <param name="parity">0 = nenhuma, 1 = ímpar (padrão HART), 2 = par.</param>
        public HartWireSimulator(string portName, HartDevice device, int baud = 1200, byte parity = 1)
        {
            Device = device;
            _portName = portName; _baud = baud; _parity = parity;
            _thread = new Thread(Loop) { IsBackground = true, Name = "HART sim " + portName };
        }

        public HartDevice Device { get; }
        public string PortName => _portName;
        public int FramesReceived { get; private set; }
        public int FramesAnswered { get; private set; }
        /// <summary>Falhas de E/S (ex.: o mestre fechou a porta). O simulador continua atendendo.</summary>
        public List<string> Errors { get; } = new List<string>();
        public static Action<string> Trace { get; set; }
        private void T(string m) => Trace?.Invoke($"[hart-sim {PortName}] {m}");

        public void Start()
        {
            _port = new Win32SerialPort(_portName, _baud, _parity, 100);
            _thread.Start();
            T($"atendendo como instrumento fabr.={Device.ManufacturerId} tipo={Device.DeviceTypeId} polling={Device.PollingAddress} id={Device.DeviceId:X6}");
        }

        public void Dispose()
        {
            _stop = true;
            if (_thread.IsAlive) _thread.Join(1000);
            try { _port?.Dispose(); } catch { }
        }

        private void Loop()
        {
            var chunk = new byte[256];
            while (!_stop)
            {
                int n;
                try { n = _port.Read(chunk); if (n == 0) continue; }
                catch (Exception ex)
                {
                    if (_stop) return;
                    lock (Errors) Errors.Add("leitura: " + ex.Message);
                    T("erro de leitura: " + ex.Message);
                    Thread.Sleep(200); // porta virtual sem o outro lado: tenta de novo
                    continue;
                }
                lock (_buf) { _buf.AddRange(chunk.Take(n)); }
                while (TryParse(out var frame))
                {
                    try { Handle(frame); }
                    catch (Exception ex) { lock (Errors) Errors.Add($"cmd {frame.Command}: {ex.GetType().Name} {ex.Message}"); T("falha ao responder: " + ex.Message); }
                }
            }
        }

        private sealed class Frame { public bool Long; public byte[] Address; public byte Command; public byte[] Data; public bool Primary; }

        /// <summary>Extrai um quadro mestre→escravo completo do buffer (descarta lixo e quadros com checksum inválido).</summary>
        private bool TryParse(out Frame f)
        {
            f = null;
            lock (_buf)
            {
                while (true)
                {
                    int i = 0;
                    while (i < _buf.Count && _buf[i] == 0xFF) i++;
                    if (i >= _buf.Count) return false;
                    if (i < 2) { _buf.RemoveRange(0, i + 1); continue; }        // precisa de ≥2 preâmbulos
                    byte delim = _buf[i];
                    if ((delim & 0x07) != 0x02) { _buf.RemoveRange(0, i + 1); continue; } // não é STX
                    bool lng = (delim & 0x80) != 0;
                    int addrLen = lng ? 5 : 1;
                    int hdr = i + 1 + addrLen + 2;                                 // delim + endereço + cmd + contagem
                    if (_buf.Count < hdr) return false;
                    int bc = _buf[hdr - 1];
                    int total = hdr + bc + 1;
                    if (_buf.Count < total) return false;
                    var body = _buf.Skip(i).Take(total - i - 1).ToArray();
                    byte chk = _buf[total - 1];
                    _buf.RemoveRange(0, total);
                    if (body.Aggregate((byte)0, (a, b) => (byte)(a ^ b)) != chk) { T("checksum inválido — quadro descartado"); continue; }
                    var addr = body.Skip(1).Take(addrLen).ToArray();
                    f = new Frame { Long = lng, Address = addr, Command = body[1 + addrLen], Data = body.Skip(addrLen + 3).ToArray(), Primary = (addr[0] & 0x80) != 0 };
                    FramesReceived++;
                    return true;
                }
            }
        }

        private bool AddressedToMe(Frame f)
        {
            if (!f.Long) return (f.Address[0] & 0x3F) == Device.PollingAddress; // quadro curto: endereço de polling
            var me = Device.UniqueAddress();
            if ((f.Address[0] & 0x3F) != me[0]) return false;
            return f.Address.Skip(1).SequenceEqual(me.Skip(1));
        }

        private void Handle(Frame f)
        {
            if (!AddressedToMe(f)) { T($"cmd {f.Command} para outro endereço {HartDevice.ToHex(f.Address)} — ignorado"); return; }
            var (rc, status, data) = Device.Execute(f.Command, f.Data);
            var addr = (byte[])f.Address.Clone();
            addr[0] &= 0xBF;                                   // escravo responde sem o bit de burst, ecoando o bit de mestre
            var body = new List<byte> { (byte)(f.Long ? 0x86 : 0x06) };
            body.AddRange(addr);
            body.Add(f.Command);
            body.Add((byte)(data.Length + 2));
            body.Add(rc); body.Add(status);
            body.AddRange(data);
            byte chk = body.Aggregate((byte)0, (a, b) => (byte)(a ^ b));
            var frame = Enumerable.Repeat((byte)0xFF, Device.PreambleCount).Concat(body).Concat(new[] { chk }).ToArray();
            Thread.Sleep(20); // tempo de resposta do escravo
            _port.Write(frame);
            FramesAnswered++;
            T($"{(f.Long ? "long" : "short")} cmd {f.Command} rc={rc} → {frame.Length} bytes");
        }
    }
}
