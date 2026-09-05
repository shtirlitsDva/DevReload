using System;
using System.Collections.Generic;
using System.IO;

namespace DevReload.Diagnostics
{
    /// <summary>
    /// The one place a swallowed exception is allowed to end up: a durable
    /// file, plus the host's own message surface when there is one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shared as SOURCE rather than as an assembly because the five consuming
    /// projects have no common reference: Acad.Rpc.Core is net8-only and cannot
    /// be referenced from the net48 Revit hosts (R22/R23/R24), and
    /// DevReload.BuildCore is not imported by Acad.Rpc.Bridge or Acad.Process.
    /// </para>
    /// <para>
    /// Deliberately <c>internal</c>, mirroring <c>Compat.cs</c> in BuildCore:
    /// each consuming assembly compiles its own copy. A <c>public</c> type here
    /// would collide (CS0433) inside DevReload.dll, which both compiles this
    /// file and references Acad.Rpc.Core, which also compiles it.
    /// </para>
    /// <para>
    /// The per-assembly copy has one consequence worth knowing: <see cref="HostWriter"/>
    /// is set independently in each assembly. DevReload.dll wires it to the AutoCAD
    /// Editor, so the whole plugin lifecycle path reports to the command line. The
    /// copies inside Acad.Rpc.Core / Acad.Rpc.Bridge / Acad.Process are file-only,
    /// which is correct — the bridge and the process controller run where no Editor
    /// exists.
    /// </para>
    /// </remarks>
    internal static class DevReloadDiagnostics
    {
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DevReload", "devreload.log");

        private static readonly object Gate = new object();

        /// <summary>
        /// Host-supplied message surface (the AutoCAD Editor, in practice).
        /// Null where the host has none. Set once during host startup.
        /// </summary>
        internal static Action<string>? HostWriter { get; set; }

        /// <summary>
        /// Record a failure. Callers decide separately whether to rethrow —
        /// this only guarantees the exception stops being invisible.
        /// </summary>
        internal static void Report(string context, Exception ex)
        {
            if (ex is null) throw new ArgumentNullException(nameof(ex));
            Write($"{context} FAILED: {ex.GetType().Name}: {ex.Message}", ex.ToString());
        }

        /// <summary>Record a non-failure event.</summary>
        internal static void Info(string message) => Write(message, detail: null);

        /// <summary>
        /// Category B: run a best-effort step that must not throw. Reports the
        /// failure and continues. For use ONLY where rethrowing is actively
        /// harmful — a finally block or cancellation path, where throwing would
        /// replace the fault that got us there; a fire-and-forget task, where it
        /// would surface as an unobserved fault; or an event-loop callback, where
        /// it would take the loop down. Everywhere else use <see cref="Step"/>.
        /// </summary>
        internal static void RunReporting(string context, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Report(context, ex);
            }
        }

        /// <summary>
        /// Category B: release a resource while unwinding. Null-tolerant, so the
        /// usual <c>writer?.Dispose()</c> shape survives the conversion.
        /// </summary>
        internal static void DisposeReporting(IDisposable? resource, string what)
        {
            if (resource is null) return;
            RunReporting($"{what}.Dispose", resource.Dispose);
        }

        /// <summary>
        /// Run one teardown step. Reports a failure and records it in
        /// <paramref name="failures"/> for the caller to rethrow, without letting
        /// it skip the steps that follow. Pair with <see cref="ThrowIfAny"/>.
        /// </summary>
        internal static void Step(List<Exception> failures, string context, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Report(context, ex);
                failures.Add(ex);
            }
        }

        /// <summary>
        /// Throw the collected failures, if any, as one. The teardown idiom: run
        /// every step, report each failure as it happens, then surface the lot
        /// without having let an early failure skip the later cleanup.
        /// </summary>
        internal static void ThrowIfAny(string context, List<Exception> failures)
        {
            if (failures is null || failures.Count == 0) return;
            if (failures.Count == 1) throw failures[0];
            throw new AggregateException($"{context}: {failures.Count} teardown steps failed", failures);
        }

        private static void Write(string headline, string? detail)
        {
            // File first — the only channel that survives the Editor being
            // absent, which is the normal case during Initialize before any
            // document is open, in both Revit hosts, and in Acad.Process.
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                    // Process.GetCurrentProcess().Id, not Environment.ProcessId:
                    // the latter is .NET 5+ and this file also compiles on net48
                    // for the Revit 2022-2024 hosts.
                    int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                    string stamp = $"{DateTime.Now:O} [{pid}]";
                    string body = detail is null
                        ? $"{stamp} {headline}{Environment.NewLine}"
                        : $"{stamp} {headline}{Environment.NewLine}{detail}{Environment.NewLine}";
                    File.AppendAllText(LogPath, body);
                }
            }
            catch (Exception logEx)
            {
                // The terminal fallback, and the one catch in this repo that
                // cannot rethrow: this method is called FROM catch blocks, so
                // throwing here would replace the original fault with a disk
                // error and lose the thing we were trying to report.
                System.Diagnostics.Debug.WriteLine(
                    $"[DevReload] diagnostics file write failed: {logEx}");
            }

            try
            {
                HostWriter?.Invoke(Environment.NewLine + "[DevReload] " + headline);
            }
            catch (Exception hostEx)
            {
                // Same reason as above. A dead Editor must not mask the fault
                // we already wrote to disk.
                System.Diagnostics.Debug.WriteLine(
                    $"[DevReload] diagnostics host write failed: {hostEx}");
            }
        }
    }
}
