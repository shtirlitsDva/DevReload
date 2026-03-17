using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Autodesk.AutoCAD.EditorInput;
using EnvDTE;
using EnvDTE80;

namespace DevReload
{
    public record VsProjectInfo(string Name, string DebugDllPath, string SolutionName, string ProjectFilePath);

    public static class DevReloadService
    {
        private const string SolutionFolderKind = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";

        public static List<VsProjectInfo> GetAvailableProjects(Editor? ed)
        {
            var result = new List<VsProjectInfo>();
            var vsInstances = VsInstanceFinder.GetRunningVSInstances();
            if (vsInstances.Count == 0)
            {
                ed?.WriteMessage("\nNo running Visual Studio instances found.");
                return result;
            }

            foreach (var kvp in vsInstances)
            {
                _DTE dte = kvp.Value;
                try
                {
                    if (string.IsNullOrEmpty(dte.Solution?.FullName))
                        continue;

                    string solName = Path.GetFileNameWithoutExtension(dte.Solution.FullName);
                    CollectProjects(dte.Solution, solName, result);
                }
                catch { }
            }

            return result;
        }

        private static void CollectProjects(Solution solution, string solName, List<VsProjectInfo> result)
        {
            foreach (Project prj in solution.Projects)
                CollectProject(prj, solName, result);
        }

        private static void CollectProject(Project prj, string solName, List<VsProjectInfo> result)
        {
            try
            {
                if (prj.Kind == SolutionFolderKind)
                {
                    foreach (ProjectItem item in prj.ProjectItems)
                    {
                        if (item.SubProject != null)
                            CollectProject(item.SubProject, solName, result);
                    }
                    return;
                }

                string projectDir = Path.GetDirectoryName(prj.FullName)!;
                string assemblyName = prj.Properties.Item("AssemblyName").Value.ToString()!;

                string? debugOutputPath = null;
                var configMgr = prj.ConfigurationManager;
                if (configMgr != null)
                {
                    for (int i = 1; i <= configMgr.Count; i++)
                    {
                        try
                        {
                            var cfg = configMgr.Item(i);
                            if (cfg.ConfigurationName.Equals("Debug", StringComparison.OrdinalIgnoreCase))
                            {
                                debugOutputPath = cfg.Properties.Item("OutputPath").Value.ToString();
                                break;
                            }
                        }
                        catch { }
                    }

                    if (debugOutputPath == null)
                    {
                        try
                        {
                            debugOutputPath = configMgr.ActiveConfiguration
                                .Properties.Item("OutputPath").Value.ToString();
                        }
                        catch { }
                    }
                }

                if (debugOutputPath == null) return;

                string dllPath = Path.GetFullPath(
                    Path.Combine(projectDir, debugOutputPath, assemblyName + ".dll"));

                result.Add(new VsProjectInfo(prj.Name, dllPath, solName, prj.FullName));
            }
            catch { }
        }

        public static string? BuildProject(string csprojPath, string buildConfiguration, Editor? ed)
        {
            using var wc = new WaitCursorScope();

            string projectDir = Path.GetDirectoryName(csprojPath)!;
            string projectName = Path.GetFileNameWithoutExtension(csprojPath);

            // 1. Query target DLL path via dotnet msbuild
            string? targetPath = QueryMsBuildProperty(
                csprojPath, "TargetPath", buildConfiguration);

            if (string.IsNullOrEmpty(targetPath))
            {
                ed?.WriteMessage($"\nFailed to resolve output path for '{projectName}'.");
                return null;
            }

            // 2. Build via dotnet CLI
            ed?.WriteMessage($"\nBuilding '{projectName}' ({buildConfiguration})...");

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build \"{csprojPath}\" -c {buildConfiguration} -p:Platform=x64",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = projectDir,
            };

            var buildLog = new StringBuilder();
            int exitCode;
            try
            {
                using var proc = new System.Diagnostics.Process { StartInfo = psi };
                proc.OutputDataReceived += (_, e) => { if (e.Data != null) buildLog.AppendLine(e.Data); };
                proc.ErrorDataReceived += (_, e) => { if (e.Data != null) buildLog.AppendLine(e.Data); };
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                proc.WaitForExit();
                exitCode = proc.ExitCode;
            }
            catch (Exception ex)
            {
                ed?.WriteMessage($"\nFailed to start dotnet build: {ex.Message}");
                return null;
            }

            string log = buildLog.ToString();
            var summary = ParseBuildSummary(log);

            if (exitCode != 0)
            {
                ed?.WriteMessage($"\nBuild FAILED — {summary.Errors} error(s), {summary.Warnings} warning(s).");
                var errorLines = log
                    .Split('\n')
                    .Where(l => l.Contains(": error "))
                    .Take(10)
                    .ToList();
                foreach (var line in errorLines)
                    ed?.WriteMessage($"\n  {line.Trim()}");
                return null;
            }

            ed?.WriteMessage(summary.Warnings > 0
                ? $"\nBuild succeeded — {summary.Warnings} warning(s)."
                : "\nBuild succeeded.");

            // 3. Verify output
            if (!File.Exists(targetPath))
            {
                ed?.WriteMessage($"\nBuild output not found at: {targetPath}");
                return null;
            }

            ed?.WriteMessage($"\nOutput: {targetPath}");
            return targetPath;
        }

        private static string? QueryMsBuildProperty(
            string csprojPath, string propertyName, string buildConfiguration)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"msbuild \"{csprojPath}\" -getProperty:{propertyName} -p:Configuration={buildConfiguration} -p:Platform=x64",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(csprojPath)!,
                };

                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return null;

                string output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit();

                return proc.ExitCode == 0 && !string.IsNullOrEmpty(output) ? output : null;
            }
            catch
            {
                return null;
            }
        }

        public static string? FindAndBuild(string projectName, Editor? ed, string buildConfiguration = "Debug")
        {
            using var wc = new WaitCursorScope();

            // 1. Get running VS instances
            var vsInstances = VsInstanceFinder.GetRunningVSInstances();
            if (vsInstances.Count == 0)
            {
                ed?.WriteMessage("\nNo running Visual Studio instances found.");
                return null;
            }

            // 2. Find project across all VS instances
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
                    // Skip VS instances without loaded solutions
                }
            }

            if (matches.Count == 0)
            {
                ed?.WriteMessage($"\nProject '{projectName}' not found in any running Visual Studio instance.");
                return null;
            }

            // 3. Select the right match
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
                // Multiple matches - ask user via StringGridForm
                var solNames = matches.Select(m => m.solutionName).ToList();
                string selection = IntersectUtilities.StringGridFormCaller.Call(
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

            // 4. Get output DLL path for the requested build configuration
            string projectDir;
            string outputPath;
            string assemblyName;
            try
            {
                projectDir = Path.GetDirectoryName(targetProject.FullName)!;

                assemblyName = targetProject.Properties
                    .Item("AssemblyName").Value.ToString()!;

                // Find the output path for the requested configuration
                outputPath = null!;
                var configMgr = targetProject.ConfigurationManager;
                if (configMgr != null)
                {
                    for (int i = 1; i <= configMgr.Count; i++)
                    {
                        try
                        {
                            var cfg = configMgr.Item(i);
                            if (cfg.ConfigurationName.Equals(buildConfiguration,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                outputPath = cfg.Properties.Item("OutputPath").Value.ToString()!;
                                break;
                            }
                        }
                        catch { }
                    }

                    // Fallback: convention-based path
                    if (string.IsNullOrEmpty(outputPath))
                        outputPath = Path.Combine("bin", buildConfiguration);
                }
                else
                {
                    outputPath = Path.Combine("bin", buildConfiguration);
                }
            }
            catch (Exception ex)
            {
                ed?.WriteMessage(
                    $"\nFailed to read project output properties: {ex.GetType().Name}: {ex.Message}");
                return null;
            }

            string dllPath = Path.GetFullPath(
                Path.Combine(projectDir, outputPath, assemblyName + ".dll"));

            // 5. Build via dotnet CLI (VS COM BuildProject is broken in VS 2026)
            ed?.WriteMessage($"\nBuilding '{projectName}' ({buildConfiguration})...");

            string projectFilePath = targetProject.FullName;
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build \"{projectFilePath}\" -c {buildConfiguration} -p:Platform=x64",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = projectDir,
            };

            var buildLog = new StringBuilder();
            int exitCode;
            try
            {
                using var proc = new System.Diagnostics.Process { StartInfo = psi };
                proc.OutputDataReceived += (_, e) => { if (e.Data != null) buildLog.AppendLine(e.Data); };
                proc.ErrorDataReceived += (_, e) => { if (e.Data != null) buildLog.AppendLine(e.Data); };
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                proc.WaitForExit();
                exitCode = proc.ExitCode;
            }
            catch (Exception ex)
            {
                ed?.WriteMessage($"\nFailed to start dotnet build: {ex.Message}");
                return null;
            }

            string log = buildLog.ToString();
            var summary = ParseBuildSummary(log);

            if (exitCode != 0)
            {
                ed?.WriteMessage($"\nBuild FAILED — {summary.Errors} error(s), {summary.Warnings} warning(s).");
                var errorLines = log
                    .Split('\n')
                    .Where(l => l.Contains(": error "))
                    .Take(10)
                    .ToList();
                foreach (var line in errorLines)
                    ed?.WriteMessage($"\n  {line.Trim()}");
                return null;
            }

            ed?.WriteMessage(summary.Warnings > 0
                ? $"\nBuild succeeded — {summary.Warnings} warning(s)."
                : "\nBuild succeeded.");

            // 6. Verify the build produced the DLL
            if (!File.Exists(dllPath))
            {
                ed?.WriteMessage($"\nBuild output not found at: {dllPath}");
                return null;
            }

            ed?.WriteMessage($"\nOutput: {dllPath}");
            return dllPath;
        }

        private static (int Warnings, int Errors) ParseBuildSummary(string log)
        {
            int warnings = 0, errors = 0;
            foreach (var line in log.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.EndsWith("Warning(s)"))
                {
                    int.TryParse(trimmed.Split(' ')[0], out warnings);
                }
                else if (trimmed.EndsWith("Error(s)"))
                {
                    int.TryParse(trimmed.Split(' ')[0], out errors);
                }
            }
            return (warnings, errors);
        }

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
