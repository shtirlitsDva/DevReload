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
                if (p == DefaultPid) DefaultConnectionChanged?.Invoke(connected);
            };
            conn.NotificationReceived += notif =>
            {
                if (p == DefaultPid) DefaultNotificationReceived?.Invoke(notif);
            };
            conn.Start();
            return conn;
        });
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
