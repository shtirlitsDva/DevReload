namespace DevReload.Core
{
    // Outcome of a BuildService.BuildProject run. Serialized as-is on the
    // AutoCAD MCP tool surface (camelCase via the host's JsonNamingPolicy),
    // so member names are part of the wire contract.
    public sealed record BuildResult(
        bool Success,
        string? OutputPath,
        int Warnings,
        int Errors,
        string Log);
}
