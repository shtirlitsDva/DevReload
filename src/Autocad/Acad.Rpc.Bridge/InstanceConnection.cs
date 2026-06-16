using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Acad.Rpc.Core;

namespace Acad.Rpc.Bridge;

/// <summary>
/// JSON-RPC client to one AutoCAD instance's named pipe. Owns request-id
/// correlation so concurrent <c>tools/call</c> forwards multiplex over the
/// single duplex pipe. One connection per pid; <see cref="ForwarderPool"/>
/// owns the set. Connecting retries until the pipe appears or the process
/// exits, so a cold instance is picked up automatically.
/// </summary>
public sealed class InstanceConnection : IDisposable
{
    public int Pid { get; }
    public string PipeName { get; }

    private readonly Action<string> _log;
    private readonly object _gate = new();
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private CancellationTokenSource? _readerCts;
    private Task? _readerLoop;
    private CancellationTokenSource? _connectCts;
    private int _nextId;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();

    public event Action<bool>? ConnectionChanged;
    public event Action<JsonObject>? NotificationReceived;

    public bool IsConnected { get { lock (_gate) return _pipe != null && _pipe.IsConnected; } }

    /// <summary>Wait up to <paramref name="timeout"/> for the connection to
    /// come up (it connects on a background loop). Returns false if the
    /// process has exited or the wait elapses.</summary>
    public async Task<bool> WaitConnectedAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!IsConnected)
        {
            if (ProcessHasExited(Pid)) return false;
            if (DateTime.UtcNow >= deadline || ct.IsCancellationRequested) return IsConnected;
            try { await Task.Delay(100, ct).ConfigureAwait(false); } catch { return IsConnected; }
        }
        return true;
    }

    public InstanceConnection(int pid, string pipeName, Action<string>? log = null)
    {
        Pid = pid;
        PipeName = pipeName;
        _log = log ?? (_ => { });
    }

    public void Start()
    {
        var cts = new CancellationTokenSource();
        lock (_gate) { _connectCts = cts; }
        _ = Task.Run(() => ConnectLoopAsync(cts.Token));
    }

    public async Task<JsonNode?> ForwardRequestAsync(string method, JsonObject? @params, CancellationToken ct)
    {
        if (!IsConnected) throw new InvalidOperationException($"pid {Pid} pipe is not connected");

        int id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (@params != null) msg["params"] = @params.DeepClone();

        StreamWriter? writer;
        lock (_gate) { writer = _writer; }
        if (writer == null) { _pending.TryRemove(id, out _); throw new InvalidOperationException("writer gone"); }

        try { await writer.WriteLineAsync(JsonSerializer.Serialize(msg, McpProtocol.JsonOptions)).ConfigureAwait(false); }
        catch (Exception ex) { _pending.TryRemove(id, out _); throw new IOException("write to pipe failed", ex); }

        await using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        try { return await tcs.Task.ConfigureAwait(false); }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            if (ProcessHasExited(Pid))
            {
                _log($"InstanceConnection: pid {Pid} exited before pipe '{PipeName}' appeared; stopping.");
                return;
            }

            var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await pipe.ConnectAsync(500, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { pipe.Dispose(); return; }
            catch
            {
                pipe.Dispose();
                int delayMs = attempt++ < 20 ? 250 : 1000;
                try { await Task.Delay(delayMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            var writer = new StreamWriter(pipe, new UTF8Encoding(false, true), 8192, leaveOpen: true)
            {
                NewLine = "\n",
                AutoFlush = true,
            };
            var reader = new StreamReader(pipe, Encoding.UTF8, false, 8192, leaveOpen: true);
            var rcts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var loop = Task.Run(() => ReaderLoopAsync(reader, rcts.Token));

            lock (_gate)
            {
                _pipe = pipe; _writer = writer; _reader = reader; _readerCts = rcts; _readerLoop = loop;
            }
            _log($"InstanceConnection: connected to \\\\.\\pipe\\{PipeName}");
            ConnectionChanged?.Invoke(true);
            return;
        }
    }

    private async Task ReaderLoopAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonNode? node;
                try { node = JsonNode.Parse(line); } catch { continue; }
                if (node is not JsonObject obj) continue;

                if (obj["method"] != null && obj["id"] == null)
                {
                    try { NotificationReceived?.Invoke(obj); } catch { }
                    continue;
                }

                if (obj.TryGetPropertyValue("id", out var idNode) && idNode is JsonValue idVal
                    && idVal.TryGetValue<int>(out int id))
                {
                    if (_pending.TryRemove(id, out var tcs))
                    {
                        if (obj.TryGetPropertyValue("error", out var err) && err is JsonObject errObj)
                            tcs.TrySetException(new Exception(errObj["message"]?.GetValue<string>() ?? "pipe error"));
                        else
                            tcs.TrySetResult(obj["result"]?.DeepClone());
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log($"InstanceConnection: reader loop exited: {ex.Message}"); }
        finally { Disconnect(); }
    }

    public void Disconnect()
    {
        NamedPipeClientStream? pipe; StreamWriter? writer; StreamReader? reader;
        CancellationTokenSource? rcts; Task? loop; bool wasConnected;
        lock (_gate)
        {
            pipe = _pipe; writer = _writer; reader = _reader; rcts = _readerCts; loop = _readerLoop;
            wasConnected = pipe != null && pipe.IsConnected;
            _pipe = null; _writer = null; _reader = null; _readerCts = null; _readerLoop = null;
        }
        try { rcts?.Cancel(); } catch { }
        try { loop?.Wait(2000); } catch { }
        try { writer?.Dispose(); } catch { }
        try { reader?.Dispose(); } catch { }
        try { pipe?.Dispose(); } catch { }
        try { rcts?.Dispose(); } catch { }
        foreach (var kv in _pending) kv.Value.TrySetException(new IOException("instance pipe disconnected"));
        _pending.Clear();
        if (wasConnected) ConnectionChanged?.Invoke(false);
    }

    public void Dispose()
    {
        CancellationTokenSource? cts;
        lock (_gate) { cts = _connectCts; _connectCts = null; }
        try { cts?.Cancel(); cts?.Dispose(); } catch { }
        Disconnect();
    }

    private static bool ProcessHasExited(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return p.HasExited; }
        catch (ArgumentException) { return true; }
        catch { return false; }
    }
}
