using System.Windows;

using WpfSHARED;

namespace RevitDevReload.Ui
{
    public partial class ManagerWindow : Window
    {
        private readonly ManagerViewModel _vm;

        public ManagerWindow()
        {
            InitializeComponent();
            _vm = new ManagerViewModel();
            DataContext = _vm;
            Closed += (_, _) => _vm.Detach();

            // Match the OS title bar to the merged Theme.xaml palette so the
            // dark window no longer wears a white caption.
            DarkTitleBar.ApplyTheme(this);
        }
    }
}
