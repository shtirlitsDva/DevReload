using System;
using System.Windows;
using System.Windows.Controls;

namespace Example.Plugin.Views
{
    /// <summary>
    /// WPF UserControl hosted inside <see cref="ExamplePaletteSet"/>.
    /// Demonstrates that WPF views loaded from an isolated ALC can interact
    /// with AutoCAD's editor and update on hot-reload.
    /// </summary>
    public partial class ExamplePaletteView : UserControl
    {
        public ExamplePaletteView()
        {
            InitializeComponent();
        }

        private void TestButton_Click(object sender, RoutedEventArgs e)
        {
            var ed = Autodesk.AutoCAD.ApplicationServices.Application
                .DocumentManager.MdiActiveDocument?.Editor;
            ed?.WriteMessage($"\nButton clicked from isolated ALC! Time: {DateTime.Now:HH:mm:ss}");
        }
    }
}
