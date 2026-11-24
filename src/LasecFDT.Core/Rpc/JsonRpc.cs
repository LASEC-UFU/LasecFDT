using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace LasecFDT.Core.Rpc
{
    /// <summary>
    /// Mensagem JSON-RPC simplificada (inspirada em JSON-RPC 2.0), uma por linha (UTF-8, JSON compacto):
    ///   requisição   {"id":1,"method":"create","params":{...}}
    ///   resposta     {"id":1,"result":{...}}  |  {"id":1,"error":{"code":-1,"message":"..."}}
    ///   notificação  {"method":"event","params":{...}}  (sem id; do worker para o supervisor)
    /// Usada entre supervisor e workers (named pipe) e, futuramente, entre a UI (Flutter) e o supervisor.
    /// </summary>
    public sealed class RpcMessage
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("method")] public string Method { get; set; }
        [JsonPropertyName("params")] public JsonNode Params { get; set; }
        [JsonPropertyName("result")] public JsonNode Result { get; set; }
        [JsonPropertyName("error")] public RpcError Error { get; set; }

        [JsonIgnore] public bool IsRequest => Id.HasValue && Method != null;
        [JsonIgnore] public bool IsResponse => Id.HasValue && Method == null;
        [JsonIgnore] public bool IsNotification => !Id.HasValue && Method != null;
    }

    public sealed class RpcError
    {
        [JsonPropertyName("code")] public int Code { get; set; }
        [JsonPropertyName("message")] public string Message { get; set; }
        [JsonPropertyName("data")] public JsonNode Data { get; set; }
    }

    public sealed class RpcException : Exception
    {
        public int Code { get; }
        public RpcException(int code, string message) : base(message) { Code = code; }
        public const int MethodNotFound = -32601, InvalidParams = -32602, Internal = -32603, Timeout = -32000, WorkerDied = -32001, DtmFailure = -32010;
    }

    /// <summary>
    /// Canal de mensagens por linhas sobre um Stream (ex.: NamedPipe). <see cref="Write"/> apenas ENFILEIRA: uma
    /// thread escritora dedicada faz a E/S. Motivo (medido no worker): escrever no pipe a partir da thread STA
    /// bloqueia; durante o bloqueio o COM bombeia mensagens, o DTM reentra com outro evento e a segunda escrita
    /// trava esperando a primeira — deadlock na mesma thread.
    /// </summary>
    public sealed class JsonLineChannel : IDisposable
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() },
            WriteIndented = false, // uma mensagem por linha: o serializador escapa quebras de linha nas strings
        };

        private readonly Stream _stream;
        private readonly StreamReader _reader;
        private readonly System.Collections.Concurrent.BlockingCollection<byte[]> _outgoing = new System.Collections.Concurrent.BlockingCollection<byte[]>();
        private int _inFlight;
        private static readonly Encoding Utf8 = new UTF8Encoding(false);

        /// <summary>Erro de escrita (o outro lado fechou). Depois dele as mensagens são descartadas.</summary>
        public Exception WriteError { get; private set; }

        public JsonLineChannel(Stream stream)
        {
            _stream = stream;
            _reader = new StreamReader(stream, Utf8, false, 8192, leaveOpen: true);
            new Thread(WriteLoop) { IsBackground = true, Name = "jsonl-writer" }.Start();
        }

        private void WriteLoop()
        {
            foreach (var bytes in _outgoing.GetConsumingEnumerable())
            {
                try { if (WriteError == null) { _stream.Write(bytes, 0, bytes.Length); _stream.Flush(); } }
                catch (Exception ex) { WriteError = ex; }
                finally { Interlocked.Decrement(ref _inFlight); }
            }
        }

        /// <summary>Aguarda o envio de tudo que foi enfileirado (ex.: resposta final antes de encerrar o processo).</summary>
        public bool Drain(TimeSpan timeout)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (Volatile.Read(ref _inFlight) > 0) { if (sw.Elapsed > timeout) return false; Thread.Sleep(5); }
            return true;
        }

        /// <summary>Lê a próxima mensagem; null quando o outro lado fechou.</summary>
        public async Task<RpcMessage> ReadAsync()
        {
            while (true)
            {
                string line = await _reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null) return null;
                if (line.Length == 0) continue;
                return JsonSerializer.Deserialize<RpcMessage>(line, Options);
            }
        }

        /// <summary>Versão síncrona (usada por threads dedicadas de leitura).</summary>
        public RpcMessage Read()
        {
            while (true)
            {
                string line = _reader.ReadLine();
                if (line == null) return null;
                if (line.Length == 0) continue;
                return JsonSerializer.Deserialize<RpcMessage>(line, Options);
            }
        }

        /// <summary>Enfileira a mensagem para envio (não bloqueia; seguro em qualquer thread, inclusive na STA).</summary>
        public void Write(RpcMessage m)
        {
            var bytes = Utf8.GetBytes(JsonSerializer.Serialize(m, Options) + "\n");
            Interlocked.Increment(ref _inFlight);
            try { _outgoing.Add(bytes); }
            catch (InvalidOperationException) { Interlocked.Decrement(ref _inFlight); } // canal já fechado
        }

        public static JsonNode ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Options);
        public static T FromNode<T>(JsonNode node) => node == null ? default : node.Deserialize<T>(Options);

        public void Dispose()
        {
            _outgoing.CompleteAdding();
            Drain(TimeSpan.FromSeconds(2));
            _reader.Dispose(); _stream.Dispose();
        }
    }
}
