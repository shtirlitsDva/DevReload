#if BRICSCAD
using Bricscad.EditorInput;
#else
using Autodesk.AutoCAD.EditorInput;
#endif

using System.Collections.Generic;

using DevReload.Core;
using DevReload.Hud;

namespace DevReload
{
    // AutoCAD-side glue over the shared BuildService: progress lines to the
    // command-line editor, build output streamed into the reload HUD. All
    // AutoCAD projects are SDK-style x64, hence the fixed platform.
    internal static class AcadBuild
    {
        internal const string Platform = "x64";

        /// <summary>
        /// Build a plugin project, reporting into <paramref name="ui"/>.
        /// </summary>
        /// <remarks>
        /// Always runs on AutoCAD's main thread (the palette buttons, the generated
        /// {PREFIX} commands and the RunOnAcadMainThread MCP tools are the only
        /// callers), so the build is driven through <see cref="PumpedBuildRunner"/>:
        /// a plain WaitForExit pins the message loop for the whole compile and
        /// Windows paints "Not Responding" over the window.
        ///
        /// <para>No wait cursor. It used to set one, which was honest while the
        /// window was frozen solid; now the HUD is the progress indicator and a
        /// wait cursor over a live, animating window reads as a hang.</para>
        /// </remarks>
        /// <param name="msBuildProperties">The plugin's extra "Name=Value"
        /// MSBuild properties (e.g. a host switch that picks the references and
        /// the output folder). Every TargetPath query for the same plugin passes
        /// the same list, or the build lands where the load never looks.</param>
        internal static BuildResult Build(
            string csprojPath, string buildConfiguration,
            IReadOnlyList<string> msBuildProperties, Editor? ed, IReloadProgress ui) =>
            BuildService.BuildProject(
                csprojPath, buildConfiguration, Platform,
                msg => ed?.WriteMessage("\n" + msg),
                runner: new PumpedBuildRunner(ui),
                extraProperties: msBuildProperties);
    }
}
