using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Acad.Rpc.Bridge;
using Xunit;

namespace Acad.Rpc.Core.Tests;

// A host whose main thread is busy for good (a command at a prompt, a modal
// dialog) must not jam the pipe: other calls keep working, and a cancelled
// call leaves the main-thread queue instead of running later for nobody.
[Collection("AcadRpcHostSingleton")]
public class PipeJamTests
{
    [Fact(Timeout = 15000)]
    public async Task Server_StuckMainThreadCall_DoesNotBlockOtherCalls_AndCancelReachesIt()
    {
        var (host, pipeName, dispatcher, cts) = await StartHostAsync();
        string mainTool = MainThreadToolName(host);

        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(3000, cts.Token);
        using var reader = new StreamReader(client, new UTF8Encoding(false), false, 8192, leaveOpen: true);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), 8192, leaveOpen: true) { NewLine = "\n", AutoFlush = true };

        // 1: waits on the main thread, which never comes free.
        await writer.WriteLineAsync($"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{{\"name\":\"{mainTool}\",\"arguments\":{{}}}}}}");
        await dispatcher.Queued.Task.WaitAsync(cts.Token);

        // 2: off-thread, must answer while 1 is still stuck.
        await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"wirefixture_greet\",\"arguments\":{\"s\":\"hi\"}}}");
        var reply = JsonNode.Parse((await reader.ReadLineAsync(cts.Token))!)!.AsObject();
        Assert.Equal(2, reply["id"]!.GetValue<int>());
        Assert.Equal("got: hi", reply["result"]!["content"]![0]!["text"]!.GetValue<string>());

        // Cancel 1: the dispatcher sees its token cancelled, and 1 gets no
        // reply (MCP). The next line on the wire is the reply to 3.
        await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":1,\"reason\":\"test\"}}");
        await dispatcher.Cancelled.Task.WaitAsync(cts.Token);
        await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"ping\"}");
        var next = JsonNode.Parse((await reader.ReadLineAsync(cts.Token))!)!.AsObject();
        Assert.Equal(3, next["id"]!.GetValue<int>());

        await host.ShutdownAsync();
    }

    [Fact(Timeout = 15000)]
    public async Task Bridge_ConcurrentCalls_AllAnswer_AndCallerCancelReachesInstance()
    {
        var (host, pipeName, dispatcher, cts) = await StartHostAsync();
        string mainTool = MainThreadToolName(host);

        using var conn = new InstanceConnection(Environment.ProcessId, pipeName);
        conn.Start();
        Assert.True(await conn.WaitConnectedAsync(TimeSpan.FromSeconds(5), cts.Token));

        // A call stuck on the main thread, cancelled by its caller later.
        using var callerCts = new CancellationTokenSource();
        var stuck = conn.ForwardRequestAsync("tools/call",
            new JsonObject { ["name"] = mainTool, ["arguments"] = new JsonObject() }, callerCts.Token);
        await dispatcher.Queued.Task.WaitAsync(cts.Token);

        // Many writes at once: without the bridge's write lock the second
        // concurrent WriteLineAsync threw and broke the writer for good.
        var calls = Enumerable.Range(0, 25).Select(i => conn.ForwardRequestAsync("tools/call",
            new JsonObject { ["name"] = "wirefixture_greet", ["arguments"] = new JsonObject { ["s"] = $"n{i}" } },
            cts.Token)).ToArray();
        var results = await Task.WhenAll(calls);
        for (int i = 0; i < results.Length; i++)
            Assert.Equal($"got: n{i}", results[i]!["content"]![0]!["text"]!.GetValue<string>());

        callerCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stuck);
        await dispatcher.Cancelled.Task.WaitAsync(cts.Token);
        Assert.True(conn.IsConnected);

        await host.ShutdownAsync();
    }

    private static async Task<(AcadRpcHost Host, string PipeName, NeverRunsDispatcher Dispatcher, CancellationTokenSource Cts)> StartHostAsync()
    {
        AcadRpcHost.ResetForTests();
        var pipeName = "acad-rpc-jam-" + Guid.NewGuid().ToString("N");
        var dispatcher = new NeverRunsDispatcher();
        var host = AcadRpcHost.Initialize(new AcadRpcHostOptions(pipeName, dispatcher));
        host.RegisterAssembly(typeof(WireFixture).Assembly);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await host.StartAsync(cts.Token);
        return (host, pipeName, dispatcher, cts);
    }

    private static string MainThreadToolName(AcadRpcHost host) => host.ListRegisteredTools()
        .Single(t => t.ToolName.EndsWith("_on_main_thread_only", StringComparison.Ordinal)).ToolName;
}

/// <summary>A main thread that never comes free: work is queued, never run,
/// and leaves only when its token is cancelled.</summary>
internal sealed class NeverRunsDispatcher : IAcadMainThreadDispatcher
{
    public TaskCompletionSource Queued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<T> InvokeAsync<T>(Func<T> work, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() =>
        {
            tcs.TrySetCanceled(ct);
            Cancelled.TrySetResult();
        });
        Queued.TrySetResult();
        return tcs.Task;
    }

    public Task InvokeAsync(Action work, CancellationToken ct) =>
        InvokeAsync<object?>(() => { work(); return null; }, ct);
}
