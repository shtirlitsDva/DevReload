using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace Acad.Rpc.Core.Tests;

/// <summary>
/// [RpcRequires] names a state key; the HOST says what the key means. The engine
/// enforces it in one place and generates the requirement clause the agent reads
/// from the same source, so a tool cannot advertise one precondition and enforce
/// another.
/// </summary>
[AcadRpcSurface(Group = "statefixture")]
public static class StateFixture
{
    [AcadRpcTool, RpcRequires("document"), System.ComponentModel.Description("Do a thing")]
    public static string NeedsDocument() => "did it";

    [AcadRpcTool, RpcRequires("nosuchkey")]
    public static string NeedsUnimplemented() => "did it";

    [AcadRpcTool]
    public static string NeedsNothing() => "did it";
}

[Collection("AcadRpcHostSingleton")]
public class RequiredStateTests
{
    private static RpcCore NewCore(Func<string?> documentCheck)
    {
        AcadRpcHost.ResetForTests();
        var checks = new Dictionary<string, RpcStateCheck>(StringComparer.Ordinal)
        {
            ["document"] = new RpcStateCheck("current drawing", documentCheck),
        };
        var core = new RpcCore(new FakeDispatcher(), "test", null, checks);
        core.RegisterAssembly(typeof(StateFixture).Assembly);
        return core;
    }

    private static JsonObject Call(RpcCore core, string tool)
    {
        var p = new JsonObject { ["name"] = tool, ["arguments"] = new JsonObject() };
        return core.DispatchAsync("tools/call", p, default).GetAwaiter().GetResult()!.AsObject();
    }

    private static string TextOf(JsonObject result) =>
        string.Concat(result["content"]!.AsArray()
            .OfType<JsonObject>()
            .Where(c => c["type"]?.GetValue<string>() == "text")
            .Select(c => c["text"]?.GetValue<string>() ?? ""));

    [Fact]
    public void UnmetState_RefusesWithTheHostsMessage()
    {
        var core = NewCore(() => "No current drawing. Open one first.");
        var result = Call(core, "statefixture_needs_document");

        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains("No current drawing.", TextOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public void MetState_Runs()
    {
        var core = NewCore(() => null);
        var result = Call(core, "statefixture_needs_document");

        Assert.NotEqual(true, result["isError"]?.GetValue<bool>());
        Assert.Contains("did it", TextOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public void ToolWithoutRequirement_IsUnaffected()
    {
        var core = NewCore(() => "would refuse");
        var result = Call(core, "statefixture_needs_nothing");

        Assert.NotEqual(true, result["isError"]?.GetValue<bool>());
        Assert.Contains("did it", TextOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public void KeyThisHostDoesNotImplement_FailsLoudly()
    {
        var core = NewCore(() => null);
        var result = Call(core, "statefixture_needs_unimplemented");

        Assert.True(result["isError"]!.GetValue<bool>());
        string text = TextOf(result);
        Assert.Contains("nosuchkey", text, StringComparison.Ordinal);
        Assert.Contains("StateChecks", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Description_CarriesTheRequirement()
    {
        var core = NewCore(() => null);
        var tool = core.ListRegisteredTools()
            .Single(t => t.ToolName == "statefixture_needs_document");

        Assert.Equal("Do a thing Requires: current drawing.", tool.Description);
    }
}
