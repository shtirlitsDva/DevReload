using System;

using Acad.Process;

namespace Acad.Rpc.Bridge;

/// <summary>
/// Mutable binding state: which AutoCAD instance the bridge is
/// currently driving. One bridge process binds at most one instance at
/// a time; another bridge process (e.g. for a second agent in a second
/// worktree) holds its own independent binding.
/// </summary>
public sealed class AcadInstanceBinding
{
    private readonly object _gate = new();
    private BoundInstance? _bound;

    /// <summary>Fires AFTER the binding mutates. Listeners receive the
    /// new value (null when detached).</summary>
    public event Action<BoundInstance?>? Changed;

    public BoundInstance? Current
    {
        get { lock (_gate) return _bound; }
    }

    public bool TryBind(int pid, string productName, string pipeName, out BoundInstance bound)
    {
        BoundInstance? old;
        BoundInstance @new;
        lock (_gate)
        {
            old = _bound;
            @new = new BoundInstance(
                Pid: pid,
                ProductName: productName,
                PipeName: pipeName,
                BoundAtUtc: DateTime.UtcNow);
            _bound = @new;
        }
        bound = @new;
        if (old?.Pid != pid) Changed?.Invoke(@new);
        return old?.Pid != pid;
    }

    public bool Detach()
    {
        BoundInstance? old;
        lock (_gate)
        {
            old = _bound;
            _bound = null;
        }
        if (old != null) Changed?.Invoke(null);
        return old != null;
    }

    /// <summary>Resolve a pid argument: explicit takes precedence; fall
    /// back to the bound pid; throw if neither.</summary>
    public int ResolvePid(int? explicitPid)
    {
        if (explicitPid is int p && p > 0) return p;
        var bound = Current;
        if (bound != null) return bound.Pid;
        throw new InvalidOperationException(
            "No pid: pass pid explicitly or bind a process first with acad_start / acad_attach.");
    }
}

public sealed record BoundInstance(
    int Pid,
    string ProductName,
    string PipeName,
    DateTime BoundAtUtc);
