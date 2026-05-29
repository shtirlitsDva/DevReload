using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Autodesk.AutoCAD.EditorInput;
using DevReload.Rpc;

namespace DevReload
{
    public static class DevReloadService
    {
        public static BuildResult BuildProject(string csprojPath, string buildConfiguration, Editor? ed)
        {
            using var wc = new WaitCursorScope();

            string projectDir = Path.GetDirectoryName(csprojPath)!;
            string projectName = Path.GetFileNameWithoutExtension(csprojPath);

            string? targetPath = QueryMsBuildProperty(
                csprojPath, "TargetPath", buildConfiguration);

            if (string.IsNullOrEmpty(targetPath))
            {
                string msg = $"Failed to resolve output path for '{projectName}'.";
                ed?.WriteMessage("\n" + msg);
                return new BuildResult(false, null, 0, 1, msg);
            }

            ed?.WriteMessage($"\nBuilding '{projectName}' ({buildConfiguration})...");

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build \"{csprojPath}\" -c {buildConfiguration} -p:Platform=x64",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
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
                string msg = $"Failed to start dotnet build: {ex.Message}";
                ed?.WriteMessage("\n" + msg);
                return new BuildResult(false, null, 0, 1, msg);
            }

            string log = buildLog.ToString();
            var summary = ParseBuildSummary(log);

            if (exitCode != 0)
            {
                ed?.WriteMessage($"\nBuild FAILED — {summary.Errors} error(s), {summary.Warnings} warning(s).");
                foreach (var line in log.Split('\n').Where(l => l.Contains(": error ")).Take(10))
                    ed?.WriteMessage($"\n  {line.Trim()}");
                return new BuildResult(false, null, summary.Warnings, summary.Errors, log);
            }

            ed?.WriteMessage(summary.Warnings > 0
                ? $"\nBuild succeeded — {summary.Warnings} warning(s)."
                : "\nBuild succeeded.");

            if (!File.Exists(targetPath))
            {
                string msg = $"Build output not found at: {targetPath}";
                ed?.WriteMessage("\n" + msg);
                return new BuildResult(false, null, summary.Warnings, summary.Errors + 1, log);
            }

            ed?.WriteMessage($"\nOutput: {targetPath}");
            return new BuildResult(true, targetPath, summary.Warnings, summary.Errors, log);
        }

        // Build output directory for a plugin selection (worktree + configuration),
        // or null when MSBuild can't resolve it yet (e.g. the worktree has never
        // been built/restored). NO fallback: null means "not resolvable / not
        // built" and the caller must handle it (e.g. tell the user to build first).
        internal static string? ResolveBuildDir(
            string projectFilePath, string? activeWorktreePath, string buildConfiguration)
        {
            string csproj = GitWorktreeService.ResolveActiveCsproj(
                projectFilePath, activeWorktreePath);
            string? targetPath = QueryMsBuildProperty(
                csproj, "TargetPath", buildConfiguration);
            return string.IsNullOrEmpty(targetPath)
                ? null
                : Path.GetDirectoryName(targetPath);
        }

        // Asks MSBuild for an evaluated property (e.g. TargetPath) without a stale
        // entry.DllPath. Reading a property does not invoke a full build, so this
        // stays cheap. Wrapped by ResolveBuildDir and used by BuildProject.
        internal static string? QueryMsBuildProperty(
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
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
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

        private class WaitCursorScope : IDisposable
        {
            private readonly Cursor? _savedCursor;

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
