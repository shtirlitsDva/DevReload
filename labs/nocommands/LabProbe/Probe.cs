using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

using Autodesk.AutoCAD.Internal;
using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(LabProbe.Commands))]
[assembly: ExtensionApplication(typeof(LabProbe.Startup))]

namespace LabProbe
{
    /// <summary>
    /// Runs the same experiment from AutoCAD start-up instead of from a command.
    /// DevReload auto-loads plugins from inside its own IExtensionApplication.
    /// Initialize, where ExtensionLoader.m_startingUp is still true and
    /// ProcessDeferred takes its other branch — so the suppression has to be
    /// proven there too, not only after start-up.
    /// </summary>
    public class Startup : IExtensionApplication
    {
        public void Initialize()
        {
            if (Environment.GetEnvironmentVariable("DEVRELOAD_LAB_AT_STARTUP") != "1") return;
            Commands.Run();
        }

        public void Terminate() { }
    }

    /// <summary>Stand-in for DevReload's IsolatedPluginContext.</summary>
    internal sealed class LabAlc : AssemblyLoadContext
    {
        public LabAlc(string tag) : base("Lab::" + tag, isCollectible: true) { }
    }

    public static class Commands
    {
        private static string _log = "";
        private static string _dir = "";

        private static void W(string s) => File.AppendAllText(_log, s + Environment.NewLine);

        // ── AutoCAD internals under test ──────────────────────────────

        private static readonly Type RuntimeLoader =
            typeof(Autodesk.AutoCAD.Runtime.ExtensionLoader);          // acdbmgd

        private static readonly Type AppLoader =
            typeof(CommandMethodAttribute).Assembly                     // accoremgd
                .GetType("Autodesk.AutoCAD.ApplicationServices.ExtensionLoader", true)!;

        private static FieldInfo Field(Type t, string n) =>
            t.GetField(n, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingFieldException(t.FullName, n);

        /// <summary>Assembly -> ExtensionApplicationHolder (holds the IExtensionApplication instance).</summary>
        private static Hashtable RuntimeExtensions =>
            (Hashtable)Field(RuntimeLoader, "m_extensions").GetValue(null)!;

        /// <summary>Assembly -> AutoCADApplicationHolder (holds the CommandClasses).</summary>
        private static IDictionary AppExtensions =>
            (IDictionary)Field(AppLoader, "m_extensions").GetValue(null)!;

        private static FieldInfo HandlerField =>
            Field(RuntimeLoader, "m_deferredAssemblyLoadEventHandler");

        // ── The proposed interception ─────────────────────────────────

        private static DeferredAssemblyLoadEventHandler? _saved;

        private static void InstallFilter()
        {
            var original = (DeferredAssemblyLoadEventHandler?)HandlerField.GetValue(null);
            _saved = original;
            DeferredAssemblyLoadEventHandler filtered = (sender, e) =>
            {
                if (AssemblyLoadContext.GetLoadContext(e.LoadedAssembly) is LabAlc)
                {
                    W($"           filter suppressed {e.LoadedAssembly.GetName().Name} " +
                      $"(mayHaveCommands={e.MayHaveCommands}, mayHaveExtApp={e.MayHaveExtensionApplication})");
                    return;
                }
                original?.Invoke(sender, e);
            };
            HandlerField.SetValue(null, filtered);
        }

        private static void RestoreFilter()
        {
            HandlerField.SetValue(null, _saved);
            _saved = null;
        }

        // ── Observations ──────────────────────────────────────────────

        private static bool CmdRegistered(string name) =>
            Utils.IsCommandNameInUse(name) != CommandTypeFlags.NoneCmd;

        private static int Holders(Hashtable h, string asmName) =>
            h.Keys.Cast<object>().Count(k => ((Assembly)k).GetName().Name == asmName);

        private static int Holders(IDictionary d, string asmName) =>
            d.Keys.Cast<object>().Count(k => ((Assembly)k).GetName().Name == asmName);

        // ── One scenario, self-contained ──────────────────────────────
        //
        // Everything that could root the ALC — the ALC, the Assembly, the
        // plugin instance — stays inside this frame and is dead by the time
        // the caller runs the GC. That is the whole point: the earlier
        // version of this lab kept the ALC in a local across the collection
        // and so could not tell a real leak from its own reference.

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference RunScenario(string asmName, bool filter)
        {
            var alc = new LabAlc(asmName + (filter ? "+filter" : ""));
            byte[] bytes = File.ReadAllBytes(Path.Combine(_dir, asmName + ".dll"));

            // Holders leaked by an earlier scenario are still in these tables,
            // so only the delta this load causes is meaningful.
            int rtBefore = Holders(RuntimeExtensions, asmName);
            int appBefore = Holders(AppExtensions, asmName);
            // A lisp defun is a process-global name that nothing here can undo,
            // so only "did THIS load define it" is meaningful.
            bool lispBefore = Utils.IsLispCommandDefined("labfn");

            if (filter) InstallFilter();
            Assembly asm;
            try
            {
                using var ms = new MemoryStream(bytes);
                asm = alc.LoadFromStream(ms);
            }
            finally { if (filter) RestoreFilter(); }

            W($"           command LABPING registered by AutoCAD : {CmdRegistered("LABPING")}");
            W($"           lisp    labfn   defined by AutoCAD    : " +
              (lispBefore ? "(already defined before this load)"
                          : Utils.IsLispCommandDefined("labfn").ToString()));
            W($"           new ExtensionApplicationHolder (acdbmgd)  : {Holders(RuntimeExtensions, asmName) - rtBefore}");
            W($"           new AutoCADApplicationHolder   (accoremgd): {Holders(AppExtensions, asmName) - appBefore}");

            // Leave the command stack as we found it, exactly the way DevReload
            // would have to. Group name for a [CommandMethod] with no GroupName
            // is the declaring assembly's full name.
            if (CmdRegistered("LABPING"))
            {
                Utils.RemoveCommand(asm.FullName!, "LABPING");
                W($"           after Utils.RemoveCommand(fullName, ..)  : {CmdRegistered("LABPING")}");
                W($"           ..but the lisp defun labfn survives it   : {Utils.IsLispCommandDefined("labfn")}");
            }

            alc.Unload();
            return new WeakReference(alc);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool StillAlive(WeakReference r)
        {
            for (int i = 0; i < 10; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            return r.IsAlive;
        }

        private static void Scenario(string title, string asmName, bool filter)
        {
            W("");
            W($"-- {title}");
            bool alive = StillAlive(RunScenario(asmName, filter));
            W($"           ALC still alive after Unload + 10x GC   : {alive}");
        }

        // ── The experiment ────────────────────────────────────────────

        [CommandMethod("LABRUN")]
        public static void Run()
        {
            _log = Environment.GetEnvironmentVariable("DEVRELOAD_LAB_LOG")
                   ?? throw new InvalidOperationException("DEVRELOAD_LAB_LOG unset");
            _dir = Environment.GetEnvironmentVariable("DEVRELOAD_LAB_DIR")
                   ?? throw new InvalidOperationException("DEVRELOAD_LAB_DIR unset");

            try { RunCore(); }
            catch (System.Exception ex) { W("FATAL: " + ex); }
        }

        /// <summary>
        /// Does Utils.AddCommand throw when AutoCAD already registered that name?
        /// It matters beyond the rejected option-2: CommandRegistrar registers
        /// under the assembly SIMPLE name while AutoCAD uses the FULL name, so if
        /// different groups are tolerated a failed suppression would produce a
        /// silent duplicate instead of a loud error.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void DuplicateProbe()
        {
            var alc = new LabAlc("dup");
            Assembly asm;
            using (var ms = new MemoryStream(File.ReadAllBytes(Path.Combine(_dir, "LabPlugin.dll"))))
                asm = alc.LoadFromStream(ms);

            W($"           AutoCAD registered LABPING : {CmdRegistered("LABPING")}");
            W($"           AutoCAD's group            : \"{asm.FullName}\"");
            W($"           CommandRegistrar's group   : \"{asm.GetName().Name}\"");

            TryAdd(asm.GetName().Name!, "different group");
            TryAdd(asm.FullName!, "same group   ");

            Utils.RemoveCommand(asm.FullName!, "LABPING");
        }

        private static void TryAdd(string group, string label)
        {
            try
            {
                Utils.AddCommand(group, "LABPING", "LABPING", CommandFlags.Modal, () => { });
                W($"           AddCommand, {label} : succeeded");
                Utils.RemoveCommand(group, "LABPING");
            }
            catch (System.Exception ex)
            {
                W($"           AddCommand, {label} : {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void RunCore()
        {
            bool startingUp = (bool)Field(RuntimeLoader, "m_startingUp").GetValue(null)!;
            W($"-- 0. ExtensionLoader.m_startingUp = {startingUp}");
            W("     subscribers to ExtensionLoader.DeferredAssemblyLoad");
            var handlers = ((Delegate?)HandlerField.GetValue(null))?.GetInvocationList()
                .Select(d => d.Method.DeclaringType!.FullName + "." + d.Method.Name);
            foreach (var h in handlers ?? Enumerable.Empty<string>()) W("           " + h);

            // Ordered so the lisp-defun observation is never masked by a
            // previous scenario having defined the same global name.
            Scenario("1. unprepared plugin + DeferredAssemblyLoad filter (proposed)",
                     "LabPlugin", filter: true);

            Scenario("2. NoCommands marker, no interception (DevReload as it ships)",
                     "LabPluginMarked", filter: false);

            Scenario("3. unprepared plugin, no interception (today's failure mode)",
                     "LabPlugin", filter: false);

            // Safety check: the filter must only ever touch assemblies DevReload
            // owns. Load one into the DEFAULT ALC while the filter is installed
            // and confirm AutoCAD still processes it normally.
            W("");
            W("-- 4. filter installed, but assembly loaded into the DEFAULT ALC");
            int defBefore = Holders(RuntimeExtensions, "LabPlugin");
            InstallFilter();
            try
            {
                Assembly def = Assembly.LoadFrom(Path.Combine(_dir, "LabPlugin.dll"));
                W($"           command LABPING registered by AutoCAD : {CmdRegistered("LABPING")}");
                W($"           new ExtensionApplicationHolder (acdbmgd)  : {Holders(RuntimeExtensions, "LabPlugin") - defBefore}");
                if (CmdRegistered("LABPING")) Utils.RemoveCommand(def.FullName!, "LABPING");
            }
            finally { RestoreFilter(); }

            W("");
            W("-- 5. registering a command AutoCAD already auto-registered");
            DuplicateProbe();

            W("");
            W("== done ==");
        }
    }
}
