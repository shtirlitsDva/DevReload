using System;
using System.Collections.Generic;

using Acad.Process;
using Acad.Rpc.Bridge;

using Xunit;

namespace Acad.Rpc.Core.Tests;

/// <summary>
/// Pure-policy + orchestration tests for <see cref="AutoAttach"/>. No
/// real process enumeration, no COM, no pipes — the enumerate callback
/// is injected with synthetic <see cref="AcadProcessInfo"/> lists so we
/// can pin every branch of "is this an unambiguous candidate?".
/// </summary>
public class AutoAttachTests
{
    private static AcadProcessInfo Acad(int pid, bool pipeAvailable) =>
        new(
            Pid: pid,
            ProcessName: "acad",
            MainWindowTitle: "Civil 3D " + pid,
            PipeName: "acad-rpc-" + pid,
            PipeAvailable: pipeAvailable);

    // ── PickCandidate (pure policy) ───────────────────────────────────

    [Fact]
    public void PickCandidate_NullList_ReturnsNull()
    {
        Assert.Null(AutoAttach.PickCandidate(null));
    }

    [Fact]
    public void PickCandidate_EmptyList_ReturnsNull()
    {
        Assert.Null(AutoAttach.PickCandidate(new List<AcadProcessInfo>()));
    }

    [Fact]
    public void PickCandidate_SingleProcess_PipeUp_ReturnsItsPid()
    {
        var list = new List<AcadProcessInfo> { Acad(1111, true) };
        Assert.Equal(1111, AutoAttach.PickCandidate(list));
    }

    [Fact]
    public void PickCandidate_SingleProcess_PipeDown_ReturnsNull()
    {
        // DevReload may not be loaded yet — refuse to bind because the
        // forwarder would fail to connect anyway.
        var list = new List<AcadProcessInfo> { Acad(1111, false) };
        Assert.Null(AutoAttach.PickCandidate(list));
    }

    [Fact]
    public void PickCandidate_TwoProcesses_BothPipesUp_ReturnsNull()
    {
        // Ambiguous — agent must pick.
        var list = new List<AcadProcessInfo> { Acad(1111, true), Acad(2222, true) };
        Assert.Null(AutoAttach.PickCandidate(list));
    }

    [Fact]
    public void PickCandidate_TwoProcesses_OnePipeUp_ReturnsTheOneWithPipe()
    {
        var list = new List<AcadProcessInfo> { Acad(1111, false), Acad(2222, true) };
        Assert.Equal(2222, AutoAttach.PickCandidate(list));
    }

    [Fact]
    public void PickCandidate_TwoProcesses_NeitherPipeUp_ReturnsNull()
    {
        var list = new List<AcadProcessInfo> { Acad(1111, false), Acad(2222, false) };
        Assert.Null(AutoAttach.PickCandidate(list));
    }

    [Fact]
    public void PickCandidate_ManyProcesses_ExactlyOnePipeUp_PicksThatOne()
    {
        var list = new List<AcadProcessInfo>
        {
            Acad(1111, false), Acad(2222, false), Acad(3333, true),
            Acad(4444, false), Acad(5555, false),
        };
        Assert.Equal(3333, AutoAttach.PickCandidate(list));
    }

    [Fact]
    public void PickCandidate_ManyProcesses_TwoPipesUp_ReturnsNull()
    {
        // Short-circuits on the second match — order matters only for
        // performance, not correctness.
        var list = new List<AcadProcessInfo>
        {
            Acad(1111, true), Acad(2222, false), Acad(3333, true),
        };
        Assert.Null(AutoAttach.PickCandidate(list));
    }

    // ── TryAttach (orchestration) ─────────────────────────────────────

    [Fact]
    public void TryAttach_SingleCandidate_BindsAndReturnsBound()
    {
        var binding = new AcadInstanceBinding();
        var logs = new List<string>();
        var snapshot = new List<AcadProcessInfo> { Acad(7777, true) };

        var outcome = AutoAttach.TryAttach(() => snapshot, binding, logs.Add);

        Assert.True(outcome.Bound);
        Assert.Equal(7777, outcome.Pid);
        Assert.Contains("bound to pid 7777", outcome.Reason);
        Assert.NotNull(binding.Current);
        Assert.Equal(7777, binding.Current!.Pid);
        Assert.Equal("acad-rpc-7777", binding.Current!.PipeName);
        Assert.Single(logs);
        Assert.Contains("AutoAttach: bound to pid 7777", logs[0]);
    }

    [Fact]
    public void TryAttach_SingleCandidate_FiresBindingChangedOnce()
    {
        var binding = new AcadInstanceBinding();
        var received = new List<BoundInstance?>();
        binding.Changed += received.Add;

        AutoAttach.TryAttach(
            () => new List<AcadProcessInfo> { Acad(7777, true) },
            binding);

        Assert.Single(received);
        Assert.Equal(7777, received[0]!.Pid);
    }

    [Fact]
    public void TryAttach_NoProcesses_LeavesBindingNull_ReportsReason()
    {
        var binding = new AcadInstanceBinding();
        var logs = new List<string>();

        var outcome = AutoAttach.TryAttach(
            () => new List<AcadProcessInfo>(), binding, logs.Add);

        Assert.False(outcome.Bound);
        Assert.Null(outcome.Pid);
        Assert.Contains("no acad.exe processes running", outcome.Reason);
        Assert.Null(binding.Current);
        Assert.Single(logs);
        Assert.Contains("no acad.exe processes running", logs[0]);
    }

    [Fact]
    public void TryAttach_ProcessButNoPipe_LeavesBindingNull_ReportsReason()
    {
        var binding = new AcadInstanceBinding();
        var logs = new List<string>();

        var outcome = AutoAttach.TryAttach(
            () => new List<AcadProcessInfo> { Acad(1111, false) },
            binding, logs.Add);

        Assert.False(outcome.Bound);
        Assert.Contains("no DevReload pipe", outcome.Reason);
        Assert.Null(binding.Current);
        Assert.Contains("AutoAttach: skipped", logs[0]);
    }

    [Fact]
    public void TryAttach_MultipleBindable_LeavesBindingNull_ReportsAmbiguous()
    {
        var binding = new AcadInstanceBinding();
        var logs = new List<string>();

        var outcome = AutoAttach.TryAttach(
            () => new List<AcadProcessInfo> { Acad(1111, true), Acad(2222, true) },
            binding, logs.Add);

        Assert.False(outcome.Bound);
        Assert.Contains("ambiguous", outcome.Reason);
        Assert.Null(binding.Current);
        Assert.Contains("2 bindable", logs[0]);
    }

    [Fact]
    public void TryAttach_NullEnumerate_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            AutoAttach.TryAttach(null!, new AcadInstanceBinding()));
    }

    [Fact]
    public void TryAttach_NullBinding_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            AutoAttach.TryAttach(() => new List<AcadProcessInfo>(), null!));
    }

    [Fact]
    public void TryAttach_NoLogger_DoesNotThrow()
    {
        var binding = new AcadInstanceBinding();
        var outcome = AutoAttach.TryAttach(
            () => new List<AcadProcessInfo> { Acad(7777, true) },
            binding,
            log: null);
        Assert.True(outcome.Bound);
    }

    [Fact]
    public void TryAttach_CalledTwice_SamePid_IdempotentEventWise()
    {
        // Re-running auto-attach (e.g. after a /reload-plugins) on the
        // same single-instance state should not re-fire Changed —
        // binding.TryBind is no-op-with-no-event when the pid matches.
        var binding = new AcadInstanceBinding();
        Func<IReadOnlyList<AcadProcessInfo>> enumerate =
            () => new List<AcadProcessInfo> { Acad(7777, true) };

        AutoAttach.TryAttach(enumerate, binding);
        var received = new List<BoundInstance?>();
        binding.Changed += received.Add;

        var outcome = AutoAttach.TryAttach(enumerate, binding);

        Assert.True(outcome.Bound);
        Assert.Equal(7777, binding.Current!.Pid);
        Assert.Empty(received);
    }
}
