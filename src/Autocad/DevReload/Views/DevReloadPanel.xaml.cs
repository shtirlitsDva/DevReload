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

        private static void CloseAncestorPopup(DependencyObject? d)
        {
            while (d != null && d is not Popup)
                d = LogicalTreeHelper.GetParent(d);
            if (d is Popup popup)
                popup.IsOpen = false;
        }
    }
}
