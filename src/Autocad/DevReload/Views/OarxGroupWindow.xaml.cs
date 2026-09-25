using System.ComponentModel;
using System.Windows;

using DevReload.ViewModels;

using WpfSHARED;

namespace DevReload.Views
{
    /// <summary>
    /// Add or edit one OARX group: its own fields and its profiles.
    /// </summary>
    public partial class OarxGroupWindow : Window
    {
        private readonly OarxGroupEditorViewModel _vm;

        public OarxGroupWindow(OarxGroupEditorViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;
            DarkTitleBar.ApplyTheme(this);
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        // Unsaved edits are never dropped without asking.
        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_vm.ConfirmClose()) e.Cancel = true;
            base.OnClosing(e);
        }
    }
}
