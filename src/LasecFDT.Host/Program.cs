using System;
using System.IO;
using System.Threading;
using LasecFDT.Core.Rpc;

namespace LasecFDT.Host
{
    internal static class Program
    {
        /// <summary>
        /// LasecFDT.Host.exe [--plant PASTA]
        /// Iniciado pela UI da estação com stdin/stdout redirecionados: JSON-RPC, uma mensagem por linha
        /// (<see cref="HostMethods"/>). Termina quando a UI fecha o stdin, liberando antes todos os DTMs pelo protocolo.
        /// Um Host por sessão do Windows: a UI garante instância única por sessão.
        /// </summary>
        [STAThread]
        private static int Main(string[] args)
        {
            string plant = null;
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--plant") plant = args[i + 1];

            // stdin/stdout herdados da UI: o canal é anônimo e exclusivo deste par de processos.
            var duplex = new DuplexStream(StdStream(-10 /*STD_INPUT_HANDLE*/, FileAccess.Read), StdStream(-11 /*STD_OUTPUT_HANDLE*/, FileAccess.Write));
            var channel = new JsonLineChannel(duplex);
            string sessionLog = null;
            try
            {
                using (var host = new HostService(channel, plant))
                {
                    sessionLog = Path.Combine(host.Info.SessionDirectory, "host.log");
                    Log(sessionLog, $"Host {host.Info.Version} estação={host.Info.Station.Id} sessão={host.Info.Station.SessionId} planta={host.Info.PlantDirectory} compartilhada={host.Info.PlantShared}");
                    host.RunAsync().GetAwaiter().GetResult();
                    Log(sessionLog, "UI fechou o canal: encerrando");
                }
                channel.Drain(TimeSpan.FromSeconds(2));
                return 0;
            }
            catch (Exception ex)
            {
                Log(sessionLog ?? Path.Combine(Path.GetTempPath(), "LasecFDT.Host.log"), "falha: " + ex);
                try { channel.Write(new RpcMessage { Method = HostMethods.Event, Params = JsonLineChannel.ToNode(new HostEventDto { Kind = "host", Method = "fatal", Detail = ex.Message, Level = "error" }) }); channel.Drain(TimeSpan.FromSeconds(2)); } catch { }
                return 1;
            }
        }

        /// <summary>
        /// stdin/stdout como FileStream sobre o handle herdado. O Dart (UI Flutter) cria os pipes do filho em modo
        /// OVERLAPPED; o Console do .NET Framework os lê com ReadFile síncrono, inválido para esse tipo de handle (a
        /// primeira leitura funciona e as seguintes se perdem — medido). Tenta o modo assíncrono primeiro e cai para o
        /// síncrono quando o handle não é overlapped (pipes anônimos, como os do Python ou do cmd).
        /// </summary>
        private static Stream StdStream(int which, FileAccess access)
        {
            IntPtr raw = GetStdHandle(which);
            if (raw == IntPtr.Zero || raw == new IntPtr(-1))
                return access == FileAccess.Read ? Console.OpenStandardInput() : Console.OpenStandardOutput();
            try { return new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(raw, false), access, 4096, isAsync: true); }
            catch (Exception) { }
            try { return new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(raw, false), access, 4096, isAsync: false); }
            catch (Exception) { return access == FileAccess.Read ? Console.OpenStandardInput() : Console.OpenStandardOutput(); }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int which);

        private static void Log(string path, string m)
        {
            try { File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {m}{Environment.NewLine}"); } catch { }
        }
    }

    /// <summary>Junta stdin (leitura) e stdout (escrita) num único Stream para o <see cref="JsonLineChannel"/>.</summary>
    internal sealed class DuplexStream : Stream
    {
        private readonly Stream _in, _out;
        public DuplexStream(Stream input, Stream output) { _in = input; _out = output; }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _in.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => _out.Write(buffer, offset, count);
        public override void Flush() => _out.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { _in.Dispose(); _out.Dispose(); } base.Dispose(disposing); }
    }
}
