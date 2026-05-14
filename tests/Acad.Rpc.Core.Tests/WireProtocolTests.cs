using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Acad.Rpc.Core.Tests;

[AcadRpcSurface(Group = "wirefixture")]
public static class WireFixture
{
    [AcadRpcTool, System.ComponentModel.Description("Echo a string with a prefix")]
    public static string Greet(string s) => $"got: {s}";

    [AcadRpcTool, System.ComponentModel.Description("Add two ints")]
    public static int Sum(int a, int b) => a + b;
}

public class WireProtocolTests
{
    [Fact(Timeout = 10000)]
    public async Task EndToEnd_Initialize_ToolsList_ToolsCall_OverNamedPipe()
    {
        AcadRpcHost.ResetForTests();
        var pipeName = "acad-rpc-test-" + Guid.NewGuid().ToString("N");
        var host = AcadRpcHost.Initialize(new AcadRpcHostOptions(pipeName, new FakeDispatcher()));
        host.RegisterAssembly(typeof(WireFixture).Assembly);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await host.StartAsync(cts.Token);

        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(3000, cts.Token);

        using var reader = new StreamReader(client, new UTF8Encoding(false), false, 8192, leaveOpen: true);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), 8192, leaveOpen: true) { NewLine = "\n", AutoFlush = true };

        // initialize
        await writer.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""");
        var initLine = await reader.ReadLineAsync(cts.Token);
        Assert.NotNull(initLine);
        var initResp = JsonNode.Parse(initLine!)!.AsObject();
        Assert.Equal("Acad.Rpc", initResp["result"]!["serverInfo"]!["name"]!.GetValue<string>());

        // tools/list
        await writer.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var listLine = await reader.ReadLineAsync(cts.Token);
        Assert.NotNull(listLine);
        var listResp = JsonNode.Parse(listLine!)!.AsObject();
        var tools = listResp["result"]!["tools"]!.AsArray();
        Assert.True(tools.Count >= 2);

        // tools/call greet
        await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"wirefixture_greet\",\"arguments\":{\"s\":\"hello\"}}}");
        var callLine = await reader.ReadLineAsync(cts.Token);
        Assert.NotNull(callLine);
        var callResp = JsonNode.Parse(callLine!)!.AsObject();
        var content = callResp["result"]!["content"]!.AsArray();
        Assert.Equal("got: hello", content[0]!["text"]!.GetValue<string>());

        // tools/call sum
        await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"wirefixture_sum\",\"arguments\":{\"a\":2,\"b\":3}}}");
        var addLine = await reader.ReadLineAsync(cts.Token);
        var addResp = JsonNode.Parse(addLine!)!.AsObject();
        var addText = addResp["result"]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        Assert.Equal("5", addText);

        await host.ShutdownAsync();
    }
}
