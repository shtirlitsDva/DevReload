using System.Collections.Generic;

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

    // Outcome of a BuildService.BuildProjects run: several projects, ONE msbuild
    // invocation. OutputPaths is index-aligned with the projects that were passed
    // in (null where a project produced nothing). FailedProjects names the
    // projects MSBuild attributed an error to - which can be a referenced
    // project (a static lib) rather than one of the projects asked for.
    public sealed record GroupBuildResult(
        bool Success,
        IReadOnlyList<string?> OutputPaths,
        IReadOnlyList<string> FailedProjects,
        int Warnings,
        int Errors,
        string Log);
}
