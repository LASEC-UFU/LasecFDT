using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace LasecFDT.Fdt1.Comm
{
    /// <summary>
    /// Servidor HART-IP simulado (UDP e TCP) para a bancada virtual: faz o papel de um gateway WirelessHART com
    /// sub-dispositivos, ou de um instrumento HART-IP simples. Nada aqui fala com equipamento real.
    ///
    /// Cabeçalho HART-IP (8 bytes): versão, tipo de mensagem, id da mensagem, status, sequência (2), tamanho total (2).
    /// O layout do cabeçalho e do início de sessão (tipo de host 8 bits + tempo de inatividade 32 bits) confere com o
    /// dissector HART-IP do Wireshark; os valores numéricos de tipo e id seguem a especificação HART-IP da FieldComm
    /// e ainda precisam ser confirmados contra um gateway real.
    ///
    /// Modo gateway: o gateway responde no endereço de polling 0 (comandos 0, 13, 74, 84); os sub-dispositivos
    /// respondem pelo endereço longo. Com <see cref="DelayedResponses"/>, cada pedido a um sub-dispositivo é
    /// respondido primeiro com o código 33 (resposta atrasada iniciada), como num gateway WirelessHART.
    /// </summary>
    public sealed class HartIpSimulator : IDisposable
    {
        private readonly bool _tcp;
        private UdpClient _udpMain;
        private TcpListener _listener;
        private readonly List<Thread> _threads = new List<Thread>();
        private volatile bool _stop;
        private readonly HashSet<string> _pendingDr = new HashSet<string>();

        public HartIpSimulator(int port, bool tcp, HartDevice gateway, IEnumerable<HartDevice> subDevices = null)
        {
            Port = port;
            _tcp = tcp;
            Gateway = gateway;
            SubDevices = (subDevices ?? Enumerable.Empty<HartDevice>()).ToList();
        }

        public int Port { get; private set; }
        public HartDevice Gateway { get; }
        public List<HartDevice> SubDevices { get; }
        public bool DelayedResponses { get; set; }
        public int SessionsOpened { get; private set; }
        public int PdusAnswered { get; private set; }
        public List<string> Errors { get; } = new List<string>();
        public static Action<string> Trace { get; set; }
        private void T(string m) => Trace?.Invoke($"[hartip-sim {(_tcp ? "TCP" : "UDP")}:{Port}] {m}");

        public void Start()
        {
            if (_tcp)
            {
                _listener = new TcpListener(IPAddress.Loopback, Port);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                Spawn(AcceptLoop);
            }
            else
            {
                _udpMain = new UdpClient(new IPEndPoint(IPAddress.Loopback, Port));
                Port = ((IPEndPoint)_udpMain.Client.LocalEndPoint).Port;
                Spawn(UdpMainLoop);
            }
            T($"gateway fabr.={Gateway.ManufacturerId} tipo={Gateway.DeviceTypeId}; {SubDevices.Count} sub-dispositivo(s){(DelayedResponses ? "; respostas atrasadas" : "")}");
        }

        public void Dispose()
        {
            _stop = true;
            try { _udpMain?.Close(); } catch { }
            try { _listener?.Stop(); } catch { }
            foreach (var t in _threads) t.Join(1000);
        }

        private void Spawn(ThreadStart s)
        {
            var t = new Thread(s) { IsBackground = true, Name = "HART-IP sim" };
            lock (_threads) _threads.Add(t);
            t.Start();
        }

        // ------------------------------------------------------------ UDP: o início de sessão chega na porta principal;
        // a sessão continua num socket novo (porta nova), como fazem servidores HART-IP.

        private void UdpMainLoop()
        {
            while (!_stop)
            {
                byte[] raw;
                var from = new IPEndPoint(IPAddress.Any, 0);
                try { raw = _udpMain.Receive(ref from); }
                catch { if (_stop) return; continue; }
                if (raw.Length < 8 || raw[2] != 0) continue; // só início de sessão aqui
                var session = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                var client = from;
                var reply = Handle(raw, out _);
                if (reply != null) session.Send(reply, reply.Length, client);
                Spawn(() => UdpSessionLoop(session, client));
            }
        }

        private void UdpSessionLoop(UdpClient s, IPEndPoint client)
        {
            using (s)
                while (!_stop)
                {
                    if (!s.Client.Poll(200 * 1000, SelectMode.SelectRead)) continue;
                    var from = new IPEndPoint(IPAddress.Any, 0);
                    byte[] raw;
                    try { raw = s.Receive(ref from); } catch { continue; }
                    var reply = Handle(raw, out bool close);
                    if (reply != null) s.Send(reply, reply.Length, from);
                    if (close) return;
                }
        }

        // ------------------------------------------------------------ TCP

        private void AcceptLoop()
        {
            while (!_stop)
            {
                TcpClient c;
                try { c = _listener.AcceptTcpClient(); } catch { return; }
                Spawn(() => TcpSession(c));
            }
        }

        private void TcpSession(TcpClient c)
        {
            using (c)
            using (var st = c.GetStream())
                try
                {
                    while (!_stop)
                    {
                        var hdr = ReadExact(st, 8);
                        if (hdr == null) return;
                        int len = (hdr[6] << 8) | hdr[7];
                        var body = ReadExact(st, len - 8) ?? new byte[0];
                        var reply = Handle(hdr.Concat(body).ToArray(), out bool close);
                        if (reply != null) st.Write(reply, 0, reply.Length);
                        if (close) return;
                    }
                }
                catch (IOException) { }
        }

        private static byte[] ReadExact(Stream s, int n)
        {
            var b = new byte[n];
            int got = 0;
            while (got < n) { int r = s.Read(b, got, n - got); if (r <= 0) return null; got += r; }
            return b;
        }

        // ------------------------------------------------------------ mensagens

        private byte[] Handle(byte[] m, out bool close)
        {
            close = false;
            if (m.Length < 8 || m[1] != 0) return null; // só pedidos
            byte id = m[2];
            var body = m.Skip(8).ToArray();
            switch (id)
            {
                case 0: SessionsOpened++; T("sessão iniciada"); return Msg(m, body.Length >= 5 ? body.Take(5).ToArray() : new byte[] { 1, 0, 0, 0xEA, 0x60 });
                case 1: close = true; T("sessão fechada"); return Msg(m, new byte[0]);
                case 2: return Msg(m, new byte[0]);
                case 3:
                    var resp = Pdu(body);
                    if (resp == null) return null;
                    PdusAnswered++;
                    return Msg(m, resp);
                default: return Msg(m, new byte[0], type: 15);
            }
        }

        private static byte[] Msg(byte[] req, byte[] body, byte type = 1)
        {
            int len = 8 + body.Length;
            var r = new byte[len];
            r[0] = 1; r[1] = type; r[2] = req[2]; r[3] = 0; r[4] = req[4]; r[5] = req[5];
            r[6] = (byte)(len >> 8); r[7] = (byte)len;
            Buffer.BlockCopy(body, 0, r, 8, body.Length);
            return r;
        }

        /// <summary>PDU HART (do delimitador ao checksum) → PDU de resposta, ou null se ninguém responde.</summary>
        private byte[] Pdu(byte[] p)
        {
            if (p.Length < 5 || (p[0] & 0x07) != 2) return null;
            bool lng = (p[0] & 0x80) != 0;
            int addrLen = lng ? 5 : 1;
            if (p.Length < addrLen + 4) return null;
            var addr = p.Skip(1).Take(addrLen).ToArray();
            byte cmd = p[1 + addrLen];
            int bc = p[2 + addrLen];
            if (p.Length < 4 + addrLen + bc) return null;
            var data = p.Skip(3 + addrLen).Take(bc).ToArray();
            if (p.Take(3 + addrLen + bc).Aggregate((byte)0, (a, b) => (byte)(a ^ b)) != p[3 + addrLen + bc]) { lock (Errors) Errors.Add("checksum"); return null; }

            HartDevice target;
            bool isGateway;
            if (!lng) { if ((addr[0] & 0x3F) != Gateway.PollingAddress) return null; target = Gateway; isGateway = true; }
            else
            {
                var u = new[] { (byte)(addr[0] & 0x3F) }.Concat(addr.Skip(1)).ToArray();
                isGateway = u.SequenceEqual(Gateway.UniqueAddress());
                target = isGateway ? Gateway : SubDevices.FirstOrDefault(d => u.SequenceEqual(d.UniqueAddress()));
                if (target == null) { T($"cmd {cmd} para endereço desconhecido {HartDevice.ToHex(addr)}"); return null; }
            }

            byte rc; byte[] payload;
            string key = HartDevice.ToHex(addr) + "/" + cmd + "/" + HartDevice.ToHex(data);
            if (!isGateway && DelayedResponses && !_pendingDr.Contains(key))
            {
                lock (_pendingDr) _pendingDr.Add(key);
                rc = 33; payload = new byte[0];          // resposta atrasada iniciada
                T($"cmd {cmd} → sub-dispositivo: resposta atrasada (33)");
            }
            else
            {
                lock (_pendingDr) _pendingDr.Remove(key);
                if (isGateway && cmd == 74) { rc = 0; payload = Command74(); }
                else if (isGateway && cmd == 84) (rc, payload) = Command84(data);
                else { var r = target.Execute(cmd, data); rc = r.ResponseCode; payload = r.Data; }
                T($"{(isGateway ? "gateway" : "sub-dispositivo " + target.Tag)} cmd {cmd} rc={rc}");
            }

            var outp = new List<byte> { (byte)(lng ? 0x86 : 0x06) };
            outp.AddRange(addr.Select((b, i) => i == 0 ? (byte)(b & 0xBF) : b));
            outp.Add(cmd);
            outp.Add((byte)(payload.Length + 2));
            outp.Add(rc); outp.Add(0);
            outp.AddRange(payload);
            outp.Add(outp.Aggregate((byte)0, (a, b) => (byte)(a ^ b)));
            return outp.ToArray();
        }

        /// <summary>Comando 74: cartões, canais, sub-disp./canal, quantidade de dispositivos (inclui o gateway), DR, mestre, repetições.</summary>
        private byte[] Command74()
        {
            int n = SubDevices.Count + 1;
            return new byte[] { 1, 1, (byte)Math.Min(255, SubDevices.Count), (byte)(n >> 8), (byte)n, 1, 1, 3 };
        }

        /// <summary>Comando 84: índice (2), cartão, canal, fabricante (2), tipo expandido (2), ID (3), rev. universal, tag longo (32).</summary>
        private (byte, byte[]) Command84(byte[] req)
        {
            if (req.Length < 2) return (5, new byte[0]); // poucos bytes de dados
            int idx = (req[0] << 8) | req[1];
            if (idx < 1 || idx > SubDevices.Count) return (2, new byte[0]); // seleção inválida
            var d = SubDevices[idx - 1];
            var b = new List<byte> { req[0], req[1], 0, 0, 0, d.ManufacturerId, d.ManufacturerId, d.DeviceTypeId,
                                     (byte)(d.DeviceId >> 16), (byte)(d.DeviceId >> 8), (byte)d.DeviceId, d.UniversalRevision };
            b.AddRange(Encoding.GetEncoding(28591).GetBytes(d.Tag.PadRight(32).Substring(0, 32)));
            return (0, b.ToArray());
        }
    }
}
