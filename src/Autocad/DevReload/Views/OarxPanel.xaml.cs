using System.Windows.Controls;

using DevReload.ViewModels;

namespace DevReload.Views
{
    /// <summary>
    /// The OARX tab of the DEVRELOAD palette.
    /// </summary>
    /// <remarks>
    /// A second <c>AddVisual</c> on the same PaletteSet, not a WPF TabControl
    /// inside one panel — AutoCAD owns the tab chrome so the palette looks and
    /// behaves like every other AutoCAD palette.
    /// <para>
    /// It shares ONE <see cref="DevReloadViewModel"/> with the .NET panel: the
    /// view-model subscribes to both plugin registries, so a second instance
    /// would double every registry event and project the same plugins twice.
    /// The shared instance is supplied by the caller, hence no parameterless
    /// constructor.
    /// </para>
    /// </remarks>
    public partial class OarxPanel : UserControl
    {
        public OarxPanel(DevReloadViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }

        // The worktree list is enumerated lazily (a git query) the moment the
        // user opens the combo, so it stays current without polling.
        private void WorktreeComboBox_DropDownOpened(object sender, System.EventArgs e)
        {
            if (sender is ComboBox combo && combo.DataContext is OarxPluginItemViewModel vm)
                vm.RefreshWorktrees();
        }
    }
}
