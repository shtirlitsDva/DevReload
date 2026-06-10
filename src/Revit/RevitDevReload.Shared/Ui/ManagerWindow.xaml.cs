using System.Windows;
using System.Windows.Media;

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

            ApplyThemedTitleBar();
        }

        // Match the OS title bar to the merged Theme.xaml palette so the dark
        // window no longer wears a white caption. Colours come straight from
        // the theme (FindResource throws if a key is missing — we want that
        // surfaced, not silently defaulted).
        private void ApplyThemedTitleBar()
        {
            var caption = (Color)FindResource("BgColor");
            var text = (Color)FindResource("FgColor");
            var border = (Color)FindResource("InputBorderColor");
            DarkTitleBar.Apply(this, caption, text, border);
        }
    }
}
