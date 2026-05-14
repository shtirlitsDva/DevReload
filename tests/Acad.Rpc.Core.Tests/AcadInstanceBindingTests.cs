using System;
using System.Collections.Generic;
using Acad.Rpc.Bridge;
using Xunit;

namespace Acad.Rpc.Core.Tests;

/// <summary>
/// Pure state-machine tests for the bridge's bound-pid record. No
/// process, no COM, no pipe — just the binding logic itself. This is
/// the foundation for multi-instance support: each bridge process owns
/// one of these.
/// </summary>
public class AcadInstanceBindingTests
{
    [Fact]
    public void Current_StartsNull()
    {
        var b = new AcadInstanceBinding();
        Assert.Null(b.Current);
    }

    [Fact]
    public void TryBind_SetsCurrent_AndFiresChanged()
    {
        var b = new AcadInstanceBinding();
        var received = new List<BoundInstance?>();
        b.Changed += received.Add;

        bool isNew = b.TryBind(1234, "Civil 3D", "acad-rpc-1234", out var bound);

        Assert.True(isNew);
        Assert.Equal(1234, bound.Pid);
        Assert.Equal("Civil 3D", bound.ProductName);
        Assert.Equal("acad-rpc-1234", bound.PipeName);
        Assert.NotNull(b.Current);
        Assert.Equal(1234, b.Current!.Pid);
        Assert.Single(received);
        Assert.Equal(1234, received[0]!.Pid);
    }

    [Fact]
    public void TryBind_SamePid_Idempotent_NoChangeEvent()
    {
        var b = new AcadInstanceBinding();
        b.TryBind(1234, "Civil 3D", "acad-rpc-1234", out _);

        var received = new List<BoundInstance?>();
        b.Changed += received.Add;

        bool isNew = b.TryBind(1234, "Civil 3D", "acad-rpc-1234", out _);

        Assert.False(isNew);
        Assert.Empty(received);
    }

    [Fact]
    public void TryBind_DifferentPid_RebindsAndFires()
    {
        var b = new AcadInstanceBinding();
        b.TryBind(1234, "Civil 3D", "acad-rpc-1234", out _);

        var received = new List<BoundInstance?>();
        b.Changed += received.Add;

        bool isNew = b.TryBind(5678, "AutoCAD", "acad-rpc-5678", out _);

        Assert.True(isNew);
        Assert.Single(received);
        Assert.Equal(5678, received[0]!.Pid);
        Assert.Equal(5678, b.Current!.Pid);
    }

    [Fact]
    public void Detach_WhenBound_ClearsAndFiresWithNull()
    {
        var b = new AcadInstanceBinding();
        b.TryBind(1234, "Civil 3D", "acad-rpc-1234", out _);

        var received = new List<BoundInstance?>();
        b.Changed += received.Add;

        bool wasBound = b.Detach();

        Assert.True(wasBound);
        Assert.Null(b.Current);
        Assert.Single(received);
        Assert.Null(received[0]);
    }

    [Fact]
    public void Detach_WhenNotBound_NoOp_NoEvent()
    {
        var b = new AcadInstanceBinding();
        var received = new List<BoundInstance?>();
        b.Changed += received.Add;

        bool wasBound = b.Detach();

        Assert.False(wasBound);
        Assert.Null(b.Current);
        Assert.Empty(received);
    }

    [Fact]
    public void ResolvePid_ExplicitWins()
    {
        var b = new AcadInstanceBinding();
        b.TryBind(1234, "Civil 3D", "acad-rpc-1234", out _);
        Assert.Equal(9999, b.ResolvePid(9999));
    }

    [Fact]
    public void ResolvePid_NoExplicit_FallsBackToBound()
    {
        var b = new AcadInstanceBinding();
        b.TryBind(1234, "Civil 3D", "acad-rpc-1234", out _);
        Assert.Equal(1234, b.ResolvePid(null));
    }

    [Fact]
    public void ResolvePid_NoExplicit_NoBound_Throws()
    {
        var b = new AcadInstanceBinding();
        Assert.Throws<InvalidOperationException>(() => b.ResolvePid(null));
    }
}
