using System;

using Autodesk.AutoCAD.Windows;

using DevReloadTest.Views;

using EventManager;

namespace DevReloadTest
{
    public class TestPaletteSet : PaletteSet
    {
        private static readonly Guid PaletteGuid =
            new Guid("e4a479a3-6271-46e9-89be-dd808af1277f");

        public TestPaletteSet(AcadEventManager events)
            : base("DevReloadTest", "DEVRELOADTEST", PaletteGuid)
        {
            Style = PaletteSetStyles.ShowCloseButton
                  | PaletteSetStyles.ShowPropertiesMenu;

            AddVisual("Test", new TestPaletteView());
            AddVisual("Events", new EventTestView(events));
        }
    }
}
