using System.Windows.Controls;
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
    }
}
