using System.Windows.Controls;

using DevReloadTest.ViewModels;

using EventManager;

namespace DevReloadTest.Views
{
    public partial class EventTestView : UserControl
    {
        public EventTestView(AcadEventManager events)
        {
            InitializeComponent();
            var vm = new EventTestViewModel(events);
            vm.Activate();
            DataContext = vm;
        }
    }
}
