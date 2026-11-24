using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using LasecFDT.Core.Rpc;

namespace LasecFDT.Supervisor
{
    /// <summary>
    /// Um processo worker e seu canal JSON-RPC. Chamadas são serializadas (uma pendente por vez — o worker
    /// executa tudo na sua thread STA). Se uma chamada estoura o timeout, a STA está presa num DTM: o worker é
    /// encerrado à força (a instância é perdida; o processo isola a falha).
    /// </summary>
    public sealed class WorkerHandle : IDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly JsonLineChannel _rpc;
        private readonly ConcurrentDictionary<long, TaskCompletionSource<RpcMessage>> _pending = new ConcurrentDictionary<long, TaskCompletionSource<RpcMessage>>();
        private readonly SemaphoreSlim _callLock = new SemaphoreSlim(1, 1);
        private long _nextId;
        private int _exitedRaised;
        private volatile bool _closing;

        public string Name { get; }
        public Process Process { get; }
        public int ProcessId { get; }
        public WorkerInfo Info { get; private set; }
        public string LogPath { get; }
        public DateTime StartedAt { get; } = DateTime.Now;
        public bool HasExited { get { try { return Process.HasExited; } catch { return true; } } }
        /// <summary>True se o processo terminou sem shutdown pedido (queda, morte forçada por timeout etc.).</summary>
        public bool Crashed { get; private set; }

        public event Action<WorkerHandle, WorkerEvent> EventReceived;
        /// <summary>Disparado uma vez quando o processo termina (esperado ou não).</summary>
        public event Action<WorkerHandle> Exited;

        private WorkerHandle(string name, NamedPipeServerStream pipe, Process p, string log)
        {
            Name = name; _pipe = pipe; Process = p; ProcessId = p.Id; LogPath = log;
            _rpc = new JsonLineChannel(pipe);
        }

        /// <summary>Cria o pipe, inicia o worker e aguarda a conexão e o "hello".</summary>
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);

        public static async Task<WorkerHandle> StartAsync(string workerExe, string logPath = null, TimeSpan? connectTimeout = null)
        {
            // Nome com a sessão do Windows (diagnóstico em servidores com várias sessões) e parte aleatória.
            string name = $"LasecFDT-S{Process.GetCurrentProcess().SessionId}-{Guid.NewGuid():N}";
            var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var psi = new ProcessStartInfo(workerExe, $"--pipe {name}" + (logPath != null ? $" --log \"{logPath}\"" : ""))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = System.IO.Path.GetDirectoryName(workerExe),
            };
            var p = Process.Start(psi) ?? throw new InvalidOperationException("Falha ao iniciar o worker " + workerExe);
            var connect = pipe.WaitForConnectionAsync();
            var winner = await Task.WhenAny(connect, Task.Delay(connectTimeout ?? TimeSpan.FromSeconds(20))).ConfigureAwait(false);
            if (winner != connect)
            {
                try { p.Kill(); } catch { }
                pipe.Dispose();
                throw new RpcException(RpcException.Timeout, "O worker não se conectou ao pipe.");
            }
            // Em servidor de terminal (várias estações na mesma máquina) só o worker que este processo iniciou pode
            // ficar com o pipe: confere o PID do cliente conectado.
            if (GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint clientPid) && clientPid != (uint)p.Id)
            {
                try { p.Kill(); } catch { }
                pipe.Dispose();
                throw new RpcException(RpcException.Internal, $"Conexão inesperada no pipe do worker (PID {clientPid}, esperado {p.Id}).");
            }
            var w = new WorkerHandle(name, pipe, p, logPath);
            p.EnableRaisingEvents = true;
            p.Exited += (s, e) => w.OnProcessExit();
            w.StartReader();
            w.Info = await w.CallAsync<WorkerInfo>(WorkerMethods.Hello, null, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            if (p.HasExited) w.OnProcessExit();
            return w;
        }

        private void StartReader()
        {
            var t = new Thread(() =>
            {
                try
                {
                    while (true)
                    {
                        var m = _rpc.Read();
                        if (m == null) break;
                        if (m.IsResponse && _pending.TryRemove(m.Id.Value, out var tcs)) tcs.TrySetResult(m);
                        else if (m.IsNotification && m.Method == WorkerMethods.Event)
                        {
                            try { EventReceived?.Invoke(this, JsonLineChannel.FromNode<WorkerEvent>(m.Params)); } catch { }
                        }
                    }
                }
                catch { }
                FailPending("O canal com o worker foi fechado.");
            }) { IsBackground = true, Name = "worker-reader " + ProcessId };
            t.Start();
        }

        private void FailPending(string why)
        {
            foreach (var kv in _pending)
                if (_pending.TryRemove(kv.Key, out var tcs))
                    tcs.TrySetException(new RpcException(RpcException.WorkerDied, $"Worker {ProcessId}: {why}"));
        }

        private void OnProcessExit()
        {
            if (Interlocked.Exchange(ref _exitedRaised, 1) == 1) return;
            int code = -1;
            try { code = Process.ExitCode; } catch { }
            Crashed = !_closing || code != 0;
            FailPending($"processo terminou (código 0x{code:X8}).");
            try { Exited?.Invoke(this); } catch { }
        }

        public async Task<JsonNode> CallAsync(string method, object parameters = null, TimeSpan? timeout = null)
        {
            if (HasExited) throw new RpcException(RpcException.WorkerDied, $"Worker {ProcessId} não está em execução.");
            await _callLock.WaitAsync().ConfigureAwait(false);
            try
            {
                long id = Interlocked.Increment(ref _nextId);
                var tcs = new TaskCompletionSource<RpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending[id] = tcs;
                _rpc.Write(new RpcMessage { Id = id, Method = method, Params = parameters == null ? null : JsonLineChannel.ToNode(parameters) });
                var limit = timeout ?? TimeSpan.FromSeconds(60);
                var winner = await Task.WhenAny(tcs.Task, Task.Delay(limit)).ConfigureAwait(false);
                if (winner != tcs.Task)
                {
                    _pending.TryRemove(id, out _);
                    Kill(); // STA presa dentro do DTM: só o processo pode ser recuperado
                    throw new RpcException(RpcException.Timeout, $"'{method}' excedeu {limit.TotalSeconds:0} s; worker {ProcessId} encerrado.");
                }
                var reply = await tcs.Task.ConfigureAwait(false);
                if (reply.Error != null) throw new RpcException(reply.Error.Code, reply.Error.Message);
                return reply.Result;
            }
            finally { _callLock.Release(); }
        }

        public async Task<T> CallAsync<T>(string method, object parameters = null, TimeSpan? timeout = null) =>
            JsonLineChannel.FromNode<T>(await CallAsync(method, parameters, timeout).ConfigureAwait(false));

        /// <summary>Libera o DTM pelo protocolo e encerra o worker; força o encerramento se não sair a tempo.</summary>
        public async Task<bool> ShutdownAsync(TimeSpan? timeout = null)
        {
            if (HasExited) return !Crashed;
            _closing = true;
            bool released = false;
            try { released = (await CallAsync(WorkerMethods.Shutdown, null, timeout ?? TimeSpan.FromSeconds(30)).ConfigureAwait(false))?.GetValue<bool>() ?? false; }
            catch (RpcException) { }
            if (!await WaitForExitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false)) Kill();
            return released;
        }

        public Task<bool> WaitForExitAsync(TimeSpan timeout) => Task.Run(() => { try { return Process.WaitForExit((int)timeout.TotalMilliseconds); } catch { return true; } });

        public void Kill()
        {
            try { if (!Process.HasExited) Process.Kill(); } catch { }
        }

        public void Dispose()
        {
            if (!HasExited) { _closing = true; Kill(); }
            try { _pipe.Dispose(); } catch { }
        }
    }
}
