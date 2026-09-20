using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Acad.Rpc.Core;
using Autodesk.AutoCAD.ApplicationServices;
using UiMcp.Win32;

using DevReload.Diagnostics;

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
/// per-call watchdog detects that condition — see <see cref="BlockingModalTitle"/>
/// for the two signals it requires — and drops the call with a message naming the
/// dialog, instead of hanging. It deliberately does NOT impose a blanket timeout:
/// a genuinely slow main-thread op (e.g. a plugin reload) with no modal present
/// keeps waiting. It also only ever drops work that has not STARTED; see the
/// Queued/Running/Done comment below.
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

    // Queued -> Running -> Done, claimed with one interlocked write each. The
    // watchdog may take an item only out of Queued: once the work has started it
    // cannot be stopped, and a watchdog that completed the caller's task anyway
    // reported a modal for an operation that went on to succeed.
    private const int Queued = 0;
    private const int Running = 1;
    private const int Done = 2;

    public Task<T> InvokeAsync<T>(Func<T> work, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var registration = ct.CanBeCanceled
            ? ct.Register(() => tcs.TrySetCanceled(ct))
            : default;

        var state = new StrongBox<int>(Queued);

        _queue.Enqueue(() =>
        {
            // Lost the race to the watchdog: the item was abandoned before it
            // could start, and running it now would run it stale.
            if (Interlocked.CompareExchange(ref state.Value, Running, Queued) != Queued)
                return;

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
                Volatile.Write(ref state.Value, Done);
                registration.Dispose();
            }
        });

        StartModalWatchdog(tcs, state);
        return tcs.Task;
    }

    public Task InvokeAsync(Action work, CancellationToken ct)
        => InvokeAsync<object?>(() => { work(); return null; }, ct);

    private void StartModalWatchdog<T>(TaskCompletionSource<T> tcs, StrongBox<int> state)
    {
        _ = Task.Run(async () =>
        {
            // Fast path: let the common (sub-tick) case finish with no probing.
            if (await CompletesWithin(tcs.Task, ModalGraceMs).ConfigureAwait(false)) return;

            while (!tcs.Task.IsCompleted)
            {
                // Work that has started owns its own outcome, whatever the window
                // state says. Nothing left for the watchdog to do.
                if (Volatile.Read(ref state.Value) != Queued) return;

                string? dialog = BlockingModalTitle();
                if (dialog != null)
                {
                    // Take the item out of the queue, or lose to the pump that
                    // just started it — in which case leave it alone.
                    if (Interlocked.CompareExchange(ref state.Value, Done, Queued) != Queued)
                        return;

                    tcs.TrySetException(new TimeoutException(
                        $"AutoCAD's main thread is in a modal dialog ({dialog}), so this " +
                        "main-thread tool was dropped without running. Dismiss the dialog " +
                        "with the off-thread tools: ui_list_windows, then ui_dialog_buttons / " +
                        "ui_dialog_click / ui_press_key."));
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

    /// <summary>The blocking dialog's title, or null when nothing is blocking.</summary>
    /// <remarks>
    /// Two independent signals, both required.
    ///
    /// <para>A disabled main frame used to be the whole test, and it is not
    /// enough: AutoCAD disables its frame for other long main-thread work too —
    /// activating a document, for one — so a tool call during a tab switch was
    /// told a dialog was up while no window on the process was a dialog.</para>
    ///
    /// <para>The second signal is not an inference at all: GetGUIThreadInfo
    /// reports GUI_INMODALLOOP when the GUI thread is inside a modal message
    /// loop, and hwndActive is the window running it. Windows is asked rather
    /// than deduced from what a modal happens to do to its neighbours, and the
    /// answer carries the dialog's identity so the error can name it.</para>
    ///
    /// <para>Both are needed because each alone over-reports: the frame is
    /// disabled for non-modal reasons, and the modal loop flag is also set for
    /// menu and drag loops, which leave the frame enabled.</para>
    /// </remarks>
    private static string? BlockingModalTitle()
    {
        try
        {
            var frame = Application.MainWindow?.Handle ?? IntPtr.Zero;
            if (frame == IntPtr.Zero) return null;
            if (NativeMethods.IsWindowEnabled(frame)) return null;

            uint tid = NativeMethods.GetWindowThreadProcessId(frame, out _);
            if (tid == 0) return null;

            var gti = new NativeMethods.GUITHREADINFO
            {
                cbSize = Marshal.SizeOf<NativeMethods.GUITHREADINFO>(),
            };
            if (!NativeMethods.GetGUIThreadInfo(tid, ref gti)) return null;
            if ((gti.flags & NativeMethods.GUI_INMODALLOOP) == 0) return null;

            string? title = WindowEnum.Describe(gti.hwndActive)?.Title;
            return string.IsNullOrWhiteSpace(title) ? "unnamed dialog" : title!;
        }
        catch (Exception ex)
        {
            // Category B - report, do not rethrow. This is a probe asking "is a
            // modal up?"; if it cannot tell, "no" is the safe answer and the pump
            // keeps running. Throwing would take the idle pump down with it.
            DevReloadDiagnostics.Report("AcadIdlePumpDispatcher.BlockingModalTitle", ex);
            return null;
        }
    }

    private void OnIdle(object? sender, EventArgs e)
    {
        while (_queue.TryDequeue(out var work))
        {
            try { work(); }
            catch (Exception ex)
            {
                // Category B - report, do not rethrow. This runs on AutoCAD's Idle
                // event: an exception escaping here takes the host down. The work
                // item's own TaskCompletionSource already carries the failure back
                // to its caller, so this line is a second copy for the log, not the
                // only record.
                DevReloadDiagnostics.Report("AcadIdlePumpDispatcher: queued work item", ex);
            }
        }
    }

    public void Dispose()
    {
        if (!_subscribed) return;
        // Category A - report and rethrow. Failing to unsubscribe leaves a live
        // delegate on AutoCAD's Idle event, which is a real leak. The flag is
        // cleared in a finally so the object does not claim to still be
        // subscribed after a failed attempt.
        try
        {
            Application.Idle -= OnIdle;
        }
        catch (Exception ex)
        {
            DevReloadDiagnostics.Report("AcadIdlePumpDispatcher.Dispose (Idle unsubscribe)", ex);
            throw;
        }
        finally
        {
            _subscribed = false;
        }
    }
}
