using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.Internal;
using Autodesk.AutoCAD.Runtime;

using DevReload.Diagnostics;

namespace DevReload
{
    /// <summary>
    /// Registers [CommandMethod]s from a loaded assembly via Utils.AddCommand
    /// and unregisters them via Utils.RemoveCommand before ALC unload.
    /// </summary>
    public class CommandRegistrar
    {
        private readonly List<RegisteredCommand> _commands = new();

        private record RegisteredCommand(
            string Group, string GlobalName, CommandCallback Callback);

        public int CommandCount => _commands.Count;

        /// <summary>
        /// Scan assembly for [CommandMethod] attributes and register each
        /// with AutoCAD via Utils.AddCommand.
        /// Always scans ALL exported types — ignores [assembly: CommandClass]
        /// (that attribute is only there to suppress AutoCAD's ExtensionLoader).
        /// </summary>
        public void RegisterFromAssembly(Assembly assembly, string? defaultGroupName = null)
        {
            defaultGroupName ??= assembly.GetName().Name ?? "PLUGIN";

            // Always scan all exported types.
            // Do NOT use [assembly: CommandClass] filtering here —
            // that attribute exists only to block AutoCAD's auto-registration.
            Type[] typesToScan = assembly.GetExportedTypes();

            foreach (Type type in typesToScan)
            {
                foreach (MethodInfo method in type.GetMethods(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                {
                    foreach (var attr in method.GetCustomAttributes<CommandMethodAttribute>())
                    {
                        string group = string.IsNullOrEmpty(attr.GroupName)
                            ? defaultGroupName : attr.GroupName;
                        string globalName = attr.GlobalName;
                        string localName = attr.LocalizedNameId ?? globalName;
                        CommandFlags flags = attr.Flags;

                        CommandCallback callback;
                        if (method.IsStatic)
                        {
                            var m = method;
                            callback = () =>
                            {
                                try { m.Invoke(null, null); }
                                catch (System.Exception ex) { ReportException(ex); }
                            };
                        }
                        else
                        {
                            var t = type;
                            var m = method;
                            callback = () =>
                            {
                                try
                                {
                                    var instance = Activator.CreateInstance(t);
                                    m.Invoke(instance, null);
                                }
                                catch (System.Exception ex) { ReportException(ex); }
                            };
                        }

                        Utils.AddCommand(group, globalName, localName, flags, callback);
                        _commands.Add(new RegisteredCommand(group, globalName, callback));
                    }
                }
            }
        }

        private static void ReportException(System.Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            try
            {
                var doc = Autodesk.AutoCAD.ApplicationServices.Application
                    .DocumentManager.MdiActiveDocument;
                doc?.Editor.WriteMessage("\n" + inner.ToString() + "\n");
            }
            catch (System.Exception reportEx)
            {
                // Category B — report, do not rethrow. This method IS the error
                // reporter for a plugin command that already threw; throwing here
                // would replace the plugin's exception with an editor failure and
                // lose the original. The file sink still records both.
                DevReloadDiagnostics.Report("CommandRegistrar.ReportException (editor write)", reportEx);
                DevReloadDiagnostics.Report("CommandRegistrar: original command exception", inner);
            }
        }

        /// <summary>
        /// Unregister all previously registered commands via Utils.RemoveCommand.
        /// Must be called BEFORE unloading the ALC so the collectible context
        /// can be GC'd (no dangling delegate references).
        /// </summary>
        public void UnregisterAll()
        {
            // Collect-then-aggregate. Every command must be attempted even if one
            // RemoveCommand throws: a command left registered holds a delegate into
            // the collectible ALC, which is exactly what stops it being collected.
            // The list is cleared regardless, for the same reason.
            var failures = new List<System.Exception>();
            foreach (var cmd in _commands)
                DevReloadDiagnostics.Step(failures,
                    $"RemoveCommand({cmd.Group}.{cmd.GlobalName})",
                    () => Utils.RemoveCommand(cmd.Group, cmd.GlobalName));
            _commands.Clear();
            DevReloadDiagnostics.ThrowIfAny("CommandRegistrar.UnregisterAll", failures);
        }
    }
}
