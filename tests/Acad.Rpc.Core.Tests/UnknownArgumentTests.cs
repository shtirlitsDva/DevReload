using System;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace Acad.Rpc.Core.Tests;

/// <summary>
/// An argument the tool has no parameter for must FAIL the call, not be
/// dropped. The binder reads declared parameters out of the argument object
/// and never looks at the rest, so a caller passing activeWorktreePath to a
/// tool without that parameter was answered "updated" while nothing was
/// written — indistinguishable, from the agent's side, from success.
/// </summary>
[AcadRpcSurface(Group = "argfixture")]
public static class ArgFixture
{
    [AcadRpcTool, System.ComponentModel.Description("Patch a thing")]
    public static string Patch(string name, string? prefix = null) =>
        $"patched {name} ({prefix ?? "-"})";

    [AcadRpcTool, System.ComponentModel.Description("Takes nothing")]
    public static string Ping() => "pong";
}

[Collection("AcadRpcHostSingleton")]
public class UnknownArgumentTests
{
    private static RpcCore NewCore()
    {
        AcadRpcHost.ResetForTests();
        var core = new RpcCore(new FakeDispatcher(), "test");
        core.RegisterAssembly(typeof(ArgFixture).Assembly);
        return core;
    }

    private static JsonObject Call(RpcCore core, string tool, JsonObject args)
    {
        var p = new JsonObject { ["name"] = tool, ["arguments"] = args };
        var result = core.DispatchAsync("tools/call", p, default).GetAwaiter().GetResult();
        return result!.AsObject();
    }

    private static string TextOf(JsonObject result) =>
        string.Concat(result["content"]!.AsArray()
            .OfType<JsonObject>()
            .Where(c => c["type"]?.GetValue<string>() == "text")
            .Select(c => c["text"]?.GetValue<string>() ?? ""));

    [Fact]
    public void UnknownArgument_IsRefused_AndNamed()
    {
        var core = NewCore();
        var result = Call(core, "argfixture_patch", new JsonObject
        {
            ["name"] = "NdhPipeline",
            ["activeWorktreePath"] = @"C:\worktrees\wt",
        });

        Assert.True(result["isError"]!.GetValue<bool>());
        string text = TextOf(result);
        Assert.Contains("activeWorktreePath", text, StringComparison.Ordinal);
        // The accepted set is in the message so the agent's next call can be right.
        Assert.Contains("prefix", text, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownArgument_OnParameterlessTool_IsRefused()
    {
        var core = NewCore();
        var result = Call(core, "argfixture_ping", new JsonObject { ["pid"] = 1234 });

        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains("pid", TextOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public void KnownArguments_StillBind()
    {
        var core = NewCore();
        var result = Call(core, "argfixture_patch", new JsonObject
        {
            ["name"] = "NdhPipeline",
            ["prefix"] = "NDHPL",
        });

        Assert.NotEqual(true, result["isError"]?.GetValue<bool>());
        Assert.Contains("patched NdhPipeline (NDHPL)", TextOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public void OmittedOptionalArguments_StillBind()
    {
        var core = NewCore();
        var result = Call(core, "argfixture_patch",
            new JsonObject { ["name"] = "NdhPipeline" });

        Assert.NotEqual(true, result["isError"]?.GetValue<bool>());
        Assert.Contains("patched NdhPipeline (-)", TextOf(result), StringComparison.Ordinal);
    }
}
