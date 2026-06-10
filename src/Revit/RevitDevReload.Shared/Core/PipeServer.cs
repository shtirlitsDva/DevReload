using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace RevitDevReload.Core
{
    // Background named-pipe server: one client at a time, newline-delimited
    // JSON requests, dispatcher runs on the pipe thread (the Revit host wraps
    // mutating commands in its own UI-thread/ExternalEvent marshalling inside
    // the dispatcher).
    //
    // A single NamedPipeServerStream instance is reused across clients via
    // Disconnect()/WaitForConnection() — disposing and recreating per client
    // races with the next client's Connect() on net48 ("safe handle closed").
    public sealed class PipeServer : IDisposable
    {
        private readonly string _pipeName;
        private readonly Func<string, JsonElement?, object?> _dispatch;
        private readonly CancellationTokenSource _cts = new();
        private Thread? _thread;
        private NamedPipeServerStream? _server;

        public PipeServer(string pipeName, Func<string, JsonElement?, object?> dispatch)
        {
            _pipeName = pipeName;
            _dispatch = dispatch;
        }

        public string PipeName => _pipeName;

        // Surfaced so the host can log connection-level failures (the request
        // dispatcher's own exceptions become error responses, not events).
        public event Action<Exception>? Error;

        public void Start()
        {
            if (_thread != null) return;
            _server = new NamedPipeServerStream(
                _pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            _thread = new Thread(ServerLoop)
            {
                IsBackground = true,
                Name = "RevitDevReload.PipeServer",
            };
            _thread.Start();
        }

        private void ServerLoop()
        {
            var server = _server!;
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    server.WaitForConnectionAsync(_cts.Token)
                        .GetAwaiter().GetResult();
                    ServeClient(server);
                }
                catch (OperationCanceledException)
                {
                    return; // shutting down
                }
                catch (ObjectDisposedException)
                {
                    return; // Dispose() closed the pipe under us
                }
                catch (Exception ex)
                {
                    Error?.Invoke(ex);
                }
                finally
                {
                    try
                    {
                        if (server.IsConnected) server.Disconnect();
                    }
                    catch { }
                }
            }
        }

        private void ServeClient(NamedPipeServerStream server)
        {
            // Full ctor overloads: the (stream, leaveOpen) shorthand doesn't
            // exist on net48. BOM-less UTF8 keeps the JSON lines clean.
            var utf8 = new UTF8Encoding(false);
            using var reader = new StreamReader(server, utf8, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(server, utf8, 1024, leaveOpen: true) { AutoFlush = true };

            while (!_cts.IsCancellationRequested && server.IsConnected)
            {
                string? line = reader.ReadLine();
                if (line == null) return; // client hung up
                if (line.Length == 0) continue;

                int id = 0;
                string response;
                try
                {
                    var request = PipeProtocol.ParseRequest(line);
                    id = request.Id;
                    object? result = _dispatch(request.Cmd, request.Args);
                    response = PipeProtocol.SerializeOk(id, result);
                }
                catch (Exception ex)
                {
                    var inner = ex is System.Reflection.TargetInvocationException tie
                        ? tie.InnerException ?? ex
                        : ex;
                    response = PipeProtocol.SerializeError(id, inner.Message);
                }

                writer.WriteLine(response);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _server?.Dispose(); } catch { }
            _thread?.Join(2000);
            _cts.Dispose();
        }
    }
}
