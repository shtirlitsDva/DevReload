using System.Windows.Controls;

using DevReload.ViewModels;

namespace DevReload.Views
{
    /// <summary>
    /// The .NET tab of the DEVRELOAD palette. Shares its view-model with
    /// <see cref="OarxPanel"/> — see the remarks there.
    /// </summary>
    public partial class DevReloadPanel : UserControl
    {
        public DevReloadPanel(DevReloadViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }

        // The worktree list is enumerated lazily (a dotnet/git query) the moment
        // the user opens the combo, so it stays current without polling.
        private void WorktreeComboBox_DropDownOpened(object sender, System.EventArgs e)
        {
            if (sender is ComboBox combo && combo.DataContext is PluginItemViewModel vm)
                vm.RefreshWorktrees();
        }
    }
}
