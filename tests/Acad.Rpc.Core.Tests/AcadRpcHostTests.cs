using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Acad.Rpc.Core.Tests;

// Fixture surface for attribute-discovery tests. Lives in the test
// assembly so the assembly identity test below is meaningful.
[AcadRpcSurface]
public static class FixtureTools
{
    [AcadRpcTool]
    public static string Echo(string s) => s;

    [AcadRpcTool, System.ComponentModel.Description("Add two ints")]
    public static int Add(int a, int b) => a + b;

    [AcadRpcTool]
    [RunOnAcadMainThread]
    public static string OnMainThreadOnly() => "main";

    [AcadRpcTool]
    [RunOnAcadMainThread]
    public static int OnMainThreadAdd(int a, int b) => a + b;
}

[CollectionDefinition("AcadRpcHostSingleton", DisableParallelization = true)]
public class AcadRpcHostSingletonCollection { }

// Every test class that touches the AcadRpcHost singleton joins this
// collection so xUnit runs them sequentially. The singleton is global
// per AppDomain; parallel access produces flaky tests.
[Collection("AcadRpcHostSingleton")]
public class AcadRpcHostTests
{
    [Fact]
    public void Initialize_Twice_ReturnsSameInstance()
    {
        AcadRpcHost.ResetForTests();
        var fake = new FakeDispatcher();
        var opts = new AcadRpcHostOptions("test-pipe", fake);
        var a = AcadRpcHost.Initialize(opts);
        var b = AcadRpcHost.Initialize(opts);
        Assert.Same(a, b);
    }

    [Fact]
    public void Current_BeforeInitialize_Throws()
    {
        AcadRpcHost.ResetForTests();
        Assert.Throws<InvalidOperationException>(() => _ = AcadRpcHost.Current);
    }

    [Fact]
    public void RegisterAssembly_DiscoversAnnotatedTools()
    {
        var host = NewHost();
        host.RegisterAssembly(typeof(FixtureTools).Assembly);
        var asmName = typeof(FixtureTools).Assembly.GetName().Name!;
        var tools = host.ListRegisteredTools()
            .Where(t => t.SourceAssembly == asmName)
            .ToList();
        Assert.True(tools.Count >= 3, $"expected at least 3 tools, got {tools.Count}");
        Assert.Contains(tools, t => t.ToolName.EndsWith("_echo", StringComparison.Ordinal));
        Assert.Contains(tools, t => t.ToolName.EndsWith("_add", StringComparison.Ordinal));
    }

    [Fact]
    public void UnregisterAssembly_RemovesOnlyThatAssemblysTools()
    {
        var host = NewHost();
        host.RegisterAssembly(typeof(FixtureTools).Assembly);
        var asmName = typeof(FixtureTools).Assembly.GetName().Name!;
        var before = host.ListRegisteredTools().Count(t => t.SourceAssembly == asmName);
        host.UnregisterAssembly(typeof(FixtureTools).Assembly);
        var after = host.ListRegisteredTools().Count(t => t.SourceAssembly == asmName);
        Assert.True(before > 0);
        Assert.Equal(0, after);
    }

    [Fact]
    public void RegisterAssembly_Idempotent()
    {
        var host = NewHost();
        host.RegisterAssembly(typeof(FixtureTools).Assembly);
        var count1 = host.ListRegisteredTools().Count;
        host.RegisterAssembly(typeof(FixtureTools).Assembly);
        var count2 = host.ListRegisteredTools().Count;
        Assert.Equal(count1, count2);
    }

    [Fact]
    public void RegisterAssembly_NullArg_Throws()
    {
        var host = NewHost();
        Assert.Throws<ArgumentNullException>(() => host.RegisterAssembly(null!));
    }

    [Fact]
    public void UnregisterAssembly_NullArg_Throws()
    {
        var host = NewHost();
        Assert.Throws<ArgumentNullException>(() => host.UnregisterAssembly(null!));
    }

    [Fact]
    public void ToolName_PrefixedByAssemblyName_WhenNotOverridden()
    {
        var host = NewHost();
        host.RegisterAssembly(typeof(FixtureTools).Assembly);
        var asmName = typeof(FixtureTools).Assembly.GetName().Name!;
        var prefix = asmName.ToLowerInvariant().Replace('.', '_');
        var tools = host.ListRegisteredTools();
        Assert.Contains(tools, t => t.ToolName.StartsWith(prefix + "_", StringComparison.Ordinal));
    }

    [Fact]
    public void Description_Captured_FromDescriptionAttribute()
    {
        var host = NewHost();
        host.RegisterAssembly(typeof(FixtureTools).Assembly);
        var add = host.ListRegisteredTools().FirstOrDefault(
            t => t.ToolName.EndsWith("_add", StringComparison.Ordinal));
        Assert.NotNull(add);
        Assert.Equal("Add two ints", add!.Description);
    }

    [Fact]
    public void EnableAutoDiscovery_RegistersFixtureAssemblyFromDefaultAlc()
    {
        var host = NewHost();
        // FixtureTools lives in this assembly (test assembly, default ALC).
        // After enabling auto-discovery, it must register automatically —
        // no explicit RegisterAssembly call.
        host.EnableAutoDiscovery();
        var asmName = typeof(FixtureTools).Assembly.GetName().Name!;
        Assert.Contains(host.ListRegisteredTools(), t => t.SourceAssembly == asmName);
    }

    [Fact]
    public void EnableAutoDiscovery_Idempotent()
    {
        var host = NewHost();
        host.EnableAutoDiscovery();
        int firstCount = host.ListRegisteredTools().Count;
        host.EnableAutoDiscovery();
        int secondCount = host.ListRegisteredTools().Count;
        Assert.Equal(firstCount, secondCount);
    }

    [Fact]
    public void ListRegisteredTools_EmptyByDefault()
    {
        var host = NewHost();
        Assert.Empty(host.ListRegisteredTools());
    }

    [Fact]
    public void ApiVersion_IsSemverShaped()
    {
        var host = NewHost();
        Assert.Matches(@"^\d+\.\d+\.\d+", host.ApiVersion);
    }

    [Fact]
    public void ToolListChanged_Fires_OnRegister()
    {
        var host = NewHost();
        int events = 0;
        host.Core.ToolListChanged += () => System.Threading.Interlocked.Increment(ref events);
        host.RegisterAssembly(typeof(FixtureTools).Assembly);
        Assert.True(events >= 1, $"expected ToolListChanged to fire at least once, got {events}");
    }

    [Fact]
    public async Task InvokeTool_MarkedRunOnAcadMainThread_GoesThroughDispatcher()
    {
        var host = NewHost();
        host.RegisterAssembly(typeof(FixtureTools).Assembly);
        var fake = (FakeDispatcher)host.Dispatcher;
        int before = fake.InvokeCount;

        var toolName = host.ListRegisteredTools()
            .Single(t => t.ToolName.EndsWith("_on_main_thread_only", StringComparison.Ordinal))
            .ToolName;

        string result = await host.Core.InvokeToolForTestAsync(toolName, new JsonObject(), default);
        Assert.Equal("main", result);
        Assert.Equal(before + 1, fake.InvokeCount);
    }

    [Fact]
    public async Task InvokeTool_NotMarked_DoesNotGoThroughDispatcher()
    {
        var host = NewHost();
        host.RegisterAssembly(typeof(FixtureTools).Assembly);
        var fake = (FakeDispatcher)host.Dispatcher;
        int before = fake.InvokeCount;

        var toolName = host.ListRegisteredTools()
            .Single(t => t.ToolName.EndsWith("_echo", StringComparison.Ordinal))
            .ToolName;

        string result = await host.Core.InvokeToolForTestAsync(
            toolName, new JsonObject { ["s"] = "x" }, default);
        Assert.Equal("x", result);
        Assert.Equal(before, fake.InvokeCount);
    }

    [Fact]
    public async Task InvokeTool_MarkedRunOnAcadMainThread_WithArgs_BindsCorrectly()
    {
        var host = NewHost();
        host.RegisterAssembly(typeof(FixtureTools).Assembly);
        var toolName = host.ListRegisteredTools()
            .Single(t => t.ToolName.EndsWith("_on_main_thread_add", StringComparison.Ordinal))
            .ToolName;

        string result = await host.Core.InvokeToolForTestAsync(
            toolName,
            new JsonObject { ["a"] = 7, ["b"] = 35 },
            default);
        Assert.Equal("42", result);
    }

    [Fact]
    public void ToolListChanged_Fires_OnUnregister()
    {
        var host = NewHost();
        host.RegisterAssembly(typeof(FixtureTools).Assembly);
        int events = 0;
        host.Core.ToolListChanged += () => System.Threading.Interlocked.Increment(ref events);
        host.UnregisterAssembly(typeof(FixtureTools).Assembly);
        Assert.True(events >= 1, $"expected ToolListChanged to fire on unregister, got {events}");
    }

    static AcadRpcHost NewHost()
    {
        AcadRpcHost.ResetForTests();
        var fake = new FakeDispatcher();
        return AcadRpcHost.Initialize(
            new AcadRpcHostOptions(
                "test-pipe-" + Guid.NewGuid().ToString("N"),
                fake));
    }
}

internal sealed class FakeDispatcher : IAcadMainThreadDispatcher
{
    public int InvokeCount;

    public Task<T> InvokeAsync<T>(Func<T> work, CancellationToken ct)
    {
        Interlocked.Increment(ref InvokeCount);
        return Task.FromResult(work());
    }

    public Task InvokeAsync(Action work, CancellationToken ct)
    {
        Interlocked.Increment(ref InvokeCount);
        work();
        return Task.CompletedTask;
    }
}
