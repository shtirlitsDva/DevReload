using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Xunit;

namespace Acad.Rpc.Core.Tests;

/// <summary>
/// Surface contract for the DevReload MCP tool layer. Builds the
/// DevReload assembly (via the solution build before tests run) then
/// registers it with a host and asserts every locked-design tool name
/// is discovered with a valid input schema. This is the gate that
/// catches the moment someone deletes an [AcadRpcTool] annotation or
/// renames a method without updating the tool surface.
///
/// Pure metadata exercise — tools are NOT invoked here. The in-AutoCAD
/// integration test (run from the bridge) is the actual runtime
/// coverage. This file is the static contract.
/// </summary>
[Collection("AcadRpcHostSingleton")]
public class DevReloadSurfaceTests
{
    private static readonly string[] ExpectedToolNames = new[]
    {
        // Lifecycle
        "devreload_reload",
        "devreload_load_plugin",
        "devreload_unload_plugin",
        "devreload_unload_all",
        "devreload_unregister",
        // Query — list_plugins is the single rich query; specific lookups
        // are derived by filtering its result on the agent side.
        "devreload_list_plugins",
        "devreload_get_assembly_info",
        "devreload_list_tools",
        // Config
        "devreload_update_build_configuration",
        "devreload_update_active_worktree",
        // Build
        "devreload_build_project",
        // Worktree
        "devreload_list_worktrees",
        // Shared assemblies
        "devreload_read_shared_assemblies",
        "devreload_write_shared_assemblies",
        // Registration
        "devreload_register_new_plugin",
    };

    [Fact]
    public void DevReloadTools_RegistersAllExpectedToolNames()
    {
        var host = NewHost();
        var asm = LoadDevReloadAssembly();
        host.RegisterAssembly(asm);

        var actual = host.ListRegisteredTools()
            .Where(t => t.SourceAssembly == "DevReload")
            .Select(t => t.ToolName)
            .ToHashSet(StringComparer.Ordinal);

        var missing = ExpectedToolNames.Where(n => !actual.Contains(n)).ToList();
        Assert.True(missing.Count == 0,
            $"missing tool(s): {string.Join(", ", missing)}. Got: {string.Join(", ", actual.OrderBy(x => x))}");
    }

    [Fact]
    public void DevReloadTools_ExactlyTheExpectedToolNames_NoExtras()
    {
        var host = NewHost();
        host.RegisterAssembly(LoadDevReloadAssembly());

        var actual = host.ListRegisteredTools()
            .Where(t => t.SourceAssembly == "DevReload")
            .Select(t => t.ToolName)
            .ToHashSet(StringComparer.Ordinal);

        var extras = actual.Where(n => !ExpectedToolNames.Contains(n)).ToList();
        Assert.True(extras.Count == 0,
            $"unexpected tool(s) on the DevReload surface: {string.Join(", ", extras)}. " +
            $"Update ExpectedToolNames in this test if you intended to expose them.");
    }

    [Theory]
    [InlineData("devreload_reload", "name")]
    [InlineData("devreload_load_plugin", "name")]
    [InlineData("devreload_unload_plugin", "name")]
    [InlineData("devreload_unregister", "name")]
    [InlineData("devreload_get_assembly_info", "name")]
    [InlineData("devreload_update_build_configuration", "name", "buildConfiguration")]
    [InlineData("devreload_update_active_worktree", "name", "worktreePath")]
    [InlineData("devreload_build_project", "csprojPath", "buildConfiguration")]
    [InlineData("devreload_list_worktrees", "repoRoot")]
    [InlineData("devreload_read_shared_assemblies", "buildDir")]
    [InlineData("devreload_write_shared_assemblies", "buildDir", "sharedAssemblies", "mixedModeAssemblies", "streamedAssemblies")]
    [InlineData("devreload_register_new_plugin", "projectFilePath", "buildConfiguration", "commandPrefix", "loadOnStartup")]
    public async System.Threading.Tasks.Task DevReloadTool_HasExpectedInputSchemaProperties(string toolName, params string[] expectedProps)
    {
        var host = NewHost();
        host.RegisterAssembly(LoadDevReloadAssembly());

        // Drive through tools/list to read the schema as the agent sees it.
        var listResult = (await host.Core.DispatchAsync("tools/list", null, default))!.AsObject();
        var tools = listResult["tools"]!.AsArray();

        var tool = tools
            .OfType<JsonObject>()
            .FirstOrDefault(t => t["name"]?.GetValue<string>() == toolName);
        Assert.NotNull(tool);

        var schema = tool!["inputSchema"]!.AsObject();
        var props = schema["properties"]!.AsObject();
        foreach (var expected in expectedProps)
        {
            Assert.True(props.ContainsKey(expected),
                $"{toolName} input schema missing property '{expected}'. Got: {string.Join(", ", props.Select(p => p.Key))}");
        }
    }

    [Fact]
    public void DevReloadTools_EveryTool_HasDescription()
    {
        var host = NewHost();
        host.RegisterAssembly(LoadDevReloadAssembly());

        var missing = host.ListRegisteredTools()
            .Where(t => t.SourceAssembly == "DevReload")
            .Where(t => string.IsNullOrWhiteSpace(t.Description))
            .Select(t => t.ToolName)
            .ToList();

        Assert.True(missing.Count == 0,
            $"tools missing description: {string.Join(", ", missing)}");
    }

    // ── helpers ────────────────────────────────────────────────────

    private static Assembly LoadDevReloadAssembly()
    {
        string path = ResolveDevReloadDllPath();
        return Assembly.LoadFrom(path);
    }

    private static string ResolveDevReloadDllPath()
    {
        string testDir = Path.GetDirectoryName(typeof(DevReloadSurfaceTests).Assembly.Location)!;
        string? cursor = testDir;
        while (cursor != null && !File.Exists(Path.Combine(cursor, "DevReload.sln")))
        {
            cursor = Directory.GetParent(cursor)?.FullName;
        }
        if (cursor == null)
            throw new InvalidOperationException(
                "Could not locate DevReload.sln walking up from " + testDir);

        string candidate = Path.Combine(cursor, "src", "Autocad", "DevReload", "bin", "Debug", "DevReload.dll");
        if (!File.Exists(candidate))
            throw new InvalidOperationException(
                $"DevReload.dll not found at {candidate}. Build DevReload (Debug|x64) before running tests.");
        return candidate;
    }

    private static AcadRpcHost NewHost()
    {
        AcadRpcHost.ResetForTests();
        return AcadRpcHost.Initialize(
            new AcadRpcHostOptions(
                "test-pipe-" + Guid.NewGuid().ToString("N"),
                new FakeDispatcher()));
    }
}
