using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Acad.Rpc.Core;

/// <summary>
/// Transport-agnostic MCP RPC engine. Owns the tool registry,
/// attribute scanning, auto-discovery, and JSON-RPC dispatch. Both
/// in-AutoCAD (named pipe) and bridge (stdio) hosts compose one of
/// these and bolt a transport on top.
/// </summary>
public sealed class RpcCore
{
    public string ApiVersion { get; } = "1.0.0";
    public IAcadMainThreadDispatcher MainThreadDispatcher { get; }

    /// <summary>Fires when the registry changes (tool added or removed).
    /// Transports subscribe to push <c>notifications/tools/list_changed</c>
    /// to their connected clients.</summary>
    public event Action? ToolListChanged;

    private readonly object _gate = new();
    private readonly Dictionary<Assembly, List<RegisteredTool>> _toolsByAssembly = new();
    private readonly Dictionary<string, RegisteredTool> _toolsByName =
        new(StringComparer.Ordinal);
    private readonly Action<string>? _log;
    private readonly string _serverName;

    private bool _autoDiscoveryEnabled;

    public RpcCore(IAcadMainThreadDispatcher mainThreadDispatcher, string serverName, Action<string>? log = null)
    {
        MainThreadDispatcher = mainThreadDispatcher ??
            throw new ArgumentNullException(nameof(mainThreadDispatcher));
        _serverName = serverName ?? throw new ArgumentNullException(nameof(serverName));
        _log = log;
    }

    // ── Auto-discovery ────────────────────────────────────────────────
    //
    // Opt-in. When enabled, auto-registers any assembly in any
    // NON-COLLECTIBLE AssemblyLoadContext that carries an
    // [AcadRpcSurface]. Collectible ALCs are skipped — they belong to a
    // hot-reload loader (DevReload) that owns register/unregister
    // explicitly.

    public void EnableAutoDiscovery()
    {
        lock (_gate)
        {
            if (_autoDiscoveryEnabled) return;
            _autoDiscoveryEnabled = true;
        }
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoaded;
        RunAutoDiscoverySweep();
    }

    /// <summary>Detach the AppDomain hook. Used by tests + by host
    /// shutdown paths so a recycled core doesn't leave a dangling
    /// subscription.</summary>
    public void DisableAutoDiscovery()
    {
        lock (_gate)
        {
            if (!_autoDiscoveryEnabled) return;
            _autoDiscoveryEnabled = false;
        }
        AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoaded;
    }

    internal void RunAutoDiscoverySweep()
    {
        foreach (var alc in AssemblyLoadContext.All)
        {
            if (alc.IsCollectible) continue;
            foreach (var asm in alc.Assemblies.ToArray())
                TryAutoRegister(asm);
        }
    }

    private void OnAssemblyLoaded(object? sender, AssemblyLoadEventArgs args)
    {
        var asm = args.LoadedAssembly;
        var alc = AssemblyLoadContext.GetLoadContext(asm);
        if (alc != null && alc.IsCollectible) return;
        TryAutoRegister(asm);
    }

    private void TryAutoRegister(Assembly asm)
    {
        if (asm == null) return;
        lock (_gate) { if (_toolsByAssembly.ContainsKey(asm)) return; }
        bool hasSurface;
        try
        {
            hasSurface = SafeGetTypes(asm)
                .Any(t => t.GetCustomAttribute<AcadRpcSurfaceAttribute>() != null);
        }
        catch { return; }
        if (hasSurface) RegisterAssembly(asm);
    }

    // ── Registry surface ──────────────────────────────────────────────

    public void RegisterAssembly(Assembly asm)
    {
        if (asm == null) throw new ArgumentNullException(nameof(asm));
        bool changed = false;
        var collisions = new List<(string Name, string ExistingAsm, string IncomingAsm)>();
        lock (_gate)
        {
            if (_toolsByAssembly.ContainsKey(asm)) return;
            var tools = ScanAssembly(asm);
            _toolsByAssembly[asm] = tools;
            foreach (var t in tools)
            {
                if (_toolsByName.TryAdd(t.Name, t))
                {
                    changed = true;
                }
                else
                {
                    var existing = _toolsByName[t.Name];
                    collisions.Add((t.Name, existing.SourceAssembly, t.SourceAssembly));
                }
            }
        }
        foreach (var (name, existing, incoming) in collisions)
        {
            _log?.Invoke(
                $"RpcCore: duplicate tool name '{name}' from assembly '{incoming}' shadowed; " +
                $"first-wins, the version from '{existing}' stays active. " +
                $"Resolve by setting [AcadRpcTool(Name = \"...\")] or [AcadRpcSurface(Group = \"...\")] on one side.");
        }
        if (changed) ToolListChanged?.Invoke();
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
        if (changed) ToolListChanged?.Invoke();
    }

    public IReadOnlyList<RegisteredToolInfo> ListRegisteredTools()
    {
        lock (_gate)
        {
            return _toolsByName.Values
                .Select(t => new RegisteredToolInfo(t.Name, t.SourceAssembly, t.Description))
                .ToList();
        }
    }

    // ── JSON-RPC dispatch ─────────────────────────────────────────────

    /// <summary>
    /// Dispatch a parsed MCP request. Transports call this and forward
    /// the result; they own framing, transport-level errors, and
    /// notification fan-out.
    /// </summary>
    public async Task<JsonNode?> DispatchAsync(string method, JsonObject? @params, CancellationToken ct)
    {
        switch (method)
        {
            case "initialize":
                return McpProtocol.InitializeResult(
                    serverName: _serverName,
                    serverVersion: ApiVersion,
                    protocolVersion: McpProtocol.NegotiateVersion(
                        @params?["protocolVersion"]?.GetValue<string>()));

            case "notifications/initialized":
                return null;

            case "ping":
                return new JsonObject();

            case "tools/list":
            {
                // Catches non-collectible ALCs that appeared after the
                // initial sweep — e.g. an NSLOAD loader that creates a
                // new isolated context per plugin.
                if (_autoDiscoveryEnabled) RunAutoDiscoverySweep();

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
                    var r = await InvokeToolAsync(tool, args, ct).ConfigureAwait(false);
                    // Fast path keeps the exact pre-image-support shape for the
                    // common text / text+structured returns; only image-bearing
                    // results take the general assembler.
                    if (r.Images == null || r.Images.Count == 0)
                    {
                        return r.Structured != null
                            ? McpProtocol.CallToolResultStructured(r.Text ?? "", r.Structured)
                            : McpProtocol.CallToolResultText(r.Text ?? "", isError: r.IsError);
                    }
                    return McpProtocol.CallToolResult(r.Text, r.Structured, r.Images, r.IsError);
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

    /// <summary>Normalized tool invocation result. Text + optional
    /// structured object preserve the original behavior; Images is populated
    /// only when a tool opts into <see cref="ToolImage"/> / <see cref="ToolResult"/>.</summary>
    private readonly record struct InvocationResult(
        string? Text, JsonObject? Structured, IReadOnlyList<ToolImage>? Images, bool IsError);

    private async Task<InvocationResult> InvokeToolAsync(
        RegisteredTool tool, JsonObject args, CancellationToken ct)
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

        // DoNotWrapExceptions surfaces the tool's own exception directly
        // (without TargetInvocationException's wrapper layer), so the
        // catch in DispatchAsync sees the original message and type.
        object? invokeResult;
        try
        {
            invokeResult = tool.RequiresMainThread
                ? await MainThreadDispatcher
                    .InvokeAsync(() => tool.Method.Invoke(null, BindingFlags.DoNotWrapExceptions, null, bound, null), ct)
                    .ConfigureAwait(false)
                : tool.Method.Invoke(null, BindingFlags.DoNotWrapExceptions, null, bound, null);
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            // Defensive: a future change to RequiresMainThread plumbing
            // could re-introduce wrapping. Unwrap regardless.
            throw tie.InnerException;
        }

        if (invokeResult is Task task)
        {
            await task.ConfigureAwait(false);
            var resultProp = task.GetType().GetProperty("Result");
            if (resultProp != null && task.GetType().IsGenericType)
                invokeResult = resultProp.GetValue(task);
            else invokeResult = null;
        }

        // Spec: structuredContent must be a JSON *object*, so arrays and
        // primitives travel as text only. ToolImage / ToolResult are the
        // opt-in shapes that attach image content blocks.
        return invokeResult switch
        {
            null => new InvocationResult("", null, null, false),
            string s => new InvocationResult(s, null, null, false),
            ToolImage img => new InvocationResult(null, null, new[] { img }, false),
            IReadOnlyList<ToolImage> imgs => new InvocationResult(null, null, imgs, false),
            IEnumerable<ToolImage> imgs => new InvocationResult(null, null, imgs.ToList(), false),
            ToolResult tr => MaterializeToolResult(tr),
            _ => Materialize(invokeResult),
        };

        static InvocationResult Materialize(object value)
        {
            JsonNode? node = JsonSerializer.SerializeToNode(value, McpProtocol.JsonOptions);
            string text = node?.ToJsonString(McpProtocol.JsonOptions) ?? "";
            return new InvocationResult(text, node as JsonObject, null, false);
        }

        static InvocationResult MaterializeToolResult(ToolResult tr)
        {
            JsonObject? structured = null;
            if (tr.Structured != null)
                structured = JsonSerializer.SerializeToNode(
                    tr.Structured, McpProtocol.JsonOptions) as JsonObject;

            // Match the bare-object convention: when no explicit Text is given
            // but a structured object is, the text block is that object's JSON.
            string? text = tr.Text;
            if (text == null && structured != null)
                text = structured.ToJsonString(McpProtocol.JsonOptions);

            return new InvocationResult(text, structured, tr.Images, tr.IsError);
        }
    }

    /// <summary>Test-only entry that exercises tool invocation without
    /// the protocol envelope. Mirrors <see cref="DispatchAsync"/>'s
    /// tools/call branch but returns the raw string result.</summary>
    internal async Task<string> InvokeToolForTestAsync(
        string toolName, JsonObject args, CancellationToken ct)
    {
        RegisteredTool? tool;
        lock (_gate) { _toolsByName.TryGetValue(toolName, out tool); }
        if (tool == null) throw new InvalidOperationException($"unknown tool: {toolName}");
        return (await InvokeToolAsync(tool, args, ct).ConfigureAwait(false)).Text ?? "";
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
                    .GetCustomAttribute<DescriptionAttribute>()
                    ?.Description;

                var descriptor = new JsonObject
                {
                    ["name"] = toolName,
                    ["inputSchema"] = JsonSchemaBuilder.Build(method),
                };
                if (!string.IsNullOrEmpty(description)) descriptor["description"] = description;

                bool requiresMainThread =
                    method.GetCustomAttribute<RunOnAcadMainThreadAttribute>() != null;

                result.Add(new RegisteredTool(
                    Name: toolName,
                    SourceAssembly: asmName,
                    Description: description,
                    Method: method,
                    Descriptor: descriptor,
                    RequiresMainThread: requiresMainThread));
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

    internal sealed record RegisteredTool(
        string Name,
        string SourceAssembly,
        string? Description,
        MethodInfo Method,
        JsonObject Descriptor,
        bool RequiresMainThread);
}
