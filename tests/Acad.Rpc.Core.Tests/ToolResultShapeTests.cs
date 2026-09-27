using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace Acad.Rpc.Core.Tests;

[AcadRpcSurface(Group = "shapefixture")]
public static class ShapeFixture
{
    public sealed record Item(string Name);

    [AcadRpcTool(Effect = ToolEffect.ReadOnly)]
    public static IReadOnlyList<Item> Items() => new[] { new Item("a"), new Item("b") };

    [AcadRpcTool(Effect = ToolEffect.ReadOnly)]
    public static ToolResult StructuredError() => new()
    {
        IsError = true,
        Structured = new { reason = "refused" },
    };

    [AcadRpcTool(Effect = ToolEffect.ReadOnly)]
    public static ToolResult StructuredList() => new()
    {
        Structured = new[] { new Item("x") },
    };

    public enum Mode { Idle, Busy }

    public sealed record ModeReport(Mode Mode);

    [AcadRpcTool(Effect = ToolEffect.ReadOnly)]
    public static ModeReport RoundTrip(Mode mode) => new(mode);

    [AcadRpcTool(Effect = ToolEffect.ReadOnly)]
    public static ModeReport Defaulted(Mode mode = Mode.Busy) => new(mode);
}

/// <summary>
/// structuredContent must be a JSON object (MCP spec), and Claude Code shows
/// the model only structuredContent when it is present (anthropics/claude-code
/// #45575). So every structured result must reach structuredContent, and the
/// text block must carry the same data, never something extra.
/// </summary>
[Collection("AcadRpcHostSingleton")]
public class ToolResultShapeTests
{
    private static JsonObject Call(string tool, JsonObject? arguments = null)
    {
        AcadRpcHost.ResetForTests();
        var core = new RpcCore(new FakeDispatcher(), "test");
        core.RegisterAssembly(typeof(ShapeFixture).Assembly);
        var p = new JsonObject { ["name"] = tool, ["arguments"] = arguments ?? new JsonObject() };
        return core.DispatchAsync("tools/call", p, default).GetAwaiter().GetResult()!.AsObject();
    }

    [Theory]
    [InlineData("Busy")]
    [InlineData("busy")] // names are matched case-insensitively
    public void Enum_TravelsByName_BothWays(string sent)
    {
        var r = Call("shapefixture_round_trip", new JsonObject { ["mode"] = sent });
        Assert.False(r["isError"]!.GetValue<bool>());
        Assert.Equal("Busy", r["structuredContent"]!["mode"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(1)]  // a valid ordinal: still refused, the number is not the contract
    [InlineData(99)] // no such member: would otherwise bind as (Mode)99
    public void Enum_IntegerArgument_IsRefused(int sent)
    {
        var r = Call("shapefixture_round_trip", new JsonObject { ["mode"] = sent });
        Assert.True(r["isError"]!.GetValue<bool>());
    }

    [Fact]
    public void EnumParameter_OmittedArgument_BindsTheCSharpDefault()
    {
        var r = Call("shapefixture_defaulted");
        Assert.False(r["isError"]!.GetValue<bool>());
        Assert.Equal("Busy", r["structuredContent"]!["mode"]!.GetValue<string>());
    }

    [Fact]
    public async System.Threading.Tasks.Task EnumParameter_InputSchemaListsTheNames()
    {
        AcadRpcHost.ResetForTests();
        var core = new RpcCore(new FakeDispatcher(), "test");
        core.RegisterAssembly(typeof(ShapeFixture).Assembly);
        var tool = (await core.DispatchAsync("tools/list", null, default))!["tools"]!
            .AsArray().OfType<JsonObject>().Single(t => t["name"]!.GetValue<string>() == "shapefixture_defaulted");

        var mode = tool["inputSchema"]!["properties"]!["mode"]!;
        Assert.Equal(new[] { "Idle", "Busy" }, mode["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Null(tool["inputSchema"]!["required"]);
    }

    [Fact]
    public void Enum_UnknownName_IsRefused()
    {
        var r = Call("shapefixture_round_trip", new JsonObject { ["mode"] = "Sleeping" });
        Assert.True(r["isError"]!.GetValue<bool>());
    }

    [Fact]
    public void ListReturn_IsWrappedAsItems_InStructuredContentAndText()
    {
        var r = Call("shapefixture_items");

        var items = r["structuredContent"]!["items"]!.AsArray();
        Assert.Equal(new[] { "a", "b" }, items.Select(i => i!["name"]!.GetValue<string>()));

        var text = r["content"]!.AsArray().Single()!["text"]!.GetValue<string>();
        Assert.Equal(r["structuredContent"]!.ToJsonString(), JsonNode.Parse(text)!.ToJsonString());
    }

    [Fact]
    public void ToolResultWithListStructured_IsWrappedAsItems()
    {
        var r = Call("shapefixture_structured_list");
        Assert.Equal("x", r["structuredContent"]!["items"]![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void StructuredErrorResult_KeepsIsErrorTrue()
    {
        var r = Call("shapefixture_structured_error");
        Assert.True(r["isError"]!.GetValue<bool>());
        Assert.Equal("refused", r["structuredContent"]!["reason"]!.GetValue<string>());
    }
}
