namespace DevReload
{
    /// <summary>
    /// Contract between a Loader assembly (permanent, in default ALC) and its
    /// hot-reloadable plugin (Core, in isolated collectible ALC).
    /// <para>
    /// Implement this interface in the Core assembly. The Loader discovers the
    /// implementation via reflection after loading the Core into an
    /// <see cref="IsolatedPluginContext"/>.
    /// </para>
    /// <para>
    /// This assembly must be referenced by both Loader and Core with
    /// <c>&lt;Private&gt;false&lt;/Private&gt;</c> in the Core project so that
    /// both sides share the same type identity from the default ALC.
    /// </para>
    /// </summary>
    public interface IPlugin
    {
        /// <summary>
        /// Called once after the plugin assembly is loaded into the isolated ALC.
        /// Use this to subscribe to AutoCAD events or perform one-time initialization.
        /// </summary>
        void Initialize();

        /// <summary>
        /// Creates and returns a <c>PaletteSet</c> instance (or any UI object).
        /// The Loader casts the return value to
        /// <c>Autodesk.AutoCAD.Windows.PaletteSet</c> and manages its
        /// visibility and lifecycle.
        /// <para>
        /// Returns <see cref="object"/> rather than <c>PaletteSet</c> to avoid
        /// requiring AutoCAD references in this interface assembly.
        /// </para>
        /// </summary>
        /// <returns>A PaletteSet instance, boxed as <see cref="object"/>.</returns>
        object CreatePaletteSet();

        /// <summary>
        /// Called before the plugin assembly is unloaded. Use this to unsubscribe
        /// from AutoCAD events and release any resources held by the plugin.
        /// </summary>
        void Terminate();
    }
}
