using System;
using System.IO;

using Autodesk.AutoCAD.Runtime;

// Built twice. Without MARKED this is the "unprepared plugin" the lab is
// about; with MARKED it carries the NoCommands marker DevReload requires
// today, so it models a correctly-prepared plugin.
[assembly: ExtensionApplication(typeof(LabPlugin.Ext))]
#if MARKED
[assembly: CommandClass(typeof(LabPlugin.NoCommands))]
#endif

namespace LabPlugin
{
#if MARKED
    public class NoCommands { }
#endif

    internal static class Log
    {
        /// <summary>
        /// A fixed path, not an env var: this plugin also gets loaded by an
        /// AutoCAD that DevReload's MCP bridge started, which does not inherit
        /// the shell environment the lab script runs in.
        /// </summary>
        internal static readonly string Path = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "devreload-lab.log");

        internal static void W(string s) =>
            File.AppendAllText(Path, "    plugin| " + s + Environment.NewLine);
    }

    public class Ext : IExtensionApplication
    {
        public void Initialize() => Log.W($"Ext.Initialize   hash={GetHashCode()}");
        public void Terminate() => Log.W($"Ext.Terminate    hash={GetHashCode()}");
    }

    public class Cmds
    {
        [CommandMethod("LABPING")]
        public static void Ping() => Log.W("LABPING invoked");

        [LispFunction("labfn")]
        public static object Fn(Autodesk.AutoCAD.DatabaseServices.ResultBuffer args)
        {
            Log.W("labfn invoked");
            return 1;
        }
    }
}
