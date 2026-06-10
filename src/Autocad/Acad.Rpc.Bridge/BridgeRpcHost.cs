using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using Acad.Rpc.Core;

namespace Acad.Rpc.Bridge;

/// <summary>
/// MCP server on the agent-facing side of the wire. Composes:
///   - <see cref="RpcCore"/> for the local tool catalogue (process control)
///   - stdin/stdout line-delimited JSON-RPC
///   - <see cref="PipeForwarder"/> for unknown-locally tool calls that
///     should reach the in-AutoCAD pipe.
///
/// Locally-handled methods: initialize, notifications/initialized,
/// ping, tools/list (merged with remote), tools/call (routed by name).
///
/// Inbound notifications from the pipe (notifications/tools/list_changed)
/// are forwarded to stdout and trigger an internal merged-list refresh.
/// </summary>
public sealed class BridgeRpcHost : IDisposable
{
    private readonly RpcCore _core;
    private readonly PipeForwarder _forwarder;
    private readonly Action<string> _log;
    private StreamWriter? _stdout;
    private readonly object _stdoutGate = new();
    private readonly HashSet<string> _localToolNames = new(StringComparer.Ordinal);
    private readonly object _localNamesGate = new();

    public BridgeRpcHost(RpcCore core, PipeForwarder forwarder, Action<string>? log = null)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _forwarder = forwarder ?? throw new ArgumentNullException(nameof(forwarder));
        _log = log ?? (_ => { });
        _core.ToolListChanged += OnLocalListChanged;
        _forwarder.ConnectionChanged += OnForwarderConnectionChanged;
        _forwarder.NotificationReceived += OnForwarderNotification;
        RebuildLocalNameCache();
    }

    public async Task RunAsync(Stream stdin, Stream stdout, CancellationToken ct)
    {
        using var reader = new StreamReader(stdin, Encoding.UTF8, false, 8192, leaveOpen: true);
        var writer = new StreamWriter(stdout, new UTF8Encoding(false, true), 8192, leaveOpen: true)
        {
            NewLine = "\n",
            AutoFlush = true,
        };
        lock (_stdoutGate) { _stdout = writer; }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) return;
                if (string.IsNullOrWhiteSpace(line)) continue;

                // Each request gets its own task — concurrent calls won't
                // block each other, but writes are serialized via the
                // stdout lock.
                _ = HandleLineAsync(line, ct);
            }
        }
        finally
        {
            lock (_stdoutGate) { _stdout = null; }
            try { writer.Dispose(); } catch { }
        }
    }

    private async Task HandleLineAsync(string line, CancellationToken ct)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(line); }
        catch
        {
            await WriteAsync(McpProtocol.MakeResponse(
                JsonValue.Create<int?>(null)!, null,
                McpProtocol.MakeError(McpProtocol.ErrorCodes.ParseError, "invalid JSON")));
            return;
        }
        if (msg is not JsonObject req) return;

        JsonNode? id = req["id"];
        string? method = req["method"]?.GetValue<string>();
        JsonObject? @params = req["params"] as JsonObject;

        if (method == null) return;
        bool isNotification = id == null;

        try
        {
            JsonNode? result = await DispatchAsync(method, @params, ct).ConfigureAwait(false);
            if (!isNotification)
                await WriteAsync(McpProtocol.MakeResponse(id!, result, null));
        }
        catch (NotSupportedException nse)
        {
            if (!isNotification)
                await WriteAsync(McpProtocol.MakeResponse(
                    id!, null,
                    McpProtocol.MakeError(McpProtocol.ErrorCodes.MethodNotFound, nse.Message)));
        }
        catch (Exception ex)
        {
            if (!isNotification)
                await WriteAsync(McpProtocol.MakeResponse(
                    id!, null,
                    McpProtocol.MakeError(McpProtocol.ErrorCodes.InternalError, ex.Message)));
        }
    }

    private async Task<JsonNode?> DispatchAsync(string method, JsonObject? @params, CancellationToken ct)
    {
        switch (method)
        {
            case "initialize":
                return McpProtocol.InitializeResult(
                    serverName: "acad-agent",
                    serverVersion: _core.ApiVersion,
                    protocolVersion: McpProtocol.NegotiateVersion(
                        @params?["protocolVersion"]?.GetValue<string>()));

            case "notifications/initialized":
                return null;

            case "ping":
                return new JsonObject();

            case "tools/list":
                return await MergedToolsListAsync(ct).ConfigureAwait(false);

            case "tools/call":
            {
                string? toolName = @params?["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(toolName))
                    return McpProtocol.CallToolResultText("missing tool name", isError: true);

                bool isLocal;
                lock (_localNamesGate) { isLocal = _localToolNames.Contains(toolName!); }

                if (isLocal) return await _core.DispatchAsync(method, @params, ct).ConfigureAwait(false);

                if (!_forwarder.IsConnected)
                    return McpProtocol.CallToolResultText(
                        $"cannot run '{toolName}': {_forwarder.DescribeNotConnected()}",
                        isError: true);

                try
                {
                    var pipeResult = await _forwarder.ForwardRequestAsync(method, @params, ct)
                        .ConfigureAwait(false);
                    return pipeResult;
                }
                catch (Exception ex)
                {
                    return McpProtocol.CallToolResultText(
                        $"forwarding to AutoCAD pipe failed: {ex.Message}", isError: true);
                }
            }

            default:
                throw new NotSupportedException($"method not supported: {method}");
        }
    }

    private async Task<JsonNode?> MergedToolsListAsync(CancellationToken ct)
    {
        // Local list — always available.
        var localResult = await _core.DispatchAsync("tools/list", null, ct).ConfigureAwait(false);
        var localTools = (localResult as JsonObject)?["tools"] as JsonArray ?? new JsonArray();

        var merged = new List<JsonObject>(localTools.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in localTools)
        {
            if (t is JsonObject obj && obj["name"]?.GetValue<string>() is string name)
            {
                merged.Add((JsonObject)obj.DeepClone());
                seen.Add(name);
            }
        }

        // Remote list — only when the forwarder is connected.
        if (_forwarder.IsConnected)
        {
            try
            {
                var remoteResult = await _forwarder.ForwardRequestAsync("tools/list", null, ct)
                    .ConfigureAwait(false);
                if (remoteResult is JsonObject rObj && rObj["tools"] is JsonArray rArr)
                {
                    foreach (var t in rArr)
                    {
                        if (t is JsonObject obj && obj["name"]?.GetValue<string>() is string name)
                        {
                            if (seen.Add(name)) merged.Add((JsonObject)obj.DeepClone());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"BridgeRpcHost: remote tools/list failed: {ex.Message}");
            }
        }

        return McpProtocol.ToolsListResult(merged);
    }

    private async Task WriteAsync(JsonObject message)
    {
        StreamWriter? writer;
        lock (_stdoutGate) { writer = _stdout; }
        if (writer == null) return;
        string line = JsonSerializer.Serialize(message, McpProtocol.JsonOptions);
        try
        {
            // Single-writer discipline — stdout is a shared resource and
            // concurrent unawaited writes would interleave bytes mid-line.
            await Task.Run(() => { lock (_stdoutGate) writer.WriteLine(line); })
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log($"BridgeRpcHost: stdout write failed: {ex.Message}");
        }
    }

    private void OnForwarderConnectionChanged(bool connected)
    {
        _log($"BridgeRpcHost: forwarder connection -> {connected}");
        _ = WriteAsync(McpProtocol.MakeNotification("notifications/tools/list_changed", null));
    }

    private void OnForwarderNotification(JsonObject notif)
    {
        // Most useful in practice: forward tools/list_changed so the
        // agent re-fetches the merged catalogue. Other notifications
        // from the pipe are bubbled up too — they're opaque to us.
        _ = WriteAsync(notif);
    }

    private void OnLocalListChanged()
    {
        RebuildLocalNameCache();
        _ = WriteAsync(McpProtocol.MakeNotification("notifications/tools/list_changed", null));
    }

    private void RebuildLocalNameCache()
    {
        var names = _core.ListRegisteredTools().Select(t => t.ToolName).ToList();
        lock (_localNamesGate)
        {
            _localToolNames.Clear();
            foreach (var n in names) _localToolNames.Add(n);
        }
    }

    public void Dispose()
    {
        _core.ToolListChanged -= OnLocalListChanged;
        _forwarder.ConnectionChanged -= OnForwarderConnectionChanged;
        _forwarder.NotificationReceived -= OnForwarderNotification;
    }
}
