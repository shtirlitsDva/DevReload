using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Acad.Rpc.Core;
#if BRICSCAD
using Bricscad.ApplicationServices;
#else
using Autodesk.AutoCAD.ApplicationServices;
#endif
using UiMcp.Win32;

using DevReload.Diagnostics;

namespace DevReload.Rpc;

/// <summary>
/// Host main-thread dispatcher. Queues work items and posts a drain to the
/// main thread's WPF <see cref="Dispatcher"/>. Used by Acad.Rpc.Core
/// to marshal tool invocations that must touch AutoCAD/BricsCAD APIs.
/// </summary>
/// <remarks>
/// WHY POSTED, NOT Application.Idle: BricsCAD raises Idle from MFC's
/// <c>CWinApp::OnIdle</c> and skips it while its input queue holds anything,
/// so a BricsCAD started by a script or agent can run for good without one
/// Idle event (measured: 0 events in 18 s, while a Post from a background
/// thread ran on the main thread within 10 ms). A posted message is
/// delivered by whichever message loop is running, in both hosts.
///
/// WHY THE THREAD'S DISPATCHER, NOT SynchronizationContext.Current: the
/// context that happens to be current at Initialize belongs to whoever
/// installed it, not to the main thread. WinForms swaps it whenever its
/// outermost message loop ends (a WinForms modal at startup, such as the
/// Drawing Recovery box, can leave a plain context behind, whose Post runs on
/// the thread pool), and a WinForms context posts through a marshalling
/// window it may destroy. Captured that way, the drain ran off the main
/// thread, where <see cref="CanRunNow"/> is never true, and retried forever
/// while the main thread sat idle in GetMessage (Civil, 3 of 3 starts that
/// had a Drawing Recovery box, 2026-10-05). The WPF Dispatcher is bound to
/// the thread itself and posts to its own message-only window, which lives
/// as long as the thread; no other code can swap it out.
///
/// READY GUARD: that includes loops where tool work must NOT run: a command
/// waiting at a prompt, a modal dialog. So the drain first asks
/// <see cref="CanRunNow"/> (application context, no blocking modal) and, when
/// the answer is no, tries again <see cref="RetryMs"/> later. While AutoCAD is
/// processing a command or blocked, tool calls queue and run once the main
/// thread is free, the same back-pressure AutoCAD's own command queue has.
///
/// MODAL GUARD: a native modal dialog (file dialog, "Drawing Units", a COGO
/// projection dialog, …) can stay up indefinitely, and queued main-thread work
/// would otherwise wait forever. A per-call watchdog detects that condition —
/// see <see cref="BlockingModalTitle"/> for the two signals it requires — and
/// drops the call with a message naming the dialog, instead of hanging. It
/// deliberately does NOT impose a blanket timeout: a genuinely slow main-thread
/// op (e.g. a plugin reload) with no modal present keeps waiting. It also only
/// ever drops work that has not STARTED; see the Queued/Running/Done comment
/// below.
///
/// Lifetime: created in <c>DevReloaderCommands.Initialize</c> (on the main
/// thread, whose Dispatcher it takes), disposed in Terminate.
/// </remarks>
public sealed class AcadMainThreadDispatcher : IAcadMainThreadDispatcher, IDisposable
{
    // Grace before the watchdog starts probing: the overwhelming majority of
    // main-thread tool calls complete on the first drain, well within this.
    private const int ModalGraceMs = 1500;
    private const int ModalPollMs = 500;

    // How long a drain that found the host busy waits before trying again.
    private const int RetryMs = 100;

    // A drain that has found the host busy this long reports why, so a stall
    // leaves a trail in devreload.log instead of needing a dump; then again
    // every StallReportEveryMs while it lasts.
    private const int StallReportAfterMs = 10_000;
    private const int StallReportEveryMs = 30_000;

    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly Dispatcher _main;
    private readonly uint _mainThreadId;
    // 1 while a drain is posted or a retry is pending, so a burst of calls
    // posts one drain, not one each.
    private int _drainScheduled;
    private volatile bool _disposed;

    // Main thread only (Drain): when the current run of busy drains began, and
    // when it was last reported. 0 = the last drain ran.
    private long _busySince;
    private long _busyReportedAt;

    public AcadMainThreadDispatcher()
    {
        _mainThreadId = NativeMethods.GetCurrentThreadId();

        // "Created on the main thread" is checked against the frame's own
        // thread, not inferred from what happens to be installed on this one.
        var frame = Application.MainWindow?.Handle ?? IntPtr.Zero;
        if (frame != IntPtr.Zero)
        {
            uint frameThread = NativeMethods.GetWindowThreadProcessId(frame, out _);
            if (frameThread != 0 && frameThread != _mainThreadId)
                throw new InvalidOperationException(
                    $"AcadMainThreadDispatcher must be created on the host's main thread " +
                    $"(created on thread {_mainThreadId}, the main window's is {frameThread}).");
        }

        _main = Dispatcher.CurrentDispatcher;

        DevReloadDiagnostics.Info(
            $"AcadMainThreadDispatcher: main thread {_mainThreadId}, posting through its WPF " +
            $"Dispatcher; SynchronizationContext.Current at creation was " +
            $"{SynchronizationContext.Current?.GetType().FullName ?? "null"} (not used).");
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

        ScheduleDrain();
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
            DevReloadDiagnostics.Report("AcadMainThreadDispatcher.BlockingModalTitle", ex);
            return null;
        }
    }

    private void ScheduleDrain()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _drainScheduled, 1) != 0) return;

        try
        {
            _main.BeginInvoke(DispatcherPriority.Normal, new Action(Drain));
        }
        catch (Exception ex)
        {
            // Category B - report, do not rethrow. A post that failed with the
            // flag left at 1 would refuse every later drain for the life of the
            // process, silently: the flag goes back so the next call posts anew.
            Volatile.Write(ref _drainScheduled, 0);
            DevReloadDiagnostics.Report("AcadMainThreadDispatcher: posting the drain", ex);
        }
    }

    // On the main thread.
    private void Drain()
    {
        if (_disposed) return;

        uint thread = NativeMethods.GetCurrentThreadId();
        if (thread != _mainThreadId)
        {
            // Cannot happen through the thread's own Dispatcher; if it ever
            // does, retrying here would spin forever (CanRunNow is never true
            // off the main thread), so say it loudly and leave the queue alone.
            Volatile.Write(ref _drainScheduled, 0);
            DevReloadDiagnostics.Report("AcadMainThreadDispatcher.Drain",
                new InvalidOperationException(
                    $"the drain ran on thread {thread}, not the main thread {_mainThreadId}; " +
                    $"{_queue.Count} call(s) left queued."));
            return;
        }

        if (!CanRunNow())
        {
            ReportIfStalled();
            // _drainScheduled stays 1: the retry owns the next drain.
            _ = RetryLaterAsync();
            return;
        }

        _busySince = 0;

        // Cleared before running: work queued while this drain runs schedules its own.
        Volatile.Write(ref _drainScheduled, 0);

        while (_queue.TryDequeue(out var work))
        {
            try { work(); }
            catch (Exception ex)
            {
                // Category B - report, do not rethrow. This runs from the host's
                // message loop: an exception escaping here takes the host down. The
                // work item's own TaskCompletionSource already carries the failure
                // back to its caller, so this line is a second copy for the log,
                // not the only record.
                DevReloadDiagnostics.Report("AcadMainThreadDispatcher: queued work item", ex);
            }
        }
    }

    private async Task RetryLaterAsync()
    {
        try
        {
            await Task.Delay(RetryMs).ConfigureAwait(false);
        }
        finally
        {
            // Whatever happened above, the retry gives up its claim on the
            // next drain; a flag left at 1 would refuse every later one.
            Volatile.Write(ref _drainScheduled, 0);
        }
        if (!_queue.IsEmpty) ScheduleDrain();
    }

    // Main thread only. No blanket timeout drops the work (see the remarks);
    // this only writes down why it is waiting.
    private void ReportIfStalled()
    {
        long now = Environment.TickCount64;
        if (_busySince == 0) { _busySince = now; _busyReportedAt = 0; return; }

        long busy = now - _busySince;
        if (busy < StallReportAfterMs) return;
        if (_busyReportedAt != 0 && now - _busyReportedAt < StallReportEveryMs) return;
        _busyReportedAt = now;

        string context;
        try { context = Application.DocumentManager.IsApplicationContext.ToString(); }
        catch (Exception ex) { context = $"unreadable ({ex.GetType().Name})"; }

        DevReloadDiagnostics.Info(
            $"AcadMainThreadDispatcher: {_queue.Count} main-thread call(s) waiting " +
            $"{busy / 1000} s; application context {context}, modal loop {InModalLoop()}.");
    }

    /// <summary>
    /// Whether tool work may run right now: in application context (not
    /// inside a command, e.g. one waiting at a prompt) and not inside any
    /// modal loop on the main thread. Main thread only.
    /// </summary>
    /// <remarks>
    /// Stricter than <see cref="BlockingModalTitle"/> on purpose. That one must
    /// never drop a call wrongly, so it wants two signals. This one must never
    /// run work inside a dialog, so the modal-loop flag alone is enough: a WPF
    /// dialog owned by something other than the main frame leaves the frame
    /// enabled. A menu or drag loop sets the flag too and costs one retry.
    /// </remarks>
    private static bool CanRunNow()
    {
        try
        {
            return Application.DocumentManager.IsApplicationContext
                && !InModalLoop();
        }
        catch (Exception ex)
        {
            // Category B - report, do not rethrow. "Can't tell" waits for the
            // next retry rather than running work at a possibly unsafe moment.
            DevReloadDiagnostics.Report("AcadMainThreadDispatcher.CanRunNow", ex);
            return false;
        }
    }

    // Called on the main thread, so the current thread is the GUI thread asked about.
    private static bool InModalLoop()
    {
        var gti = new NativeMethods.GUITHREADINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.GUITHREADINFO>(),
        };
        return NativeMethods.GetGUIThreadInfo(NativeMethods.GetCurrentThreadId(), ref gti)
            && (gti.flags & NativeMethods.GUI_INMODALLOOP) != 0;
    }

    public void Dispose()
    {
        // A drain already posted finds _disposed set and returns. Queued items
        // are left to their callers' cancellation and the modal watchdog, as
        // they were when this dispatcher drained on Application.Idle.
        _disposed = true;
    }
}
