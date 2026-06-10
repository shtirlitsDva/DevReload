using System;
using System.Collections.Generic;

using Acad.Process;

namespace Acad.Rpc.Bridge;

/// <summary>
/// Bridge-startup auto-binding policy. Solves the agent-resume case:
/// when the bridge process restarts (Claude Code reloads plugins, agent
/// resumes a session, etc.) there's typically still one AutoCAD running
/// from the previous session — without auto-attach the agent sees the
/// in-AutoCAD tools (<c>devreload_*</c>) as offline until it remembers
/// to call <c>acad_attach</c>.
///
/// Conservative: bind only when exactly one running AutoCAD has its
/// in-process RPC pipe up. Zero / multiple → leave unbound and let the
/// agent pick explicitly via <c>acad_attach</c>.
/// </summary>
public static class AutoAttach
{
    /// <summary>Pure policy: among running AutoCADs, return the pid to
    /// bind to, or null when no unambiguous candidate exists. Bindable
    /// means <see cref="AcadProcessInfo.PipeAvailable"/> — i.e. DevReload
    /// is already loaded in that process.</summary>
    public static int? PickCandidate(IReadOnlyList<AcadProcessInfo>? running)
    {
        if (running == null) return null;
        int? only = null;
        foreach (var p in running)
        {
            if (!p.PipeAvailable) continue;
            if (only != null) return null;
            only = p.Pid;
        }
        return only;
    }

    /// <summary>Orchestrate one auto-attach attempt: enumerate, pick,
    /// bind. Returns the outcome (whether binding occurred + a short
    /// human-readable reason). <paramref name="enumerate"/> is injected
    /// for testability — production passes
    /// <c>controller.EnumerateProcesses</c>.</summary>
    public static AutoAttachOutcome TryAttach(
        Func<IReadOnlyList<AcadProcessInfo>> enumerate,
        AcadInstanceBinding binding,
        Action<string>? log = null)
    {
        if (enumerate == null) throw new ArgumentNullException(nameof(enumerate));
        if (binding == null) throw new ArgumentNullException(nameof(binding));

        var running = enumerate();
        var pid = PickCandidate(running);
        if (pid is null)
        {
            int total = running?.Count ?? 0;
            int bindable = 0;
            if (running != null)
                foreach (var p in running) if (p.PipeAvailable) bindable++;
            string reason = total == 0
                ? "no acad.exe processes running"
                : bindable == 0
                    ? $"{total} acad process(es) but no DevReload pipe is up"
                    : $"{bindable} bindable acad process(es) — ambiguous, agent must pick";
            log?.Invoke("AutoAttach: skipped — " + reason);
            return new AutoAttachOutcome(Bound: false, Pid: null, Reason: reason);
        }

        binding.TryBind(
            pid: pid.Value,
            productName: "AutoCAD",
            pipeName: "acad-rpc-" + pid.Value,
            out _);
        string ok = "bound to pid " + pid.Value;
        log?.Invoke("AutoAttach: " + ok);
        return new AutoAttachOutcome(Bound: true, Pid: pid, Reason: ok);
    }
}

public sealed record AutoAttachOutcome(bool Bound, int? Pid, string Reason);
