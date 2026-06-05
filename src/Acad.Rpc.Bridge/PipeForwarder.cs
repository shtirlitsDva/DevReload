using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using Acad.Process;

namespace Acad.Rpc.Bridge;

/// <summary>
/// JSON-RPC client to the in-AutoCAD named pipe. Connects lazily once
/// a binding sets a pipe name; auto-reconnects when the binding
/// changes. Owns request-id correlation so the bridge can multiplex
/// concurrent <c>tools/call</c> forwards over one duplex pipe.
///
/// One forwarder = one bridge process. Switching the bound pid closes
/// the previous connection and opens a new one against the new pipe
/// name.
/// </summary>
public sealed class PipeForwarder : IDisposable
{
    private readonly AcadInstanceBinding _binding;
    private readonly Action<string> _log;

    private readonly object _gate = new();
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private CancellationTokenSource? _readerCts;
    private Task? _readerLoop;
    private string? _connectedPipeName;
    private int _nextId;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();

    /// <summary>Fires when the pipe (re)connects or disconnects so the
    /// bridge can refresh its merged tools/list view and push
    /// notifications/tools/list_changed to the agent.</summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>Forwarded inbound notification from the pipe — bridge
    /// pushes these to its own stdout.</summary>
    public event Action<JsonObject>? NotificationReceived;

    public bool IsConnected
    {
        get { lock (_gate) return _pipe != null && _pipe.IsConnected; }
    }

    public string? ConnectedPipeName
    {
        get { lock (_gate) return _connectedPipeName; }
    }

    /// <summary>
    /// Plain-language reason this forwarder can't reach an in-AutoCAD
    /// pipe right now, written for the agent that just had a
    /// <c>devreload_*</c> / plugin tool call fail. Distinguishes the
    /// three states the agent must act on differently:
    /// <list type="bullet">
    ///   <item>nothing bound → launch your own AutoCAD;</item>
    ///   <item>bound but no pipe → DevReload isn't loaded there;</item>
    ///   <item>bound, pipe present, but we're not connected → the pipe is
    ///   already held by ANOTHER bridge (single-connection only) → start
    ///   your own instance instead of sharing this one.</item>
    /// </list>
    /// The in-AutoCAD pipe accepts exactly one client, so two agents
    /// cannot share one AutoCAD — each must drive its own.
    /// </summary>
    public string DescribeNotConnected()
    {
        var bound = _binding.Current;
        if (bound == null)
            return "no AutoCAD is bound to this bridge. Each agent drives its OWN AutoCAD — " +
                   "launch one with acad_start (it auto-binds and can NETLOAD DevReload at boot " +
                   "via startupCommands), then acad_wait_pipe.";

        if (!NamedPipeProbe.Exists(bound.PipeName))
            return $"bridge is bound to AutoCAD pid {bound.Pid}, but its DevReload pipe " +
                   $"('{bound.PipeName}') is not up — DevReload isn't loaded in that instance. " +
                   "NETLOAD it, or acad_quit then acad_start a clean instance of your own.";

        // Pipe name is present yet we (a forwarder that retries on bind)
        // are not connected: the single server slot is held by another
        // bridge. This is the "shared AutoCAD" anti-pattern.
        return $"AutoCAD pid {bound.Pid}'s DevReload pipe ('{bound.PipeName}') is already held by " +
               "ANOTHER bridge — the in-AutoCAD pipe accepts a single connection at a time, so two " +
               "agents cannot share one AutoCAD. Start your OWN instance with acad_start instead of " +
               "this one. (If you just launched it, wait a moment and retry — the connection may " +
               "still be coming up.)";
    }

    public PipeForwarder(AcadInstanceBinding binding, Action<string>? log = null)
    {
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
        _log = log ?? (_ => { });
        _binding.Changed += OnBindingChanged;
    }

    /// <summary>
    /// Send a JSON-RPC request to the pipe and await the response. The
    /// bridge translates its own ID assignment so we don't collide with
    /// the agent's IDs.
    /// </summary>
    public async Task<JsonNode?> ForwardRequestAsync(string method, JsonObject? @params, CancellationToken ct)
    {
        if (!IsConnected)
            throw new InvalidOperationException("pipe forwarder is not connected");

        int id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        // DeepClone — the caller still owns @params (it may sit inside
        // the agent's original request). JsonNode enforces one-parent;
        // attaching the same node twice throws "The node already has a parent."
        if (@params != null) msg["params"] = @params.DeepClone();

        StreamWriter? writer;
        lock (_gate) { writer = _writer; }
        if (writer == null)
        {
            _pending.TryRemove(id, out _);
            throw new InvalidOperationException("pipe forwarder writer is gone");
        }

        try
        {
            string line = JsonSerializer.Serialize(msg);
            await writer.WriteLineAsync(line).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _pending.TryRemove(id, out _);
            throw new IOException("failed to write to pipe", ex);
        }

        await using var registration = ct.Register(() => tcs.TrySetCanceled(ct));
        try { return await tcs.Task.ConfigureAwait(false); }
        finally { _pending.TryRemove(id, out _); }
    }

    public void Dispose()
    {
        _binding.Changed -= OnBindingChanged;
        Disconnect();
    }

    private void OnBindingChanged(BoundInstance? bound)
    {
        if (bound == null) { Disconnect(); return; }
        _ = Task.Run(() => TryConnectAsync(bound.PipeName, CancellationToken.None));
    }

    public async Task TryConnectAsync(string pipeName, CancellationToken ct)
    {
        Disconnect();

        // The in-AutoCAD pipe takes a moment to come up after AutoCAD
        // starts. Caller policy decides the retry budget; we poll every
        // 250ms up to 60s here as a sane default — connect ETA is not
        // typically dominated by pipe-availability latency.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        Exception? lastEx = null;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var pipe = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(500, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lastEx = ex;
                pipe.Dispose();
                try { await Task.Delay(250, ct).ConfigureAwait(false); } catch { return; }
                continue;
            }

            var writer = new StreamWriter(pipe, new UTF8Encoding(false, true), 8192, leaveOpen: true)
            {
                NewLine = "\n",
                AutoFlush = true,
            };
            var reader = new StreamReader(pipe, Encoding.UTF8, false, 8192, leaveOpen: true);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var readerLoop = Task.Run(() => ReaderLoopAsync(reader, cts.Token));

            lock (_gate)
            {
                _pipe = pipe;
                _writer = writer;
                _reader = reader;
                _readerCts = cts;
                _readerLoop = readerLoop;
                _connectedPipeName = pipeName;
            }

            _log($"PipeForwarder: connected to \\\\.\\pipe\\{pipeName}");
            ConnectionChanged?.Invoke(true);
            return;
        }

        _log($"PipeForwarder: timed out connecting to \\\\.\\pipe\\{pipeName}: {lastEx?.Message}");
    }

    public void Disconnect()
    {
        NamedPipeClientStream? pipe;
        StreamWriter? writer;
        StreamReader? reader;
        CancellationTokenSource? cts;
        Task? loop;
        bool wasConnected;

        lock (_gate)
        {
            pipe = _pipe;
            writer = _writer;
            reader = _reader;
            cts = _readerCts;
            loop = _readerLoop;
            wasConnected = pipe != null && pipe.IsConnected;
            _pipe = null;
            _writer = null;
            _reader = null;
            _readerCts = null;
            _readerLoop = null;
            _connectedPipeName = null;
        }

        try { cts?.Cancel(); } catch { }
        try { loop?.Wait(2000); } catch { }
        try { writer?.Dispose(); } catch { }
        try { reader?.Dispose(); } catch { }
        try { pipe?.Dispose(); } catch { }
        try { cts?.Dispose(); } catch { }

        foreach (var kv in _pending)
            kv.Value.TrySetException(new IOException("pipe forwarder disconnected"));
        _pending.Clear();

        if (wasConnected) ConnectionChanged?.Invoke(false);
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
                try { node = JsonNode.Parse(line); }
                catch { continue; }
                if (node is not JsonObject obj) continue;

                // Inbound notification?
                if (obj["method"] != null && obj["id"] == null)
                {
                    try { NotificationReceived?.Invoke(obj); } catch { }
                    continue;
                }

                // Response to a prior request?
                if (obj.TryGetPropertyValue("id", out var idNode) && idNode is JsonValue idVal
                    && idVal.TryGetValue<int>(out int id))
                {
                    if (_pending.TryRemove(id, out var tcs))
                    {
                        if (obj.TryGetPropertyValue("error", out var err) && err is JsonObject errObj)
                        {
                            string msg = errObj["message"]?.GetValue<string>() ?? "unknown pipe error";
                            tcs.TrySetException(new Exception(msg));
                        }
                        else
                        {
                            tcs.TrySetResult(obj["result"]?.DeepClone());
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log($"PipeForwarder: reader loop exited: {ex.Message}"); }
        finally
        {
            // Mark connection dead if not already.
            bool wasConnected;
            lock (_gate) { wasConnected = _pipe != null && _pipe.IsConnected; }
            if (!wasConnected) Disconnect();
            else ConnectionChanged?.Invoke(false);
        }
    }
}
