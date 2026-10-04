using System.Windows;
using System.Windows.Controls;

namespace DevReload.Views
{
    /// <summary>
    /// Vertical tabs down the left edge, the look of AutoCAD's PaletteSet tabs,
    /// for a host whose panel has none: BricsCAD's <c>Panel</c> holds one
    /// visual, so the manager puts its .NET and OARX views in one of these.
    /// AutoCAD keeps the PaletteSet's own tabs.
    /// </summary>
    /// <remarks>
    /// Pages stay in the tree and are only hidden, the way a PaletteSet
    /// switches tabs, so a page keeps its scroll and expander state across
    /// switches. The first tab added is selected.
    /// </remarks>
    public partial class SideTabs : UserControl
    {
        public SideTabs() => InitializeComponent();

        public void Add(string header, UIElement page)
        {
            // RadioButtons in one panel form one group: checking a tab
            // unchecks the others.
            var tab = new RadioButton { Content = header };
            tab.SetResourceReference(StyleProperty, "SideTab");
            page.Visibility = Visibility.Collapsed;
            tab.Checked += (_, _) => page.Visibility = Visibility.Visible;
            tab.Unchecked += (_, _) => page.Visibility = Visibility.Collapsed;

            Strip.Children.Add(tab);
            Pages.Children.Add(page);
            if (Strip.Children.Count == 1) tab.IsChecked = true;
        }
    }
}
