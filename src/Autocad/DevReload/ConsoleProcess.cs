using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DevReload
{
    /// <summary>The CAD this build of DevReload runs in.</summary>
    public enum CadHost
    {
        AutoCad,
        BricsCad,
    }

    /// <summary>
    /// Is this process a UI-less console of the host? DevReload does nothing in
    /// one: no RPC server, no window, status-bar or palette reads, no build, no
    /// autoload. Decided from what the process states about itself (its image
    /// and its command line), never from a guess, and before anything touches
    /// the UI. Host-free, so it is unit-tested.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><b>BricsCAD</b> has no core console. Its console is bricscad.exe
    /// started with <c>/automation</c>, which "starts BricsCAD without
    /// displaying the main frame window" (Bricsys, Startup options); NSSM's
    /// PDF plot starts N of them (<c>/b script /automation /nologo</c>), and COM
    /// registers BricsCAD's local server as <c>bricscad.exe /Automation</c>.
    /// Measured 2026-10-06 (BricsCAD V26): DevReload autoloaded into all six
    /// plot consoles, read Application.MainWindow and started msbuild; two
    /// consoles then died at QUIT in BrxMgd's WindowFromHandle finalizer.</item>
    /// <item><b>AutoCAD</b>'s console is its own image, accoreconsole.exe.
    /// <c>acad.exe /Automation</c> is NOT a console: a COM-started AutoCAD has
    /// its full UI and may be made visible, so the switch is not read there.</item>
    /// </list>
    /// </remarks>
    public static class ConsoleProcess
    {
        /// <summary>Why this process is a console, in a sentence for the log, or
        /// null when it is the interactive host.</summary>
        /// <param name="imagePath">The process image (path or file name).</param>
        /// <param name="arguments">The command line, without the image.</param>
        public static string? Reason(CadHost host, string? imagePath, IEnumerable<string> arguments)
        {
            string image = Path.GetFileName(imagePath ?? "");
            if (image.Equals("accoreconsole.exe", StringComparison.OrdinalIgnoreCase))
                return "the process is accoreconsole.exe, AutoCAD's UI-less console";

            if (host == CadHost.BricsCad
                && arguments.FirstOrDefault(a => IsSwitch(a, "automation")) is string sw)
                return $"BricsCAD was started with {sw}, which shows no main window (a UI-less console)";

            return null;
        }

        /// <summary><see cref="Reason"/> for the running process.</summary>
        public static string? Current(CadHost host) =>
            Reason(host, Environment.ProcessPath, Environment.GetCommandLineArgs().Skip(1));

        private static bool IsSwitch(string arg, string name) =>
            arg.Length == name.Length + 1
            && (arg[0] == '/' || arg[0] == '-')
            && string.Compare(arg, 1, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0;
    }
}
