using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Acad.Rpc.Core.Tests;

/// <summary>
/// Fixture for the tools/list descriptor contract that Anthropic's mcp-builder
/// guide asks for: tool annotations, an outputSchema for structured results,
/// and input schemas that describe arrays as arrays.
/// </summary>
[AcadRpcSurface(Group = "descfixture")]
public static class DescriptorFixture
{
    public enum Shade { Light, Dark }

    public sealed record Leaf(string Label, int? Weight);

    public sealed record Report(
        string Name,
        bool Enabled,
        double Ratio,
        Shade Shade,
        string? Note,
        IReadOnlyList<string> Tags,
        Leaf Child,
        Dictionary<string, int> Counts);

    [AcadRpcTool(Effect = ToolEffect.ReadOnly)]
    public static Report Read() => throw new NotSupportedException();

    [AcadRpcTool(Effect = ToolEffect.ReadOnly)]
    public static Task<Report> ReadAsync() => throw new NotSupportedException();

    [AcadRpcTool(Effect = ToolEffect.ReadOnly)]
    public static IReadOnlyList<Leaf> ListLeaves() => throw new NotSupportedException();

    [AcadRpcTool(Effect = ToolEffect.Additive)]
    public static string Append(string[] values, List<int> counts) => "";

    [AcadRpcTool(Effect = ToolEffect.Additive, Idempotent = true)]
    public static int Bind() => 0;

    [AcadRpcTool(Effect = ToolEffect.Destructive)]
    public static ToolResult Wipe() => new();
}

[Collection("AcadRpcHostSingleton")]
public class ToolDescriptorTests
{
    private static JsonObject Descriptor(string tool)
    {
        AcadRpcHost.ResetForTests();
        var core = new RpcCore(new FakeDispatcher(), "test");
        core.RegisterAssembly(typeof(DescriptorFixture).Assembly);
        var list = core.DispatchAsync("tools/list", null, default).GetAwaiter().GetResult()!;
        return list["tools"]!.AsArray().OfType<JsonObject>()
            .Single(t => t["name"]!.GetValue<string>() == tool);
    }

    // ── Annotations ──────────────────────────────────────────────────

    [Fact]
    public void ReadOnlyTool_AnnotatedReadOnlyAndClosedWorld()
    {
        var a = Descriptor("descfixture_read")["annotations"]!.AsObject();
        Assert.True(a["readOnlyHint"]!.GetValue<bool>());
        Assert.False(a["openWorldHint"]!.GetValue<bool>());
        // Meaningful only when readOnlyHint is false, so not emitted.
        Assert.Null(a["destructiveHint"]);
        Assert.Null(a["idempotentHint"]);
    }

    [Fact]
    public void AdditiveTool_NotReadOnlyNotDestructive()
    {
        var a = Descriptor("descfixture_append")["annotations"]!.AsObject();
        Assert.False(a["readOnlyHint"]!.GetValue<bool>());
        Assert.False(a["destructiveHint"]!.GetValue<bool>());
        Assert.False(a["idempotentHint"]!.GetValue<bool>());
        Assert.False(a["openWorldHint"]!.GetValue<bool>());
    }

    [Fact]
    public void IdempotentFlag_IsEmitted()
    {
        var a = Descriptor("descfixture_bind")["annotations"]!.AsObject();
        Assert.True(a["idempotentHint"]!.GetValue<bool>());
    }

    [Fact]
    public void DestructiveTool_AnnotatedDestructive()
    {
        var a = Descriptor("descfixture_wipe")["annotations"]!.AsObject();
        Assert.False(a["readOnlyHint"]!.GetValue<bool>());
        Assert.True(a["destructiveHint"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolWithoutEffect_RefusesToRegister_AndNamesTheTool()
    {
        var asm = BuildAssemblyWithUndeclaredEffect();
        AcadRpcHost.ResetForTests();
        var core = new RpcCore(new FakeDispatcher(), "test");

        var ex = Assert.Throws<InvalidOperationException>(() => core.RegisterAssembly(asm));
        Assert.Contains("undeclared_tool", ex.Message);
        Assert.Contains("Effect", ex.Message);
        Assert.Empty(core.ListRegisteredTools());
    }

    // ── outputSchema ─────────────────────────────────────────────────

    [Fact]
    public void RecordReturn_OutputSchemaDescribesTheWireShape()
    {
        var s = Descriptor("descfixture_read")["outputSchema"]!.AsObject();
        Assert.Equal("object", s["type"]!.GetValue<string>());
        var p = s["properties"]!.AsObject();

        // camelCase, same naming policy as the wire.
        Assert.Equal("string", p["name"]!["type"]!.GetValue<string>());
        Assert.Equal("boolean", p["enabled"]!["type"]!.GetValue<string>());
        Assert.Equal("number", p["ratio"]!["type"]!.GetValue<string>());
        // Enums travel by member name, and the schema lists the names.
        Assert.Equal("string", p["shade"]!["type"]!.GetValue<string>());
        Assert.Equal(new[] { "Light", "Dark" },
            p["shade"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("string", p["note"]!["type"]!.GetValue<string>());
        Assert.Equal("array", p["tags"]!["type"]!.GetValue<string>());
        Assert.Equal("string", p["tags"]!["items"]!["type"]!.GetValue<string>());
        Assert.Equal("object", p["child"]!["type"]!.GetValue<string>());
        Assert.Equal("string", p["child"]!["properties"]!["label"]!["type"]!.GetValue<string>());
        Assert.Equal("object", p["counts"]!["type"]!.GetValue<string>());
        Assert.Equal("integer", p["counts"]!["additionalProperties"]!["type"]!.GetValue<string>());

        // Null members are omitted on the wire (WhenWritingNull), so only
        // non-nullable members are required.
        var required = s["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToHashSet();
        Assert.Contains("name", required);
        Assert.Contains("tags", required);
        Assert.DoesNotContain("note", required);
        var childRequired = p["child"]!["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "label" }, childRequired);
    }

    [Fact]
    public void TaskOfRecord_OutputSchemaIsTheRecord()
    {
        var s = Descriptor("descfixture_read_async")["outputSchema"]!.AsObject();
        Assert.Equal("string", s["properties"]!["name"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void ListReturn_OutputSchemaIsTheItemsWrapper()
    {
        var s = Descriptor("descfixture_list_leaves")["outputSchema"]!.AsObject();
        Assert.Equal("object", s["type"]!.GetValue<string>());
        var items = s["properties"]!["items"]!.AsObject();
        Assert.Equal("array", items["type"]!.GetValue<string>());
        Assert.Equal("object", items["items"]!["type"]!.GetValue<string>());
        Assert.Equal(new[] { "items" }, s["required"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Theory]
    [InlineData("descfixture_append")] // string
    [InlineData("descfixture_bind")]   // primitive
    [InlineData("descfixture_wipe")]   // ToolResult: shape unknown until run time
    public void NonStructuredReturn_HasNoOutputSchema(string tool)
    {
        Assert.Null(Descriptor(tool)["outputSchema"]);
    }

    // ── inputSchema ──────────────────────────────────────────────────

    [Fact]
    public void ArrayParameters_AreDescribedAsArrays()
    {
        var p = Descriptor("descfixture_append")["inputSchema"]!["properties"]!.AsObject();
        Assert.Equal("array", p["values"]!["type"]!.GetValue<string>());
        Assert.Equal("string", p["values"]!["items"]!["type"]!.GetValue<string>());
        Assert.Equal("array", p["counts"]!["type"]!.GetValue<string>());
        Assert.Equal("integer", p["counts"]!["items"]!["type"]!.GetValue<string>());
    }

    // A tool without Effect cannot live in this test assembly: every test
    // registers the whole assembly, so one bad fixture would fail them all.
    private static Assembly BuildAssemblyWithUndeclaredEffect()
    {
        var ab = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("UndeclaredEffectFixture"), AssemblyBuilderAccess.Run);
        var mb = ab.DefineDynamicModule("m");
        var tb = mb.DefineType("Surface",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        tb.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(AcadRpcSurfaceAttribute).GetConstructor(Type.EmptyTypes)!,
            Array.Empty<object>()));

        var method = tb.DefineMethod("Tool",
            MethodAttributes.Public | MethodAttributes.Static, typeof(string), Type.EmptyTypes);
        method.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(AcadRpcToolAttribute).GetConstructor(Type.EmptyTypes)!,
            Array.Empty<object>(),
            new[] { typeof(AcadRpcToolAttribute).GetProperty(nameof(AcadRpcToolAttribute.Name))! },
            new object[] { "undeclared_tool" }));
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        tb.CreateType();
        return ab;
    }
}
