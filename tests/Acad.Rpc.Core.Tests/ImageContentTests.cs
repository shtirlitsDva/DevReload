using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Nodes;
using Xunit;

namespace Acad.Rpc.Core.Tests;

/// <summary>
/// Contract for image-bearing tool results (added so tools like UiMCP's
/// screenshot surface can return PNGs inline instead of only a file path).
/// A tool opts in by returning <see cref="ToolImage"/>, an array of them,
/// or a <see cref="ToolResult"/> that combines text/structured/images.
/// Plain string / object returns must be byte-for-byte unchanged.
/// </summary>
[AcadRpcSurface(Group = "imgfixture")]
public static class ImageFixture
{
    // 1x1 transparent PNG, base64.
    public const string OnePxPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+M8AAAMBAQDJ/pLvAAAAAElFTkSuQmCC";

    [AcadRpcTool, System.ComponentModel.Description("Return a single image")]
    public static ToolImage Shot() => new(OnePxPng, "image/png");

    [AcadRpcTool, System.ComponentModel.Description("Return several image frames")]
    public static ToolImage[] Burst() =>
        new[] { new ToolImage(OnePxPng), new ToolImage(OnePxPng) };

    [AcadRpcTool, System.ComponentModel.Description("Return text + structured + image together")]
    public static ToolResult Combined() => new()
    {
        Text = "captured",
        Structured = new { width = 1, height = 1, path = "C:\\tmp\\f.png" },
        Images = new[] { new ToolImage(OnePxPng) },
    };
}

[Collection("AcadRpcHostSingleton")]
public class ImageContentTests
{
    private static RpcCore NewCore()
    {
        AcadRpcHost.ResetForTests();
        var core = new RpcCore(new FakeDispatcher(), "test");
        core.RegisterAssembly(typeof(ImageFixture).Assembly);
        return core;
    }

    private static JsonObject Call(RpcCore core, string tool)
    {
        var p = new JsonObject { ["name"] = tool, ["arguments"] = new JsonObject() };
        var result = core.DispatchAsync("tools/call", p, default).GetAwaiter().GetResult();
        return result!.AsObject();
    }

    [Fact]
    public void ToolImage_ProducesImageContentBlock()
    {
        var core = NewCore();
        var content = Call(core, "imgfixture_shot")["content"]!.AsArray();

        var img = content.OfType<JsonObject>().Single(c => c["type"]?.GetValue<string>() == "image");
        Assert.Equal("image/png", img["mimeType"]!.GetValue<string>());
        Assert.Equal(ImageFixture.OnePxPng, img["data"]!.GetValue<string>());
    }

    [Fact]
    public void ToolImageArray_ProducesMultipleImageBlocks()
    {
        var core = NewCore();
        var content = Call(core, "imgfixture_burst")["content"]!.AsArray();

        var imgs = content.OfType<JsonObject>().Where(c => c["type"]?.GetValue<string>() == "image").ToList();
        Assert.Equal(2, imgs.Count);
    }

    [Fact]
    public void ToolResult_CombinesTextStructuredAndImage()
    {
        var core = NewCore();
        var result = Call(core, "imgfixture_combined");
        var content = result["content"]!.AsArray();

        var text = content.OfType<JsonObject>().First(c => c["type"]?.GetValue<string>() == "text");
        Assert.Equal("captured", text["text"]!.GetValue<string>());

        var img = content.OfType<JsonObject>().Single(c => c["type"]?.GetValue<string>() == "image");
        Assert.Equal(ImageFixture.OnePxPng, img["data"]!.GetValue<string>());

        var structured = result["structuredContent"]!.AsObject();
        Assert.Equal(1, structured["width"]!.GetValue<int>());
    }

    [Fact]
    public void PlainStringReturn_StaysTextOnly_NoImageBlock()
    {
        var core = NewCore();
        core.RegisterAssembly(typeof(WireFixture).Assembly);
        var content = Call(core, "wirefixture_greet" /* arguments empty → s defaults null */)["content"]!.AsArray();

        Assert.DoesNotContain(content.OfType<JsonObject>(),
            c => c["type"]?.GetValue<string>() == "image");
    }
}
