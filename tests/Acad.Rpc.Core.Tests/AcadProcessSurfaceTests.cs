using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Xunit;

namespace Acad.Rpc.Core.Tests;

/// <summary>
/// Surface contract for the bridge's AutoCAD process-control tools.
/// Loads the bridge assembly, registers it with a host, and asserts
/// every expected tool name is discovered with the right input
/// schema. Pure metadata exercise; no AutoCAD or COM is touched.
/// </summary>
[Collection("AcadRpcHostSingleton")]
public class AcadProcessSurfaceTests
{
    private static readonly string[] ExpectedToolNames = new[]
    {
        // Discovery
        "acad_list_instances",
        "acad_locate_install",
        // Lifecycle
        "acad_start",
        "acad_attach",
        "acad_detach",
        "acad_wait_pipe",
        "acad_wait_quiescent",
        "acad_quit",
        // State
        "acad_get_state",
        // Commands
        "acad_send_command",
        "acad_post_command",
        // Documents
        "acad_open_drawing",
        "acad_new_drawing",
        "acad_close_active_drawing",
        "acad_list_open_documents",
        "acad_activate_document",
    };

    [Fact]
    public void AcadProcessTools_RegistersAllExpectedToolNames()
    {
        var host = NewHost();
        host.RegisterAssembly(LoadBridgeAssembly());

        var actual = host.ListRegisteredTools()
            .Where(t => t.SourceAssembly == "Acad.Rpc.Bridge")
            .Select(t => t.ToolName)
            .ToHashSet(StringComparer.Ordinal);

        var missing = ExpectedToolNames.Where(n => !actual.Contains(n)).ToList();
        Assert.True(missing.Count == 0,
            $"missing tool(s): {string.Join(", ", missing)}. Got: {string.Join(", ", actual.OrderBy(x => x))}");
    }

    [Fact]
    public void AcadProcessTools_ExactlyTheExpectedToolNames_NoExtras()
    {
        var host = NewHost();
        host.RegisterAssembly(LoadBridgeAssembly());

        var actual = host.ListRegisteredTools()
            .Where(t => t.SourceAssembly == "Acad.Rpc.Bridge")
            .Select(t => t.ToolName)
            .ToHashSet(StringComparer.Ordinal);

        var extras = actual.Where(n => !ExpectedToolNames.Contains(n)).ToList();
        Assert.True(extras.Count == 0,
            $"unexpected acad_* tool(s): {string.Join(", ", extras)}. " +
            $"Update ExpectedToolNames if you intended to expose them.");
    }

    [Theory]
    [InlineData("acad_start", "flavor", "installPath", "profile", "drawingPath", "startupCommands", "visible")]
    [InlineData("acad_attach", "pid")]
    [InlineData("acad_wait_pipe", "pid", "timeoutSeconds")]
    [InlineData("acad_wait_quiescent", "pid", "timeoutSeconds", "requireActiveDocument")]
    [InlineData("acad_quit", "pid", "saveChanges", "timeoutSeconds")]
    [InlineData("acad_get_state", "pid")]
    [InlineData("acad_send_command", "commandString", "pid")]
    [InlineData("acad_post_command", "commandString", "pid")]
    [InlineData("acad_open_drawing", "path", "readOnly", "pid")]
    [InlineData("acad_new_drawing", "templatePath", "pid")]
    [InlineData("acad_close_active_drawing", "saveChanges", "pid")]
    [InlineData("acad_list_open_documents", "pid")]
    [InlineData("acad_activate_document", "documentId", "pid")]
    public async System.Threading.Tasks.Task AcadProcessTool_HasExpectedInputSchemaProperties(string toolName, params string[] expectedProps)
    {
        var host = NewHost();
        host.RegisterAssembly(LoadBridgeAssembly());

        var listResult = (await host.Core.DispatchAsync("tools/list", null, default))!.AsObject();
        var tools = listResult["tools"]!.AsArray();

        var tool = tools.OfType<JsonObject>()
            .FirstOrDefault(t => t["name"]?.GetValue<string>() == toolName);
        Assert.NotNull(tool);

        var schema = tool!["inputSchema"]!.AsObject();
        var props = schema["properties"]!.AsObject();
        foreach (var expected in expectedProps)
        {
            Assert.True(props.ContainsKey(expected),
                $"{toolName} input schema missing property '{expected}'. " +
                $"Got: {string.Join(", ", props.Select(p => p.Key))}");
        }
    }

    [Fact]
    public void AcadProcessTools_EveryTool_HasDescription()
    {
        var host = NewHost();
        host.RegisterAssembly(LoadBridgeAssembly());

        var missing = host.ListRegisteredTools()
            .Where(t => t.SourceAssembly == "Acad.Rpc.Bridge")
            .Where(t => string.IsNullOrWhiteSpace(t.Description))
            .Select(t => t.ToolName)
            .ToList();

        Assert.True(missing.Count == 0,
            $"acad_* tools missing description: {string.Join(", ", missing)}");
    }

    // ── helpers ────────────────────────────────────────────────────

    private static Assembly LoadBridgeAssembly()
    {
        // Use the type-anchored reference rather than file probing —
        // the test project has a ProjectReference to the bridge so the
        // DLL is already in the test runner's load context.
        return typeof(Acad.Rpc.Bridge.AcadProcessTools).Assembly;
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
