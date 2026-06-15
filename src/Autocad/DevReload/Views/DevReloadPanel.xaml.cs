using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DevReload.ViewModels;

namespace DevReload.Views
{
    public partial class DevReloadPanel : UserControl
    {
        public DevReloadPanel()
        {
            InitializeComponent();
            DataContext = new DevReloadViewModel();
        }

        private void WorktreeComboBox_DropDownOpened(object sender, System.EventArgs e)
        {
            if (sender is ComboBox combo && combo.DataContext is PluginItemViewModel vm)
                vm.RefreshWorktrees();
        }

        // "Build only" lives inside a Popup, so a RelativeSource FindAncestor
        // binding to the ItemsControl's DataContext can't reach the root VM
        // (popup content is detached from the visual tree). Invoke the command
        // from code-behind instead, then close the flyout.
        private void BuildOnlyFlyout_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe
                && fe.DataContext is PluginItemViewModel item
                && DataContext is DevReloadViewModel vm
                && vm.BuildOnlyPluginCommand.CanExecute(item.Name))
            {
                vm.BuildOnlyPluginCommand.Execute(item.Name);
            }

            CloseAncestorPopup(sender as DependencyObject);
        }

        // Config-picker items live in a Popup, so (like the Build-only flyout)
        // their DataContext is the config string, not the plugin VM. Reach the VM
        // via the popup's PlacementTarget — the ConfigDrop toggle, whose
        // DataContext IS the item VM — then assign the chosen configuration and
        // close the popup.
        private void ConfigItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Content is not string config)
                return;

            Popup? popup = FindAncestorPopup(btn);
            if (popup?.PlacementTarget is FrameworkElement target
                && target.DataContext is PluginItemViewModel item)
            {
                item.SelectedConfiguration = config;
            }

            if (popup != null)
                popup.IsOpen = false;
        }

        private static void CloseAncestorPopup(DependencyObject? d)
        {
            if (FindAncestorPopup(d) is Popup popup)
                popup.IsOpen = false;
        }

        private static Popup? FindAncestorPopup(DependencyObject? d)
        {
            while (d != null && d is not Popup)
                d = LogicalTreeHelper.GetParent(d);
            return d as Popup;
        }
    }
}
