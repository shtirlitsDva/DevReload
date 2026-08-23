using System;
using System.Reflection;
using System.Runtime.Loader;

using Autodesk.AutoCAD.Runtime;

using DevReload.Core;

namespace DevReload
{
    /// <summary>
    /// Stops AutoCAD from processing the assemblies DevReload loads, so plugins
    /// no longer have to carry a <c>NoCommands</c> marker class.
    /// </summary>
    /// <remarks>
    /// <para>
    /// AutoCAD hooks <c>AppDomain.AssemblyLoad</c>, tags each assembly with
    /// <c>MayHaveCommands</c> (it references accoremgd) and
    /// <c>MayHaveExtensionApplication</c> (it references acdbmgd), then raises it
    /// on the public static event
    /// <c>Autodesk.AutoCAD.Runtime.ExtensionLoader.DeferredAssemblyLoad</c>.
    /// Two subscribers act on that event and between them do everything DevReload
    /// wants to own: one registers every <c>[CommandMethod]</c> it can find, the
    /// other instantiates the <c>IExtensionApplication</c> and calls
    /// <c>Initialize</c> on it.
    /// </para>
    /// <para>
    /// The event carries no cancellation, so the only way in is its backing
    /// delegate. This replaces it with a wrapper that drops assemblies loaded into
    /// an <see cref="IsolatedPluginContext"/> and forwards everything else
    /// untouched. The test is which load context the assembly is in, not when it
    /// arrived, so there is no window to race and no way to suppress an assembly
    /// DevReload does not own. Plugin dependencies land in the same context and
    /// are covered too, which they need to be: AutoCAD scans those today as well.
    /// </para>
    /// <para>
    /// Consequences for the rest of DevReload: AutoCAD no longer builds its own
    /// instance of the plugin, so <c>PluginManager.LoadCore</c> has to call
    /// <c>Initialize</c> itself. That is the fix for the dual-instance problem,
    /// not a new cost. It also means the collectible ALC can actually unload,
    /// because the only thing that was pinning it was AutoCAD's static table of
    /// plugin instances.
    /// </para>
    /// <para>
    /// The one private member this depends on is the event's backing field. If a
    /// future AutoCAD renames it, <see cref="Install"/> throws rather than
    /// quietly leaving the scan on, and the caller reports it.
    /// </para>
    /// </remarks>
    internal static class AutoCadScanSuppressor
    {
        private const string BackingField = "m_deferredAssemblyLoadEventHandler";

        private static DeferredAssemblyLoadEventHandler? _installed;
        private static DeferredAssemblyLoadEventHandler? _original;

        /// <summary>
        /// True once the scan is suppressed. While this is false AutoCAD still
        /// registers commands and calls Initialize on its own instance, so
        /// plugins need the NoCommands marker and DevReload must not call
        /// Initialize a second time.
        /// </summary>
        internal static bool IsActive => _installed != null;

        internal static void Install()
        {
            if (_installed != null) return;

            FieldInfo field = Handler();
            var original = (DeferredAssemblyLoadEventHandler?)field.GetValue(null);

            DeferredAssemblyLoadEventHandler filtered = (sender, e) =>
            {
                if (AssemblyLoadContext.GetLoadContext(e.LoadedAssembly)
                        is IsolatedPluginContext)
                    return;

                original?.Invoke(sender, e);
            };

            field.SetValue(null, filtered);
            _original = original;
            _installed = filtered;
        }

        internal static void Restore()
        {
            if (_installed == null) return;

            // Anything that subscribed after us combined onto our wrapper, so the
            // field is no longer just it. Overwriting then would silently drop
            // that subscriber; leaving the wrapper in place costs nothing, since
            // this only runs as AutoCAD shuts down.
            FieldInfo field = Handler();
            if (ReferenceEquals(field.GetValue(null), _installed))
                field.SetValue(null, _original);

            _installed = null;
            _original = null;
        }

        private static FieldInfo Handler()
        {
            FieldInfo? field = typeof(ExtensionLoader).GetField(
                BackingField, BindingFlags.NonPublic | BindingFlags.Static);

            if (field == null)
                throw new InvalidOperationException(
                    $"Autodesk.AutoCAD.Runtime.ExtensionLoader.{BackingField} is gone. " +
                    "This AutoCAD version does not expose the assembly-scan hook " +
                    "DevReload suppresses.");

            if (field.FieldType != typeof(DeferredAssemblyLoadEventHandler))
                throw new InvalidOperationException(
                    $"Autodesk.AutoCAD.Runtime.ExtensionLoader.{BackingField} is " +
                    $"{field.FieldType.Name}, expected DeferredAssemblyLoadEventHandler.");

            return field;
        }
    }
}
