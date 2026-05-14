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

    private StreamWriter? _activeWriter;
    private readonly object _writerGate = new();

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
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    pipeName: _options.PipeName,
                    direction: PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    transmissionMode: PipeTransmissionMode.Byte,
                    options: PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 8192, leaveOpen: true);
                var writer = new StreamWriter(pipe, new UTF8Encoding(false, true), 8192, leaveOpen: true)
                {
                    NewLine = "\n",
                    AutoFlush = true,
                };

                lock (_writerGate) { _activeWriter = writer; }
                try
                {
                    await SessionLoopAsync(reader, writer, ct).ConfigureAwait(false);
                }
                finally
                {
                    lock (_writerGate) { _activeWriter = null; }
                    try { writer.Dispose(); } catch { }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _options.Log?.Invoke($"AcadRpcHost: server loop iteration failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                try { pipe?.Dispose(); } catch { }
            }
        }
    }

    private async Task SessionLoopAsync(StreamReader reader, StreamWriter writer, CancellationToken ct)
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
                await WriteAsync(writer, McpProtocol.MakeResponse(
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

            try
            {
                JsonNode? result = await Core.DispatchAsync(method, @params, ct).ConfigureAwait(false);
                if (!isNotification)
                    await WriteAsync(writer, McpProtocol.MakeResponse(id!, result, null));
            }
            catch (NotSupportedException nse)
            {
                if (!isNotification)
                    await WriteAsync(writer, McpProtocol.MakeResponse(
                        id!, null,
                        McpProtocol.MakeError(McpProtocol.ErrorCodes.MethodNotFound, nse.Message)));
            }
            catch (Exception ex)
            {
                if (!isNotification)
                    await WriteAsync(writer, McpProtocol.MakeResponse(
                        id!, null,
                        McpProtocol.MakeError(McpProtocol.ErrorCodes.InternalError, ex.Message)));
            }
        }
    }

    private static async Task WriteAsync(StreamWriter writer, JsonObject message)
    {
        string line = JsonSerializer.Serialize(message, McpProtocol.JsonOptions);
        await writer.WriteLineAsync(line).ConfigureAwait(false);
    }

    private void TryNotifyListChanged()
    {
        StreamWriter? writer;
        lock (_writerGate) { writer = _activeWriter; }
        if (writer == null) return;

        try
        {
            var notif = McpProtocol.MakeNotification("notifications/tools/list_changed", null);
            string line = JsonSerializer.Serialize(notif, McpProtocol.JsonOptions);
            _ = writer.WriteLineAsync(line);
        }
        catch { }
    }
}
