using System;
using System.Diagnostics;

namespace DevReload.Core
{
    /// <summary>
    /// How <see cref="BuildService"/> runs the build child process.
    /// </summary>
    /// <remarks>
    /// A seam, not a general abstraction. It exists for exactly one reason: the
    /// .NET reload builds while the old plugin is still running, so blocking the
    /// caller for the duration costs nothing — but the OARX reload has already
    /// UNLOADED the modules by the time it builds, and it does that on AutoCAD's
    /// main thread. A plain <c>WaitForExit</c> there pins the message loop for the
    /// whole compile: the window greys out and Windows paints "Not Responding"
    /// over it. The OARX host supplies a runner that pumps paint messages instead
    /// and streams the log into its HUD.
    /// </remarks>
    public interface IBuildProcessRunner
    {
        /// <summary>Run the child to completion and return its exit code. Every
        /// complete line of its stdout/stderr is handed to <paramref name="onLine"/>
        /// as it arrives.</summary>
        int Run(ProcessStartInfo startInfo, Action<string> onLine);
    }

    /// <summary>
    /// Reads both streams asynchronously and waits. The right runner whenever the
    /// caller is not the UI thread the child's duration would freeze.
    /// </summary>
    public sealed class DefaultBuildProcessRunner : IBuildProcessRunner
    {
        public static readonly DefaultBuildProcessRunner Instance = new();

        public int Run(ProcessStartInfo startInfo, Action<string> onLine)
        {
            using var proc = new Process { StartInfo = startInfo };
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            proc.WaitForExit();
            return proc.ExitCode;
        }
    }
}
