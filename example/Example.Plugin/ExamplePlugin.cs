using DevReload;

namespace Example.Plugin
{
    /// <summary>
    /// Entry point for the hot-reloadable plugin. Implements <see cref="IPlugin"/>
    /// which is discovered by <see cref="PluginHost{TPlugin}"/> via reflection.
    /// </summary>
    public class ExamplePlugin : IPlugin
    {
        /// <inheritdoc/>
        public void Initialize()
        {
            // Subscribe to AutoCAD events or perform one-time setup here.
            // This is called after each reload.
        }

        /// <inheritdoc/>
        public object CreatePaletteSet()
        {
            return new ExamplePaletteSet();
        }

        /// <inheritdoc/>
        public void Terminate()
        {
            // Unsubscribe from AutoCAD events or cleanup here.
            // This is called before each unload.
        }
    }
}
