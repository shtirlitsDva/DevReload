using DevReload.Core;

namespace DevReload.Rpc
{
    // Records returned by the DevReload MCP tool surface. They serialize
    // to plain JSON objects via System.Text.Json's default record support
    // (positional records → camelCase properties under JsonNamingPolicy.Web).
    // The agent sees structured data, not flat strings, so it can branch
    // on fields like Success or filter by Loaded without parsing prose.

    public sealed record PluginInfo(
        string Name,
        bool Loaded,
        string BuildConfiguration,
        string DllPath,
        string ProjectFilePath,
        string? ActiveWorktreePath,
        int CommandCount);

    public sealed record PluginAssemblyInfo(
        string PluginName,
        bool Loaded,
        string? AssemblyName,
        string? Version,
        string? Location,
        string? LastWriteUtc);

    // Single shape for any "act on one plugin" tool (load / dev_reload /
    // unload / unregister). Success is true if the post-condition holds;
    // CommandCount, Loaded and Message describe what happened.
    // Build is the optional build outcome — populated for load/reload
    // tools that triggered a `dotnet build`. If the build failed, Build
    // carries the full log so the agent can read errors without a
    // second tool call.
    public sealed record PluginActionResult(
        string PluginName,
        bool Success,
        int CommandCount,
        bool Loaded,
        string Message,
        BuildResult? Build = null);

    public sealed record UnloadAllResult(
        int Total,
        int UnloadedNow,
        System.Collections.Generic.IReadOnlyList<string> PluginNames);

    public sealed record ToolListEntry(
        string SourceAssembly,
        string ToolName,
        string? Description);

    public sealed record SharedAssembliesConfig(
        System.Collections.Generic.IReadOnlyList<string> SharedAssemblies,
        System.Collections.Generic.IReadOnlyList<string> MixedModeAssemblies,
        System.Collections.Generic.IReadOnlyList<string> StreamedAssemblies);

    public sealed record RegisterPluginResult(
        bool Success,
        string Name,
        string Message);
}
