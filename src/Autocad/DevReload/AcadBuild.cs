using System;
using System.Windows.Forms;

using Autodesk.AutoCAD.EditorInput;

using DevReload.Core;

namespace DevReload
{
    // AutoCAD-side glue over the shared BuildService: wait cursor while the
    // build runs, progress lines to the command-line editor. All AutoCAD
    // projects are SDK-style x64, hence the fixed platform.
    internal static class AcadBuild
    {
        internal const string Platform = "x64";

        internal static BuildResult Build(
            string csprojPath, string buildConfiguration, Editor? ed)
        {
            var saved = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;
            try
            {
                return BuildService.BuildProject(
                    csprojPath, buildConfiguration, Platform,
                    msg => ed?.WriteMessage("\n" + msg));
            }
            finally
            {
                Cursor.Current = saved;
            }
        }
    }
}
