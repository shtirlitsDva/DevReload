using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitDevReload
{
    // The host's single real external command (ribbon button). Besides
    // opening the window it captures ExternalCommandData — the handle plugin
    // command invocations are executed with (its ctor is Revit-internal, so
    // capturing here is the only way to obtain one; RevitAddInManager has
    // shipped this pattern for years).
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public sealed class OpenManagerCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            RevitContext.CapturedCommandData = commandData;
            RevitDevReloadApp.ShowManagerWindow();
            return Result.Succeeded;
        }
    }
}
