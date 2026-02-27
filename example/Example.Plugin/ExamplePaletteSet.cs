using System;
using Autodesk.AutoCAD.Windows;
using Example.Plugin.Views;

namespace Example.Plugin
{
    /// <summary>
    /// A <see cref="PaletteSet"/> that hosts the <see cref="ExamplePaletteView"/>
    /// WPF UserControl. Created by <see cref="ExamplePlugin.CreatePaletteSet"/>
    /// and managed by the Loader.
    /// </summary>
    public class ExamplePaletteSet : PaletteSet
    {
        private static readonly Guid PaletteSetId =
            new Guid("2b1eb48c-4205-4805-b5a6-346c727aa0ee");

        public ExamplePaletteSet()
            : base("DevReload Example", "DEVRELOADEXAMPLE", PaletteSetId)
        {
            Style = PaletteSetStyles.ShowCloseButton
                  | PaletteSetStyles.ShowPropertiesMenu;

            AddVisual("Example", new ExamplePaletteView());
        }
    }
}
