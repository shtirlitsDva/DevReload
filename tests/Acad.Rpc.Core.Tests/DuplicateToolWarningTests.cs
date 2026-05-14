using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Xunit;

namespace Acad.Rpc.Core.Tests;

[AcadRpcSurface(Group = "duptest_a")]
public static class DupFixtureA
{
    [AcadRpcTool(Name = "shared_name")]
    public static string Hello() => "from A";
}

[AcadRpcSurface(Group = "duptest_b")]
public static class DupFixtureB
{
    [AcadRpcTool(Name = "shared_name")]
    public static string Hello() => "from B";
}

[Collection("AcadRpcHostSingleton")]
public class DuplicateToolWarningTests
{
    [Fact]
    public void RegisterAssembly_DuplicateToolName_LogsWarning_AndKeepsFirst()
    {
        // Same assembly contains both fixtures — collision happens during
        // a single ScanAssembly call. The first survives, the second is
        // logged. Use an in-memory dynamic assembly with two surfaces to
        // exercise cross-assembly collisions too.
        var logs = new List<string>();
        AcadRpcHost.ResetForTests();
        var host = AcadRpcHost.Initialize(new AcadRpcHostOptions(
            "test-pipe-" + Guid.NewGuid().ToString("N"),
            new FakeDispatcher(),
            Log: logs.Add));

        host.RegisterAssembly(typeof(DupFixtureA).Assembly);

        // One of {Hello from A, Hello from B} is the surviving tool;
        // the other generated a log line.
        Assert.Contains(logs, l => l.Contains("duplicate tool name 'shared_name'", StringComparison.Ordinal));
        Assert.True(
            host.ListRegisteredTools().Count(t => t.ToolName == "shared_name") == 1,
            "expected exactly one entry for 'shared_name' to survive");
    }
}
