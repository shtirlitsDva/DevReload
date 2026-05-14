namespace Acad.Rpc.Core;

/// <summary>
/// Public-facing descriptor of a registered tool. Returned from
/// <see cref="AcadRpcHost.ListRegisteredTools"/>. The framework's own
/// view of registered surface area, intended for diagnostic tools and
/// for the step-2 supervisor's introspection.
/// </summary>
public sealed record RegisteredToolInfo(
    string ToolName,
    string SourceAssembly,
    string? Description);
