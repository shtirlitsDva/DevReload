using System;
using System.Linq;
using System.Reflection;
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
}

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
    public void MethodRequiresMainThread_ReturnsTrue_ForAnnotatedMethod()
    {
        var mi = typeof(FixtureTools).GetMethod(nameof(FixtureTools.OnMainThreadOnly))!;
        Assert.True(AcadRpcHost.MethodRequiresMainThread(mi));
    }

    [Fact]
    public void MethodRequiresMainThread_ReturnsFalse_ForUnannotatedMethod()
    {
        var mi = typeof(FixtureTools).GetMethod(nameof(FixtureTools.Echo))!;
        Assert.False(AcadRpcHost.MethodRequiresMainThread(mi));
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
    public void RegisterAssembly_AddsTools_ToLiveByNameIndex()
    {
        var host = NewHost();
        host.RegisterAssembly(typeof(FixtureTools).Assembly);
        var index = (System.Collections.IDictionary)typeof(AcadRpcHost).GetField(
            "_toolsByName", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(host)!;
        Assert.True(index.Count >= 3, $"expected >=3 live tools, got {index.Count}");
    }

    [Fact]
    public void UnregisterAssembly_RemovesTools_FromLiveByNameIndex()
    {
        var host = NewHost();
        host.RegisterAssembly(typeof(FixtureTools).Assembly);
        var index = (System.Collections.IDictionary)typeof(AcadRpcHost).GetField(
            "_toolsByName", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(host)!;
        var before = index.Count;
        host.UnregisterAssembly(typeof(FixtureTools).Assembly);
        var after = index.Count;
        Assert.True(before > after);
        Assert.Equal(0, after);
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
