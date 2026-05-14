using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Acad.Rpc.Core;

/// <summary>
/// In-AutoCAD MCP server host. One per process. Loaded into the
/// default ALC (via DevReload's shared assembly mechanism) so every
/// plugin sees the same <see cref="Current"/> instance.
/// </summary>
public sealed class AcadRpcHost
{
    private static AcadRpcHost? _current;

    public static AcadRpcHost Current => _current ??
        throw new InvalidOperationException(
            "AcadRpcHost.Current accessed before Initialize().");

    public static bool IsInitialized => _current != null;

    public string ApiVersion { get; } = "1.0.0";
    public IAcadMainThreadDispatcher Dispatcher => _options.MainThreadDispatcher;
    public string PipeName => _options.PipeName;
    public bool IsRunning { get; private set; }

    private readonly object _gate = new();
    private readonly Dictionary<Assembly, List<RegisteredTool>> _toolsByAssembly = new();
    private readonly Dictionary<string, RegisteredTool> _toolsByName = new(StringComparer.Ordinal);
    private readonly AcadRpcHostOptions _options;
    private CancellationTokenSource? _serverLoopCts;
    private Task? _serverLoopTask;

    /// <summary>Active client connection's outbound writer, if any. Used
    /// to push tools/list_changed notifications when registry mutates
    /// while a client is attached. null when no client is connected.</summary>
    private StreamWriter? _activeWriter;
    private readonly object _writerGate = new();

    private AcadRpcHost(AcadRpcHostOptions options) { _options = options; }

    public static AcadRpcHost Initialize(AcadRpcHostOptions options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (_current != null) return _current;
        _current = new AcadRpcHost(options);
        return _current;
    }

    internal static void ResetForTests() => _current = null;

    // ── Registry surface ──────────────────────────────────────────────

    public void RegisterAssembly(Assembly asm)
    {
        if (asm == null) throw new ArgumentNullException(nameof(asm));
        bool changed = false;
        lock (_gate)
        {
            if (_toolsByAssembly.ContainsKey(asm)) return;
            var tools = ScanAssembly(asm);
            _toolsByAssembly[asm] = tools;
            foreach (var t in tools)
            {
                if (_toolsByName.TryAdd(t.Name, t)) changed = true;
            }
        }
        if (changed) TryNotifyListChanged();
    }

    public void UnregisterAssembly(Assembly asm)
    {
        if (asm == null) throw new ArgumentNullException(nameof(asm));
        bool changed = false;
        lock (_gate)
        {
            if (!_toolsByAssembly.TryGetValue(asm, out var tools)) return;
            foreach (var t in tools)
            {
                if (_toolsByName.Remove(t.Name)) changed = true;
            }
            _toolsByAssembly.Remove(asm);
        }
        if (changed) TryNotifyListChanged();
    }

    public IReadOnlyList<RegisteredToolInfo> ListRegisteredTools()
    {
        lock (_gate)
        {
            return _toolsByAssembly.Values
                .SelectMany(v => v)
                .Select(t => new RegisteredToolInfo(t.Name, t.SourceAssembly, t.Description))
                .ToList();
        }
    }

    public static bool MethodRequiresMainThread(MethodInfo mi)
    {
        if (mi == null) throw new ArgumentNullException(nameof(mi));
        return mi.GetCustomAttribute<RunOnAcadMainThreadAttribute>() != null;
    }

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
            catch { /* client disconnected / IO error; loop and accept next */ }
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
            if (line == null) return; // EOF
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

            if (method == null)
            {
                // Responses to server-sent requests would land here; we
                // don't send any so ignore.
                continue;
            }

            // Notifications (no id): one-way, no response.
            bool isNotification = id == null;

            try
            {
                JsonNode? result = await DispatchAsync(method, @params, ct).ConfigureAwait(false);
                if (!isNotification)
                {
                    await WriteAsync(writer, McpProtocol.MakeResponse(id!, result, null));
                }
            }
            catch (NotSupportedException nse)
            {
                if (!isNotification)
                {
                    await WriteAsync(writer, McpProtocol.MakeResponse(
                        id!, null,
                        McpProtocol.MakeError(McpProtocol.ErrorCodes.MethodNotFound, nse.Message)));
                }
            }
            catch (Exception ex)
            {
                if (!isNotification)
                {
                    await WriteAsync(writer, McpProtocol.MakeResponse(
                        id!, null,
                        McpProtocol.MakeError(McpProtocol.ErrorCodes.InternalError, ex.Message)));
                }
            }
        }
    }

    private async Task<JsonNode?> DispatchAsync(string method, JsonObject? @params, CancellationToken ct)
    {
        switch (method)
        {
            case "initialize":
                return McpProtocol.InitializeResult(serverName: "Acad.Rpc", serverVersion: ApiVersion);

            case "notifications/initialized":
                return null;

            case "ping":
                return new JsonObject();

            case "tools/list":
            {
                List<JsonObject> tools;
                lock (_gate)
                {
                    tools = _toolsByName.Values
                        .OrderBy(t => t.Name, StringComparer.Ordinal)
                        .Select(t => (JsonObject)t.Descriptor.DeepClone())
                        .ToList();
                }
                return McpProtocol.ToolsListResult(tools);
            }

            case "tools/call":
            {
                string? toolName = @params?["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(toolName))
                    return McpProtocol.CallToolResultText("missing tool name", isError: true);

                RegisteredTool? tool;
                lock (_gate) { _toolsByName.TryGetValue(toolName!, out tool); }
                if (tool == null)
                    return McpProtocol.CallToolResultText($"unknown tool: {toolName}", isError: true);

                JsonObject args = (@params?["arguments"] as JsonObject) ?? new JsonObject();
                try
                {
                    var resultText = await InvokeToolAsync(tool, args, ct).ConfigureAwait(false);
                    return McpProtocol.CallToolResultText(resultText, isError: false);
                }
                catch (Exception ex)
                {
                    return McpProtocol.CallToolResultText($"{ex.GetType().Name}: {ex.Message}", isError: true);
                }
            }

            default:
                throw new NotSupportedException($"method not supported: {method}");
        }
    }

    private async Task<string> InvokeToolAsync(RegisteredTool tool, JsonObject args, CancellationToken ct)
    {
        var parameters = tool.Method.GetParameters();
        var bound = new object?[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            if (p.ParameterType == typeof(CancellationToken)) { bound[i] = ct; continue; }

            if (args.TryGetPropertyValue(p.Name!, out var node) && node != null)
            {
                bound[i] = JsonSerializer.Deserialize(node, p.ParameterType, McpProtocol.JsonOptions);
            }
            else if (p.HasDefaultValue) bound[i] = p.DefaultValue;
            else if (p.ParameterType.IsValueType) bound[i] = Activator.CreateInstance(p.ParameterType);
            else bound[i] = null;
        }

        object? invokeResult = tool.Method.Invoke(null, bound);

        // Handle Task / Task<T> / ValueTask<T>
        if (invokeResult is Task task)
        {
            await task.ConfigureAwait(false);
            var resultProp = task.GetType().GetProperty("Result");
            if (resultProp != null && task.GetType().IsGenericType)
                invokeResult = resultProp.GetValue(task);
            else invokeResult = null;
        }

        return invokeResult switch
        {
            null => "",
            string s => s,
            _ => JsonSerializer.Serialize(invokeResult, McpProtocol.JsonOptions),
        };
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
            // Fire-and-forget — list_changed is advisory; failure means
            // the connection is gone and the loop will recycle.
            _ = writer.WriteLineAsync(line);
        }
        catch { }
    }

    // ── Attribute scan ────────────────────────────────────────────────

    private List<RegisteredTool> ScanAssembly(Assembly asm)
    {
        var asmName = asm.GetName().Name ?? "unknown";
        var defaultPrefix = asmName.ToLowerInvariant().Replace('.', '_');
        var result = new List<RegisteredTool>();

        foreach (var type in SafeGetTypes(asm))
        {
            var surface = type.GetCustomAttribute<AcadRpcSurfaceAttribute>();
            if (surface == null) continue;
            var prefix = surface.Group ?? defaultPrefix;

            const BindingFlags flags =
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;

            foreach (var method in type.GetMethods(flags))
            {
                var toolAttr = method.GetCustomAttribute<AcadRpcToolAttribute>();
                if (toolAttr == null) continue;

                string toolName = !string.IsNullOrEmpty(toolAttr.Name)
                    ? toolAttr.Name!
                    : prefix + "_" + ToSnakeCase(method.Name);

                string? description = method
                    .GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()
                    ?.Description;

                var descriptor = new JsonObject
                {
                    ["name"] = toolName,
                    ["inputSchema"] = JsonSchemaBuilder.Build(method),
                };
                if (!string.IsNullOrEmpty(description)) descriptor["description"] = description;

                result.Add(new RegisteredTool(
                    Name: toolName,
                    SourceAssembly: asmName,
                    Description: description,
                    Method: method,
                    Descriptor: descriptor));
            }
        }

        return result;
    }

    private static Type[] SafeGetTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null).Select(t => t!).ToArray();
        }
    }

    private static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]))
                sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private sealed record RegisteredTool(
        string Name,
        string SourceAssembly,
        string? Description,
        MethodInfo Method,
        JsonObject Descriptor);
}
