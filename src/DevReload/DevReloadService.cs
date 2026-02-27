using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.EditorInput;
using EnvDTE;

namespace DevReload
{
    /// <summary>
    /// Finds a project by name across all running Visual Studio instances,
    /// builds it, and returns the path to the output DLL.
    /// <para>
    /// This is the core "dev-reload" service: type a command in AutoCAD,
    /// and it automatically builds your latest code in VS and returns the DLL path
    /// for <see cref="PluginHost{TPlugin}"/> to load.
    /// </para>
    /// </summary>
    public static class DevReloadService
    {
        /// <summary>
        /// GUID for Visual Studio solution folders (virtual containers, not real projects).
        /// </summary>
        private const string SolutionFolderKind = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";

        /// <summary>
        /// Finds a project named <paramref name="projectName"/> in any running
        /// Visual Studio instance, builds it in the active Debug configuration,
        /// and returns the full path to the output DLL.
        /// </summary>
        /// <param name="projectName">
        /// The project name as it appears in VS Solution Explorer
        /// (e.g., <c>"Example.Plugin"</c>).
        /// </param>
        /// <param name="ed">
        /// Optional AutoCAD editor for status messages. Pass <c>null</c> for silent operation.
        /// </param>
        /// <returns>
        /// Full path to the built DLL, or <c>null</c> if the project was not found,
        /// the build failed, or the user cancelled.
        /// </returns>
        public static string? FindAndBuild(string projectName, Editor? ed)
        {
            using var wc = new WaitCursorScope();

            // 1. Get running VS instances via COM ROT
            var vsInstances = VsInstanceFinder.GetRunningVSInstances();
            if (vsInstances.Count == 0)
            {
                ed?.WriteMessage("\nNo running Visual Studio instances found.");
                return null;
            }

            // 2. Search for the project across all VS instances
            var matches = new List<(string solutionName, _DTE dte, Project project)>();

            foreach (var kvp in vsInstances)
            {
                _DTE dte = kvp.Value;
                try
                {
                    if (string.IsNullOrEmpty(dte.Solution?.FullName))
                        continue;

                    foreach (Project prj in dte.Solution.Projects)
                    {
                        SearchProject(prj, projectName, dte, matches);
                    }
                }
                catch
                {
                    // Skip VS instances that don't have a loaded solution
                }
            }

            if (matches.Count == 0)
            {
                ed?.WriteMessage($"\nProject '{projectName}' not found in any running Visual Studio instance.");
                return null;
            }

            // 3. Select the right match (prompt user if multiple)
            _DTE targetDte;
            Project targetProject;

            if (matches.Count == 1)
            {
                targetDte = matches[0].dte;
                targetProject = matches[0].project;
                ed?.WriteMessage($"\nFound '{projectName}' in '{matches[0].solutionName}'.");
            }
            else
            {
                // Multiple matches — show selection dialog
                var solNames = matches.Select(m => m.solutionName).ToList();
                string selection = DevReload.Forms.StringGridFormCaller.Call(
                    solNames,
                    $"Project '{projectName}' found in {matches.Count} instances. Select:");

                if (string.IsNullOrEmpty(selection))
                {
                    ed?.WriteMessage("\nCancelled.");
                    return null;
                }

                var selected = matches.FirstOrDefault(m => m.solutionName == selection);

                if (selected.dte == null)
                {
                    ed?.WriteMessage("\nInvalid selection.");
                    return null;
                }

                targetDte = selected.dte;
                targetProject = selected.project;
            }

            // 4. Verify Debug configuration
            string activeConfig = targetProject.ConfigurationManager
                .ActiveConfiguration.ConfigurationName;

            if (activeConfig != "Debug")
            {
                ed?.WriteMessage(
                    $"\nActive configuration is '{activeConfig}', expected 'Debug'. Aborting.");
                return null;
            }

            // 5. Build the project (including dependencies)
            ed?.WriteMessage($"\nBuilding '{projectName}'...");

            SolutionBuild solBuild = targetDte.Solution.SolutionBuild;
            string solutionConfig = solBuild.ActiveConfiguration.Name;
            solBuild.BuildProject(solutionConfig, targetProject.UniqueName, true);

            if (solBuild.LastBuildInfo != 0)
            {
                ed?.WriteMessage(
                    $"\nBuild failed ({solBuild.LastBuildInfo} project(s) failed).");
                return null;
            }

            ed?.WriteMessage("\nBuild succeeded.");

            // 6. Resolve output DLL path from project properties
            string projectDir = Path.GetDirectoryName(targetProject.FullName)!;

            string outputPath = targetProject.ConfigurationManager
                .ActiveConfiguration.Properties.Item("OutputPath").Value.ToString()!;

            string assemblyName = targetProject.Properties
                .Item("AssemblyName").Value.ToString()!;

            string dllPath = Path.GetFullPath(
                Path.Combine(projectDir, outputPath, assemblyName + ".dll"));

            if (!File.Exists(dllPath))
            {
                ed?.WriteMessage($"\nBuild output not found at: {dllPath}");
                return null;
            }

            ed?.WriteMessage($"\nOutput: {dllPath}");
            return dllPath;
        }

        /// <summary>
        /// Recursively searches a project (or solution folder) for a project
        /// matching <paramref name="projectName"/>.
        /// </summary>
        private static void SearchProject(
            Project prj,
            string projectName,
            _DTE dte,
            List<(string solutionName, _DTE dte, Project project)> matches)
        {
            try
            {
                if (prj.Kind == SolutionFolderKind)
                {
                    // Recurse into solution folders
                    foreach (ProjectItem item in prj.ProjectItems)
                    {
                        if (item.SubProject != null)
                            SearchProject(item.SubProject, projectName, dte, matches);
                    }
                }
                else if (prj.Name == projectName)
                {
                    string solName = Path.GetFileNameWithoutExtension(
                        dte.Solution.FullName);
                    matches.Add((solName, dte, prj));
                }
            }
            catch
            {
                // Skip inaccessible projects
            }
        }

        /// <summary>
        /// Sets the cursor to <see cref="Cursors.WaitCursor"/> for the scope
        /// of the <c>using</c> block, restoring the previous cursor on dispose.
        /// </summary>
        private class WaitCursorScope : IDisposable
        {
            private readonly Cursor _savedCursor;

            public WaitCursorScope()
            {
                _savedCursor = Cursor.Current;
                Cursor.Current = Cursors.WaitCursor;
            }

            public void Dispose()
            {
                Cursor.Current = _savedCursor;
            }
        }
    }
}
