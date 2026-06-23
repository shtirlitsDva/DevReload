using System;
using System.Collections.Concurrent;

using System.Text.Json.Nodes;

using Acad.Process;

namespace Acad.Rpc.Bridge;

/// <summary>
/// Owns one <see cref="InstanceConnection"/> per AutoCAD pid the bridge is
/// driving. The bound pid (from <see cref="AcadInstanceBinding"/>) is the
/// default route when a tool call omits a pid; any other pid is connected on
/// demand. Several instances are driven at once, each over its own pipe.
/// </summary>
public sealed class ForwarderPool : IDisposable
{
    private readonly AcadInstanceBinding _binding;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<int, InstanceConnection> _conns = new();

    /// <summary>Fires when the default (bound) instance connects or
    /// disconnects, so the bridge refreshes its merged tools/list.</summary>
    public event Action<bool>? DefaultConnectionChanged;

    /// <summary>Notifications from the default (bound) instance.</summary>
    public event Action<JsonObject>? DefaultNotificationReceived;

    public ForwarderPool(AcadInstanceBinding binding, Action<string>? log = null)
    {
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
        _log = log ?? (_ => { });
        _binding.Changed += OnBindingChanged;
    }

    public int? DefaultPid => _binding.Current?.Pid;

    public InstanceConnection GetOrConnect(int pid)
    {
        return _conns.GetOrAdd(pid, p =>
        {
            var conn = new InstanceConnection(p, $"acad-rpc-{p}", _log);
            conn.ConnectionChanged += connected =>
            {
                // Capture default-ness BEFORE OnConnectionDropped detaches,
                // so the bridge still gets the disconnect signal (and refreshes
                // its merged tools/list) even though the binding is cleared.
                bool wasDefault = p == DefaultPid;
                if (!connected) OnConnectionDropped(conn);
                if (wasDefault) DefaultConnectionChanged?.Invoke(connected);
            };
            conn.NotificationReceived += notif =>
            {
                if (p == DefaultPid) DefaultNotificationReceived?.Invoke(notif);
            };
            conn.Start();
            return conn;
        });
    }

    /// <summary>
    /// A live pipe dropping is terminal: <see cref="InstanceConnection"/> never
    /// reconnects, so a connection that was up and then dropped is dead for good
    /// (the usual cause is the AutoCAD/Civil 3D process crashing). Evict it from
    /// the pool and, if it was the bound instance, clear the binding — otherwise
    /// the bridge keeps forwarding pid-less calls to a dead pipe ("zombie
    /// binding") and every remote tool appears to have dropped. Clearing the
    /// binding makes the next pid-less call fail with a clear "bind one with
    /// acad_start / acad_attach" instead.
    /// </summary>
    private void OnConnectionDropped(InstanceConnection conn)
    {
        if (_conns.TryRemove(conn.Pid, out var dead))
        {
            // Safe to dispose synchronously: we are invoked at the tail of the
            // connection's own Disconnect(), which has already nulled its reader
            // loop, so the second Disconnect() inside Dispose() is a no-op and
            // cannot self-wait.
            try { dead.Dispose(); } catch { }
            _log($"ForwarderPool: pid {conn.Pid} pipe dropped; evicted dead connection.");
        }

        if (_binding.Current?.Pid == conn.Pid)
        {
            _binding.Detach();
            _log($"ForwarderPool: bound instance pid {conn.Pid} is gone; binding cleared.");
        }
    }

    public InstanceConnection? Default
    {
        get
        {
            var pid = DefaultPid;
            if (pid is int p && _conns.TryGetValue(p, out var c)) return c;
            return null;
        }
    }

    public bool DefaultConnected => Default?.IsConnected == true;

    /// <summary>Plain-language reason a forwarded call to <paramref name="pid"/>
    /// can't reach a pipe right now.</summary>
    public string DescribeNotConnected(int pid)
    {
        if (DefaultPid == null && _conns.IsEmpty)
            return "no AutoCAD is bound. Launch one with acad_start, or pass an explicit pid of a running instance.";
        if (!NamedPipeProbe.Exists($"acad-rpc-{pid}"))
            return $"AutoCAD pid {pid}'s pipe is not up. Start it with acad_start, then acad_wait_pipe.";
        return $"AutoCAD pid {pid}'s pipe is present but the bridge hasn't finished connecting; retry in a moment.";
    }

    private void OnBindingChanged(BoundInstance? bound)
    {
        if (bound == null) return;
        var conn = GetOrConnect(bound.Pid);
        DefaultConnectionChanged?.Invoke(conn.IsConnected);
    }

    public void Dispose()
    {
        _binding.Changed -= OnBindingChanged;
        foreach (var c in _conns.Values) { try { c.Dispose(); } catch { } }
        _conns.Clear();
    }
}
