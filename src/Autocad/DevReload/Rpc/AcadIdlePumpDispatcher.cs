using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Acad.Rpc.Core;
using Autodesk.AutoCAD.ApplicationServices;

namespace DevReload.Rpc;

/// <summary>
/// AutoCAD main-thread dispatcher. Drains a queue of work items on
/// every <see cref="Application.Idle"/> tick. Used by Acad.Rpc.Core to
/// marshal tool invocations that must touch AutoCAD APIs.
/// </summary>
/// <remarks>
/// Lifetime: subscribed during <c>DevReloaderCommands.Initialize</c>,
/// unsubscribed in Terminate. While AutoCAD is processing a command or
/// blocked, Idle does not fire — tool calls will queue and be drained
/// once the main thread is available again. This is the same back-pressure
/// AutoCAD's own command queue exhibits and is therefore consistent with
/// user expectations.
/// </remarks>
public sealed class AcadIdlePumpDispatcher : IAcadMainThreadDispatcher, IDisposable
{
    private readonly ConcurrentQueue<Action> _queue = new();
    private bool _subscribed;

    public AcadIdlePumpDispatcher()
    {
        Application.Idle += OnIdle;
        _subscribed = true;
    }

    public Task<T> InvokeAsync<T>(Func<T> work, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var registration = ct.CanBeCanceled
            ? ct.Register(() => tcs.TrySetCanceled(ct))
            : default;

        _queue.Enqueue(() =>
        {
            try
            {
                if (ct.IsCancellationRequested) { tcs.TrySetCanceled(ct); return; }
                var result = work();
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                registration.Dispose();
            }
        });

        return tcs.Task;
    }

    public Task InvokeAsync(Action work, CancellationToken ct)
        => InvokeAsync<object?>(() => { work(); return null; }, ct);

    private void OnIdle(object? sender, EventArgs e)
    {
        while (_queue.TryDequeue(out var work))
        {
            try { work(); }
            catch { /* tcs already captured the exception */ }
        }
    }

    public void Dispose()
    {
        if (!_subscribed) return;
        try { Application.Idle -= OnIdle; }
        catch { }
        _subscribed = false;
    }
}
