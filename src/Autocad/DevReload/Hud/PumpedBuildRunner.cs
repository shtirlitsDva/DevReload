using System;
using System.Collections.Generic;
using System.Diagnostics;

using DevReload.Core;

namespace DevReload.Hud
{
    /// <summary>
    /// Runs a build while AutoCAD keeps painting, streaming every output line into
    /// the HUD.
    /// </summary>
    /// <remarks>
    /// The port of LoaderUnloader's <c>runPumped</c>. Both reload cycles build from
    /// AutoCAD's main thread, so a plain <c>WaitForExit</c> greys the window out for
    /// the whole compile and Windows paints "Not Responding" over it. This waits in
    /// short slices instead and pumps paint messages between them — see
    /// <see cref="ReloadHud.PumpPaint"/> for why input is dropped while it does.
    /// </remarks>
    internal sealed class PumpedBuildRunner : IBuildProcessRunner
    {
        private readonly IReloadProgress _progress;

        public PumpedBuildRunner(IReloadProgress progress) => _progress = progress;

        public int Run(ProcessStartInfo startInfo, Action<string> onLine)
        {
            // The child's streams are read on the thread-pool threads Process gives
            // us, but the HUD may only be touched from the main thread — so lines
            // are queued here and drained by the pump loop below.
            var pending = new Queue<string>();

            using var proc = new Process { StartInfo = startInfo };
            proc.OutputDataReceived += (_, e) => Enqueue(pending, e.Data);
            proc.ErrorDataReceived += (_, e) => Enqueue(pending, e.Data);
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            while (!proc.WaitForExit(ReloadHud.FrameMs))
            {
                Drain(pending, onLine);

                // No HUD on screen means nothing to animate — and nothing that
                // would justify pumping AutoCAD's message loop. That matters at
                // startup auto-load, where a missing DLL triggers a build from
                // inside Initialize and pumping there would run the loop
                // reentrantly during AutoCAD's own start-up.
                if (!ReloadHud.IsShown) continue;

                ReloadHud.PumpPaint();
                ReloadHud.Tick();
            }

            // WaitForExit(int) returning true means the process ended, but the
            // async output readers may still be mid-flush; the parameterless
            // overload is what waits for them.
            proc.WaitForExit();
            Drain(pending, onLine);
            ReloadHud.Tick();
            return proc.ExitCode;
        }

        private static void Enqueue(Queue<string> q, string? data)
        {
            if (data == null) return;
            lock (q) q.Enqueue(data);
        }

        private void Drain(Queue<string> q, Action<string> onLine)
        {
            while (true)
            {
                string line;
                lock (q)
                {
                    if (q.Count == 0) return;
                    line = q.Dequeue();
                }
                onLine(line);           // the full build log, verbatim
                _progress.Line(Tidy(line));
            }
        }

        /// <summary>One line of child output as it should appear in the panel.</summary>
        /// <remarks>
        /// MSBuild's parallel build appends " [&lt;full path&gt;.vcxproj]" to every
        /// diagnostic, which costs half the panel width and says nothing the
        /// project name above it did not. The build log keeps the full text.
        /// </remarks>
        private static string Tidy(string line)
        {
            string s = line.TrimEnd();
            if (s.EndsWith("]", StringComparison.Ordinal))
            {
                int tag = s.LastIndexOf(" [", StringComparison.Ordinal);
                if (tag > 0) s = s.Substring(0, tag);
            }
            return s.Trim();
        }
    }
}
