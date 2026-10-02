using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
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
        "devreload_list_configurations",
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
        // In-AutoCAD process/document control (AcadControlTools) — ships in
        // the DevReload assembly, so it is part of this surface. Commands,
        // state, and document tools, served over this instance's pipe.
        "acad_send_command",
        "acad_post_command",
        "acad_get_state",
        "acad_wait_quiescent",
        "acad_open_drawing",
        "acad_new_drawing",
        "acad_close_active_drawing",
        "acad_list_open_documents",
        "acad_activate_document",
        // ObjectARX groups (OarxTools): lifecycle, group, profiles.
        "oarx_list_plugins",
        "oarx_reload",
        "oarx_load_plugin",
        "oarx_unload_plugin",
        "oarx_register_new_plugin",
        "oarx_update_plugin",
        "oarx_unregister",
        "oarx_publish_profile",
        "oarx_activate_profile",
        "oarx_delete_profile",
        "oarx_reload_payload",
        // In-process UI automation (Ui/Tools) — also ships in DevReload.
        "ui_list_windows",
        "ui_list_surfaces",
        "ui_snapshot",
        "ui_invoke",
        "ui_set_value",
        "ui_toggle",
        "ui_select",
        "ui_click",
        "ui_drag",
        "ui_mouse_move",
        "ui_press_key",
        "ui_dialog_buttons",
        "ui_dialog_click",
        "ui_screenshot_window",
        "ui_screenshot_region",
        "ui_screenshot_element",
        "ui_screenshot_wcs_box",
        "ui_canvas_capture_view",
        "ui_canvas_click",
        "ui_canvas_drag",
        "ui_canvas_drag_capture",
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
    [InlineData("devreload_list_configurations", "name")]
    [InlineData("devreload_update_build_configuration", "name", "buildConfiguration")]
    [InlineData("devreload_update_active_worktree", "name", "worktreePath")]
    [InlineData("devreload_build_project", "csprojPath", "buildConfiguration")]
    [InlineData("devreload_list_worktrees", "repoRoot")]
    [InlineData("devreload_read_shared_assemblies", "buildDir")]
    [InlineData("devreload_write_shared_assemblies", "buildDir", "sharedAssemblies", "mixedModeAssemblies", "streamedAssemblies")]
    [InlineData("devreload_register_new_plugin", "projectFilePath", "buildConfiguration", "commandPrefix", "loadOnStartup")]
    // In-AutoCAD acad_* control tools (AcadControlTools, DevReload assembly).
    [InlineData("acad_send_command", "commandString")]
    [InlineData("acad_post_command", "commandString")]
    [InlineData("acad_open_drawing", "path", "readOnly")]
    [InlineData("acad_new_drawing", "templatePath")]
    [InlineData("acad_close_active_drawing", "saveChanges")]
    [InlineData("acad_activate_document", "documentName")]
    // OARX: group fields vs profile fields are separate tools.
    [InlineData("oarx_reload", "name", "modifiedDrawings")]
    [InlineData("oarx_unload_plugin", "name", "modifiedDrawings")]
    [InlineData("oarx_update_plugin", "name", "commandPrefix", "loadOnStartup", "buildConfiguration")]
    [InlineData("oarx_publish_profile", "name", "worktreePath", "profile", "copyFrom", "projectFilePaths",
        "msbuildProperties", "preloadNativeModules", "preloadManagedAssemblies", "postloadManagedAssemblies", "buildFolder", "activate")]
    [InlineData("oarx_activate_profile", "name", "profile")]
    [InlineData("oarx_delete_profile", "name", "profile")]
    [InlineData("oarx_reload_payload", "name", "payloadDir")]
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

    [Theory]
    // The document tools used to answer with a bare word ("created"), which
    // left the caller unable to learn e.g. the new drawing's name.
    [InlineData("acad_open_drawing", "name")]
    [InlineData("acad_new_drawing", "name")]
    [InlineData("acad_activate_document", "name")]
    [InlineData("acad_close_active_drawing", "documentCount")]
    [InlineData("acad_send_command", "isQuiescent")]
    [InlineData("acad_post_command", "documentName")]
    [InlineData("acad_list_open_documents", "items")]
    [InlineData("devreload_list_plugins", "items")]
    // A payload refusal is a normal result the caller branches on.
    [InlineData("oarx_reload_payload", "restartRequired")]
    [InlineData("oarx_reload_payload", "loaded")]
    public async Task DevReloadTool_DeclaresStructuredOutput(string toolName, string expectedProperty)
    {
        var host = NewHost();
        host.RegisterAssembly(LoadDevReloadAssembly());
        var tools = (await host.Core.DispatchAsync("tools/list", null, default))!["tools"]!.AsArray();

        var schema = tools.OfType<JsonObject>()
            .Single(t => t["name"]!.GetValue<string>() == toolName)["outputSchema"]?.AsObject();
        Assert.NotNull(schema);
        Assert.True(schema!["properties"]!.AsObject().ContainsKey(expectedProperty),
            $"{toolName} outputSchema has no '{expectedProperty}'");
    }

    [Theory]
    [InlineData("ui_click")]
    [InlineData("ui_drag")]
    [InlineData("ui_canvas_click")]
    [InlineData("ui_canvas_drag")]
    [InlineData("ui_canvas_drag_capture")]
    public async Task MouseTools_Button_IsAnEnum(string toolName)
    {
        var host = NewHost();
        host.RegisterAssembly(LoadDevReloadAssembly());
        var tools = (await host.Core.DispatchAsync("tools/list", null, default))!["tools"]!.AsArray();

        var button = tools.OfType<JsonObject>()
            .Single(t => t["name"]!.GetValue<string>() == toolName)["inputSchema"]!["properties"]!["button"]!;
        Assert.Equal(new[] { "Left", "Right", "Middle" }, button["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task PressKey_Key_IsAnEnum()
    {
        var host = NewHost();
        host.RegisterAssembly(LoadDevReloadAssembly());
        var tools = (await host.Core.DispatchAsync("tools/list", null, default))!["tools"]!.AsArray();

        var key = tools.OfType<JsonObject>()
            .Single(t => t["name"]!.GetValue<string>() == "ui_press_key")["inputSchema"]!["properties"]!["key"]!;
        Assert.Equal(new[] { "Enter", "Escape", "Tab", "Space", "Yes", "No" },
            key["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task ActionTools_HaveNoSuccessFlag()
    {
        // A refused action is an error result (isError), so a success flag in
        // the payload could only ever say true.
        var host = NewHost();
        host.RegisterAssembly(LoadDevReloadAssembly());
        var tools = (await host.Core.DispatchAsync("tools/list", null, default))!["tools"]!.AsArray();

        var props = tools.OfType<JsonObject>()
            .Single(t => t["name"]!.GetValue<string>() == "ui_dialog_click")["outputSchema"]!["properties"]!.AsObject();
        Assert.Equal(new[] { "message" }, props.Select(p => p.Key));
    }

    [Fact]
    public async Task DevReloadTools_ReadOnlyAnnotation_MatchesTheReadTools()
    {
        var host = NewHost();
        host.RegisterAssembly(LoadDevReloadAssembly());
        var tools = (await host.Core.DispatchAsync("tools/list", null, default))!["tools"]!.AsArray();

        var readOnly = tools.OfType<JsonObject>()
            .Where(t => t["annotations"]!["readOnlyHint"]!.GetValue<bool>())
            .Select(t => t["name"]!.GetValue<string>())
            .ToHashSet();

        // Spot checks both ways: a read tool that is not marked read-only
        // would be refused under a read-only permission mode, and a mutating
        // tool marked read-only would be let through.
        Assert.Contains("acad_get_state", readOnly);
        Assert.Contains("devreload_list_plugins", readOnly);
        Assert.Contains("ui_screenshot_window", readOnly);
        Assert.DoesNotContain("acad_send_command", readOnly);
        Assert.DoesNotContain("devreload_reload", readOnly);
        Assert.DoesNotContain("ui_click", readOnly);
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
