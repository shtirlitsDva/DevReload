using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Acad.Rpc.Core;
using Autodesk.AutoCAD.ApplicationServices;
using UiMcp.Win32;

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
///
/// MODAL GUARD: a native modal dialog (file dialog, "Drawing Units", a COGO
/// projection dialog, …) runs its OWN message loop, so <see cref="Application.Idle"/>
/// stops firing and queued main-thread work would otherwise wait forever. A
/// per-call watchdog detects that specific condition — AutoCAD's main frame is
/// disabled, which only happens while a modal is up — and fails the call fast
/// with a message pointing at the off-thread dialog tools, instead of hanging.
/// It deliberately does NOT impose a blanket timeout: a genuinely slow main-
/// thread op (e.g. a plugin reload) with no modal present keeps waiting.
/// </remarks>
public sealed class AcadIdlePumpDispatcher : IAcadMainThreadDispatcher, IDisposable
{
    // Grace before the watchdog starts probing: the overwhelming majority of
    // main-thread tool calls complete on the next idle tick, well within this.
    private const int ModalGraceMs = 1500;
    private const int ModalPollMs = 500;

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

        // Set by the watchdog when it gives up on a modal-blocked call so the
        // work, if it is ever dequeued (after the modal closes), is skipped
        // rather than executed stale.
        int abandoned = 0;

        _queue.Enqueue(() =>
        {
            try
            {
                if (Volatile.Read(ref abandoned) != 0) return;
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

        StartModalWatchdog(tcs, () => Interlocked.Exchange(ref abandoned, 1));
        return tcs.Task;
    }

    public Task InvokeAsync(Action work, CancellationToken ct)
        => InvokeAsync<object?>(() => { work(); return null; }, ct);

    private void StartModalWatchdog<T>(TaskCompletionSource<T> tcs, Action markAbandoned)
    {
        _ = Task.Run(async () =>
        {
            // Fast path: let the common (sub-tick) case finish with no probing.
            if (await CompletesWithin(tcs.Task, ModalGraceMs).ConfigureAwait(false)) return;

            while (!tcs.Task.IsCompleted)
            {
                if (ModalDialogIsBlocking())
                {
                    markAbandoned();
                    tcs.TrySetException(new TimeoutException(
                        "AutoCAD's main thread is blocked by a modal dialog, so this main-thread " +
                        "tool was abandoned (it would otherwise hang until the dialog closes). " +
                        "Dismiss the dialog first with the off-thread tools: ui_list_windows to find " +
                        "it, then ui_dialog_buttons / ui_dialog_click / ui_press_key."));
                    return;
                }
                if (await CompletesWithin(tcs.Task, ModalPollMs).ConfigureAwait(false)) return;
            }
        });
    }

    private static async Task<bool> CompletesWithin(Task t, int ms)
    {
        var done = await Task.WhenAny(t, Task.Delay(ms)).ConfigureAwait(false);
        return done == t;
    }

    // A modal dialog disables its owner, so a disabled main frame is a precise
    // signal that a modal is up — distinct from AutoCAD merely being busy in a
    // command (which leaves the frame enabled).
    private static bool ModalDialogIsBlocking()
    {
        try
        {
            var h = Application.MainWindow?.Handle ?? IntPtr.Zero;
            return h != IntPtr.Zero && !NativeMethods.IsWindowEnabled(h);
        }
        catch { return false; }
    }

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
