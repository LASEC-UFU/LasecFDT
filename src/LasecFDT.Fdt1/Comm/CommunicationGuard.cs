using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml;
using Jigfdt.Fdt100;
using LasecFDT.Fdt1.Frame;

namespace LasecFDT.Fdt1.Comm
{
    /// <summary>
    /// Política de segurança aplicada a todo tráfego de comunicação aninhada que passa pelo Frame.
    /// </summary>
    public enum CommPolicy
    {
        /// <summary>Nenhuma requisição chega ao canal real: ConnectRequest/TransactionRequest retornam FALSE ("request refused", §4.5.3.2). Padrão.</summary>
        Blocked,
        /// <summary>Só comandos HART de leitura da lista branca (<see cref="HartCommands.IsKnownRead"/>) são repassados; demais são recusados.</summary>
        ReadOnly,
        /// <summary>Tudo é repassado (inclui escrita em instrumento). Exige autorização explícita do usuário.</summary>
        Full,
    }

    /// <summary>Registro de uma operação de comunicação (auditoria).</summary>
    public sealed class CommRecord
    {
        public DateTime Time { get; set; } = DateTime.Now;
        public string Direction { get; set; }   // "DTM→canal" | "canal→DTM"
        public string Method { get; set; }
        public string InvokeId { get; set; }
        public bool Forwarded { get; set; }
        public string Note { get; set; }
        public string Xml { get; set; }
        public override string ToString() => $"{Time:HH:mm:ss.fff} {Direction} {Method} {(Forwarded ? "" : "[RECUSADO] ")}{Note} {Xml}";
    }

    /// <summary>
    /// Proxy entre o Device DTM (cliente) e o canal de comunicação (Comm DTM real ou simulador).
    /// O Frame entrega ESTE objeto em IDtm::SetCommunication(), nunca o canal diretamente, para:
    ///  · auditar todo o tráfego (XML de requisições e respostas);
    ///  · impor a política (<see cref="CommPolicy"/>): por padrão nada chega ao instrumento.
    /// Implementa IFdtCommunication e repassa IFdtChannel (o DTM cliente pode consultar parâmetros do canal, §4.3.1.11).
    ///
    /// StandardOleMarshalObject: sem ele o objeto .NET seria "ágil" e executaria chamadas na thread de trabalho do
    /// DTM que chamou (visto com o LD301: ConnectRequest numa thread própria), causando deadlock ao repassar a
    /// resposta. Com ele, o COM encaminha toda chamada à thread STA dona do objeto, como num objeto COM nativo.
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class CommunicationGuard : StandardOleMarshalObject, IFdtCommunication, IFdtChannel
    {
        private readonly IFdtCommunication _inner;
        private readonly IFdtChannel _innerChannel;

        public CommunicationGuard(IFdtCommunication inner, CommPolicy policy, string clientTag)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _innerChannel = inner as IFdtChannel;
            Policy = policy;
            ClientTag = clientTag;
        }

        public CommPolicy Policy { get; set; }
        public string ClientTag { get; }
        public List<CommRecord> Log { get; } = new List<CommRecord>();
        public static Action<string> Trace { get; set; }

        internal void Add(CommRecord r) { Log.Add(r); Trace?.Invoke("[comm " + ClientTag + "] " + r); }

        private bool Refuse(string method, string invokeId, string xml, string why)
        {
            Add(new CommRecord { Direction = "DTM→canal", Method = method, InvokeId = invokeId, Forwarded = false, Note = why, Xml = xml });
            return false;
        }

        // ---------------------------------------------------------------- IFdtCommunication

        public string GetSupportedProtocols()
        {
            string r = _inner.GetSupportedProtocols();
            Add(new CommRecord { Direction = "DTM→canal", Method = "GetSupportedProtocols", Forwarded = true, Xml = r });
            return r;
        }

        public bool ConnectRequest(IFdtCommunicationEvents callBack, string invokeId, string protocolId, string fieldbusFrame)
        {
            if (Policy == CommPolicy.Blocked) return Refuse("ConnectRequest", invokeId, fieldbusFrame, "política Blocked");
            var relay = new CommunicationEventsRelay(this, callBack);
            bool ok = _inner.ConnectRequest(relay, invokeId, protocolId, fieldbusFrame);
            Add(new CommRecord { Direction = "DTM→canal", Method = "ConnectRequest", InvokeId = invokeId, Forwarded = true, Note = "protocol=" + protocolId + " ret=" + ok, Xml = fieldbusFrame });
            return ok;
        }

        public bool DisconnectRequest(string invokeId, string fieldbusFrame)
        {
            // Desconectar nunca é perigoso; repassa sempre (exceto em Blocked, onde não há conexão).
            if (Policy == CommPolicy.Blocked) return Refuse("DisconnectRequest", invokeId, fieldbusFrame, "política Blocked");
            bool ok = _inner.DisconnectRequest(invokeId, fieldbusFrame);
            Add(new CommRecord { Direction = "DTM→canal", Method = "DisconnectRequest", InvokeId = invokeId, Forwarded = true, Note = "ret=" + ok, Xml = fieldbusFrame });
            return ok;
        }

        public bool TransactionRequest(string invokeId, string fieldbusFrame)
        {
            if (Policy == CommPolicy.Blocked) return Refuse("TransactionRequest", invokeId, fieldbusFrame, "política Blocked");
            if (Policy == CommPolicy.ReadOnly)
            {
                int? cmd = HartCommands.CommandNumberOf(fieldbusFrame);
                if (cmd == null || !HartCommands.IsKnownRead(cmd.Value))
                    return Refuse("TransactionRequest", invokeId, fieldbusFrame, $"política ReadOnly: comando {(cmd?.ToString() ?? "?")} não é leitura conhecida");
            }
            bool ok = _inner.TransactionRequest(invokeId, fieldbusFrame);
            Add(new CommRecord { Direction = "DTM→canal", Method = "TransactionRequest", InvokeId = invokeId, Forwarded = true, Note = "ret=" + ok, Xml = fieldbusFrame });
            return ok;
        }

        public void Abort(string fieldbusFrame)
        {
            Add(new CommRecord { Direction = "DTM→canal", Method = "Abort", Forwarded = Policy != CommPolicy.Blocked, Xml = fieldbusFrame });
            if (Policy != CommPolicy.Blocked) _inner.Abort(fieldbusFrame);
        }

        public bool SequenceBegin(string fieldbusFrame) => Seq("SequenceBegin", fieldbusFrame, () => _inner.SequenceBegin(fieldbusFrame));
        public bool SequenceStart(string fieldbusFrame) => Seq("SequenceStart", fieldbusFrame, () => _inner.SequenceStart(fieldbusFrame));
        public bool SequenceEnd(string fieldbusFrame) => Seq("SequenceEnd", fieldbusFrame, () => _inner.SequenceEnd(fieldbusFrame));

        private bool Seq(string m, string xml, Func<bool> call)
        {
            // Sequências agrupam transações já filtradas individualmente; em ReadOnly o conteúdo de cada uma
            // continua passando por TransactionRequest. Em Blocked são recusadas.
            if (Policy == CommPolicy.Blocked) return Refuse(m, null, xml, "política Blocked");
            bool ok = call();
            Add(new CommRecord { Direction = "DTM→canal", Method = m, Forwarded = true, Note = "ret=" + ok, Xml = xml });
            return ok;
        }

        // ---------------------------------------------------------------- IFdtChannel (somente leitura é repassada)

        public string GetChannelPath() => _innerChannel?.GetChannelPath();

        public string GetChannelParameters(string parameterPath, string protocolId)
        {
            string r = _innerChannel?.GetChannelParameters(parameterPath, protocolId);
            Add(new CommRecord { Direction = "DTM→canal", Method = "GetChannelParameters", Forwarded = _innerChannel != null, Note = "protocol=" + protocolId, Xml = r });
            return r;
        }

        public bool SetChannelParameters(string parameterPath, string protocolId, string xmlDocument)
        {
            // Alterar parâmetros do canal pai é responsabilidade do usuário/Frame, não do DTM filho.
            return Refuse("SetChannelParameters", null, xmlDocument, "não permitido ao DTM cliente");
        }
    }

    /// <summary>
    /// Recebe as respostas assíncronas do canal (IFdtCommunicationEvents/2), registra e repassa ao DTM cliente.
    /// Se o cliente implementa IFdtCommunicationEvents2, OnConnectResponse2 é repassado como tal; senão, como OnConnectResponse.
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class CommunicationEventsRelay : StandardOleMarshalObject, IFdtCommunicationEvents, IFdtCommunicationEvents2
    {
        private readonly CommunicationGuard _guard;
        private readonly IFdtCommunicationEvents _client;

        public CommunicationEventsRelay(CommunicationGuard guard, IFdtCommunicationEvents client) { _guard = guard; _client = client; }

        private void Rec(string m, string invokeId, string xml) =>
            _guard.Add(new CommRecord { Direction = "canal→DTM", Method = m, InvokeId = invokeId, Forwarded = true, Xml = xml });

        public void OnAbort(string communicationReference) { Rec("OnAbort", null, communicationReference); _client?.OnAbort(communicationReference); }
        public void OnConnectResponse(string invokeId, string response) { Rec("OnConnectResponse", invokeId, response); _client?.OnConnectResponse(invokeId, response); }
        public void OnDisconnectResponse(string invokeId, string response) { Rec("OnDisconnectResponse", invokeId, response); _client?.OnDisconnectResponse(invokeId, response); }
        public void OnTransactionResponse(string invokeId, string response) { Rec("OnTransactionResponse", invokeId, response); _client?.OnTransactionResponse(invokeId, response); }

        public void OnConnectResponse2(string invokeId, string parentInformation, string response)
        {
            Rec("OnConnectResponse2", invokeId, response);
            if (_client is IFdtCommunicationEvents2 c2) c2.OnConnectResponse2(invokeId, parentInformation, response);
            else _client?.OnConnectResponse(invokeId, response);
        }
    }

    /// <summary>Classificação de comandos HART usada pela política ReadOnly.</summary>
    public static class HartCommands
    {
        /// <summary>
        /// Comandos HART universais/de prática comum que apenas LEEM dados do instrumento.
        /// Fonte: conjunto de comandos universais HART (HCF_SPEC-127) e de prática comum (HCF_SPEC-151),
        /// conforme conhecimento público — a especificação HART NÃO está na documentação do projeto; conferir
        /// antes de usar com instrumento real. Comandos específicos do fabricante (128–253) são sempre tratados
        /// como desconhecidos (podem escrever) e recusados em ReadOnly.
        /// </summary>
        private static readonly HashSet<int> Reads = new HashSet<int>
        {
            0, 1, 2, 3, 7, 8, 9, 11, 12, 13, 14, 15, 16, 20, 21, 48, // universais de leitura
            33, 50, 54,                                              // prática comum de leitura
        };

        public static bool IsKnownRead(int cmd) => Reads.Contains(cmd);

        /// <summary>Extrai commandNumber de um DataExchangeRequest (FDTHARTCommunicationSchema).</summary>
        public static int? CommandNumberOf(string fieldbusFrame)
        {
            try
            {
                var doc = FdtXml.Load(fieldbusFrame);
                var e = doc.GetElementsByTagName("*").OfType<XmlElement>().FirstOrDefault(x => x.LocalName == "DataExchangeRequest");
                return e != null && int.TryParse(e.GetAttribute("commandNumber"), out int n) ? n : (int?)null;
            }
            catch { return null; }
        }
    }
}
