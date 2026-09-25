using System.Windows;

using WpfSHARED;

namespace DevReload.Views
{
    public partial class SharedAssembliesWindow : Window
    {
        public SharedAssembliesWindow()
        {
            InitializeComponent();
            DarkTitleBar.ApplyTheme(this);
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
