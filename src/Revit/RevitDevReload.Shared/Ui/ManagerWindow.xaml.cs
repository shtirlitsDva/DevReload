using System.Windows;

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
        }
    }
}
