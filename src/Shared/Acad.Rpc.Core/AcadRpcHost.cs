using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Acad.Rpc.Core;

/// <summary>
/// In-AutoCAD MCP server: a process-singleton <see cref="RpcCore"/>
/// wired to a named-pipe transport. Lives in the default ALC so every
/// plugin sees the same <see cref="Current"/> instance.
/// </summary>
public sealed class AcadRpcHost
{
    private static AcadRpcHost? _current;

    public static AcadRpcHost Current => _current ??
        throw new InvalidOperationException(
            "AcadRpcHost.Current accessed before Initialize().");

    public static bool IsInitialized => _current != null;

    public RpcCore Core { get; }
    public IAcadMainThreadDispatcher Dispatcher => Core.MainThreadDispatcher;
    public string ApiVersion => Core.ApiVersion;
    public string PipeName => _options.PipeName;
    public bool IsRunning { get; private set; }

    private readonly AcadRpcHostOptions _options;
    private CancellationTokenSource? _serverLoopCts;
    private Task? _serverLoopTask;

    private readonly object _connectionsGate = new();
    private readonly List<Connection> _connections = new();
    private const int MaxServerInstances = 16;

    /// <summary>One connected client. Multiple agents/bridges can drive
    /// this AutoCAD at once, so writes to a given client's pipe are
    /// serialized by its own lock (a response and a list-changed
    /// broadcast can race otherwise).</summary>
    private sealed class Connection
    {
        public Connection(StreamWriter writer) { Writer = writer; }
        public StreamWriter Writer { get; }
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
    }

    private AcadRpcHost(AcadRpcHostOptions options)
    {
        _options = options;
        Core = new RpcCore(options.MainThreadDispatcher, serverName: "Acad.Rpc", log: options.Log);
        Core.ToolListChanged += TryNotifyListChanged;
    }

    public static AcadRpcHost Initialize(AcadRpcHostOptions options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (_current != null) return _current;
        _current = new AcadRpcHost(options);
        return _current;
    }

    internal static void ResetForTests()
    {
        if (_current != null)
        {
            _current.Core.DisableAutoDiscovery();
            _current.Core.ToolListChanged -= _current.TryNotifyListChanged;
        }
        _current = null;
    }

    // ── Facade over RpcCore ───────────────────────────────────────────

    public void EnableAutoDiscovery() => Core.EnableAutoDiscovery();
    public void RegisterAssembly(Assembly asm) => Core.RegisterAssembly(asm);
    public void UnregisterAssembly(Assembly asm) => Core.UnregisterAssembly(asm);
    public IReadOnlyList<RegisteredToolInfo> ListRegisteredTools() => Core.ListRegisteredTools();

    // ── Server lifecycle ──────────────────────────────────────────────

    public Task StartAsync(CancellationToken ct = default)
    {
        if (IsRunning) return Task.CompletedTask;
        _serverLoopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _serverLoopTask = Task.Run(() => RunServerLoopAsync(_serverLoopCts.Token));
        IsRunning = true;
        return Task.CompletedTask;
    }

    public async Task ShutdownAsync()
    {
        if (!IsRunning) return;
        IsRunning = false;
        try { _serverLoopCts?.Cancel(); } catch { }
        if (_serverLoopTask != null)
        {
            try { await _serverLoopTask.ConfigureAwait(false); }
            catch { }
        }
        _serverLoopCts?.Dispose();
        _serverLoopCts = null;
        _serverLoopTask = null;
    }

    private async Task RunServerLoopAsync(CancellationToken ct)
    {
        // Multi-client accept loop: accept a connection, hand it to a
        // concurrent handler, immediately loop to accept the next. This is
        // what lets several agents/bridges drive ONE AutoCAD at once.
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(
                    pipeName: _options.PipeName,
                    direction: PipeDirection.InOut,
                    maxNumberOfServerInstances: MaxServerInstances,
                    transmissionMode: PipeTransmissionMode.Byte,
                    options: PipeOptions.Asynchronous);
            }
            catch (Exception ex)
            {
                _options.Log?.Invoke($"AcadRpcHost: failed to create pipe server: {ex.GetType().Name}: {ex.Message}");
                try { await Task.Delay(500, ct).ConfigureAwait(false); } catch { return; }
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                try { pipe.Dispose(); } catch { }
                return;
            }
            catch (Exception ex)
            {
                _options.Log?.Invoke($"AcadRpcHost: WaitForConnection failed: {ex.GetType().Name}: {ex.Message}");
                try { pipe.Dispose(); } catch { }
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(pipe, ct), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var reader = new StreamReader(pipe, Encoding.UTF8, false, 8192, leaveOpen: true);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false, true), 8192, leaveOpen: true)
        {
            NewLine = "\n",
            AutoFlush = true,
        };
        var conn = new Connection(writer);
        lock (_connectionsGate) { _connections.Add(conn); }
        try
        {
            await SessionLoopAsync(reader, conn, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _options.Log?.Invoke($"AcadRpcHost: client session ended: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            lock (_connectionsGate) { _connections.Remove(conn); }
            try { writer.Dispose(); } catch { }
            try { reader.Dispose(); } catch { }
            try { pipe.Dispose(); } catch { }
        }
    }

    private async Task SessionLoopAsync(StreamReader reader, Connection conn, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null) return;
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonNode? msg;
            try { msg = JsonNode.Parse(line); }
            catch
            {
                await WriteAsync(conn, McpProtocol.MakeResponse(
                    JsonValue.Create<int?>(null)!,
                    null,
                    McpProtocol.MakeError(McpProtocol.ErrorCodes.ParseError, "invalid JSON")));
                continue;
            }
            if (msg is not JsonObject req) continue;

            JsonNode? id = req["id"];
            string? method = req["method"]?.GetValue<string>();
            JsonObject? @params = req["params"] as JsonObject;

            if (method == null) continue;
            bool isNotification = id == null;

            // Serialize requests within a single client (preserves ordering);
            // concurrency across clients comes from each having its own
            // HandleClientAsync task. The main-thread dispatcher serializes
            // the actual AutoCAD work regardless.
            await DispatchAndReplyAsync(conn, id, method, @params, isNotification, ct).ConfigureAwait(false);
        }
    }

    private async Task DispatchAndReplyAsync(
        Connection conn, JsonNode? id, string method, JsonObject? @params, bool isNotification, CancellationToken ct)
    {
        try
        {
            JsonNode? result = await Core.DispatchAsync(method, @params, ct).ConfigureAwait(false);
            if (!isNotification)
                await WriteAsync(conn, McpProtocol.MakeResponse(id!, result, null));
        }
        catch (NotSupportedException nse)
        {
            if (!isNotification)
                await WriteAsync(conn, McpProtocol.MakeResponse(
                    id!, null, McpProtocol.MakeError(McpProtocol.ErrorCodes.MethodNotFound, nse.Message)));
        }
        catch (Exception ex)
        {
            if (!isNotification)
                await WriteAsync(conn, McpProtocol.MakeResponse(
                    id!, null, McpProtocol.MakeError(McpProtocol.ErrorCodes.InternalError, ex.Message)));
        }
    }

    private static async Task WriteAsync(Connection conn, JsonObject message)
    {
        string line = JsonSerializer.Serialize(message, McpProtocol.JsonOptions);
        await conn.WriteLock.WaitAsync().ConfigureAwait(false);
        try { await conn.Writer.WriteLineAsync(line).ConfigureAwait(false); }
        finally { conn.WriteLock.Release(); }
    }

    private void TryNotifyListChanged()
    {
        Connection[] conns;
        lock (_connectionsGate) { conns = _connections.ToArray(); }
        if (conns.Length == 0) return;

        var notif = McpProtocol.MakeNotification("notifications/tools/list_changed", null);
        foreach (var conn in conns)
            _ = SafeNotifyAsync(conn, notif);
    }

    private async Task SafeNotifyAsync(Connection conn, JsonObject notif)
    {
        try { await WriteAsync(conn, notif).ConfigureAwait(false); } catch { }
    }
}
