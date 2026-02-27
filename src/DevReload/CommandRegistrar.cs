using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.Internal;
using Autodesk.AutoCAD.Runtime;

namespace DevReload
{
    /// <summary>
    /// Registers <c>[CommandMethod]</c> attributes from a loaded assembly via
    /// <see cref="Utils.AddCommand"/> and unregisters them via
    /// <see cref="Utils.RemoveCommand"/> before ALC unload.
    /// <para>
    /// <b>Important:</b> Core (plugin) assemblies must include:<br/>
    /// <c>[assembly: CommandClass(typeof(SomeEmptyClass))]</c><br/>
    /// to suppress AutoCAD's built-in <c>ExtensionLoader.ProcessAssembly</c>
    /// from auto-registering commands via <c>CommandClass.AddCommand</c>.
    /// That internal registry is separate from <c>Utils.AddCommand</c>/
    /// <c>RemoveCommand</c> and cannot be cleaned up by this class.
    /// </para>
    /// </summary>
    /// <remarks>
    /// AutoCAD .NET 8 fires <c>AppDomain.AssemblyLoad</c> for ALL
    /// <c>AssemblyLoadContext</c>s, not just the default. Without the
    /// <c>[assembly: CommandClass]</c> suppression, AutoCAD auto-registers
    /// commands from the isolated ALC and throws <c>eDuplicateKey</c> on reload.
    /// </remarks>
    public class CommandRegistrar
    {
        private readonly List<RegisteredCommand> _commands = new();

        private record RegisteredCommand(
            string Group, string GlobalName, CommandCallback Callback);

        /// <summary>
        /// Gets the number of commands currently registered by this instance.
        /// </summary>
        public int CommandCount => _commands.Count;

        /// <summary>
        /// Scans the assembly for <c>[CommandMethod]</c> attributes and registers
        /// each command with AutoCAD via <see cref="Utils.AddCommand"/>.
        /// <para>
        /// Always scans <b>all exported types</b> regardless of any
        /// <c>[assembly: CommandClass]</c> attribute — that attribute exists
        /// only to suppress AutoCAD's own <c>ExtensionLoader</c>.
        /// </para>
        /// </summary>
        /// <param name="assembly">
        /// The plugin assembly loaded into the isolated ALC.
        /// Typically obtained from <see cref="PluginHost{TPlugin}.LoadedAssembly"/>.
        /// </param>
        /// <param name="defaultGroupName">
        /// Command group name used when <see cref="CommandMethodAttribute.GroupName"/>
        /// is empty. Defaults to the assembly name.
        /// </param>
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

                        // Instance methods: create a new instance per invocation
                        // (matches AutoCAD's normal behavior for [CommandMethod])
                        CommandCallback callback;
                        if (method.IsStatic)
                        {
                            var m = method;
                            callback = () => m.Invoke(null, null);
                        }
                        else
                        {
                            var t = type;
                            var m = method;
                            callback = () =>
                            {
                                var instance = Activator.CreateInstance(t);
                                m.Invoke(instance, null);
                            };
                        }

                        Utils.AddCommand(group, globalName, localName, flags, callback);
                        _commands.Add(new RegisteredCommand(group, globalName, callback));
                    }
                }
            }
        }

        /// <summary>
        /// Unregisters all previously registered commands via
        /// <see cref="Utils.RemoveCommand"/>.
        /// <para>
        /// Must be called <b>before</b> unloading the isolated ALC so that
        /// AutoCAD releases its delegate references and the collectible
        /// context can be garbage-collected.
        /// </para>
        /// </summary>
        public void UnregisterAll()
        {
            foreach (var cmd in _commands)
                Utils.RemoveCommand(cmd.Group, cmd.GlobalName);
            _commands.Clear();
        }
    }
}
