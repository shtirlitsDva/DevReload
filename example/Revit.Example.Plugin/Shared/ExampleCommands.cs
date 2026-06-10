using System;
using System.IO;

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Revit.Example.Plugin
{
    // E2E probe command: writes a marker file instead of showing UI so the
    // automated test can assert it ran. The file content proves which Revit
    // and which assembly produced it.
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class WriteMarkerCommand : IExternalCommand
    {
        public static string MarkerPath => Path.Combine(
            Path.GetTempPath(), "revit-devreload-example-marker.txt");

        public Result Execute(
            ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            string revitVersion = commandData.Application.Application.VersionNumber;
            File.WriteAllText(MarkerPath,
                $"ran at {DateTime.Now:O} in Revit {revitVersion} " +
                $"from {GetType().Assembly.GetName().Name}");
            return Result.Succeeded;
        }
    }

    // Second command so the manager UI/pipe shows a list, not a single item.
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class PingCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            message = "";
            return Result.Succeeded;
        }
    }
}
