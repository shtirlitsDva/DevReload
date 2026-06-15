using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace DevReload.Core
{
    // Host-agnostic build engine shared by the AutoCAD plugin and the Revit
    // add-ins. Two project flavours exist in the wild:
    //
    //   - SDK-style csproj (<Project Sdk="...">)  -> `dotnet build` / `dotnet msbuild`
    //   - old-style csproj (ToolsVersion, xmlns)  -> full-framework MSBuild.exe
    //     located via vswhere (the user's pre-2025 Revit plugins are old-style;
    //     `dotnet build` cannot load them)
    //
    // Progress text goes through an optional callback so each host renders it
    // its own way (AutoCAD editor, Revit log pane) without this code knowing
    // about either.
    public static class BuildService
    {
        public static BuildResult BuildProject(
            string csprojPath,
            string buildConfiguration,
            string? platform,
            Action<string>? progress)
        {
            string projectDir = Path.GetDirectoryName(csprojPath)!;
            string projectName = Path.GetFileNameWithoutExtension(csprojPath);

            string? targetPath = QueryMsBuildProperty(
                csprojPath, "TargetPath", buildConfiguration, platform);

            if (string.IsNullOrEmpty(targetPath))
            {
                string msg = $"Failed to resolve output path for '{projectName}'.";
                progress?.Invoke(msg);
                return new BuildResult(false, null, 0, 1, msg);
            }

            progress?.Invoke($"Building '{projectName}' ({buildConfiguration})...");

            string platformArg = string.IsNullOrEmpty(platform)
                ? ""
                : $" -p:Platform={platform}";

            ProcessStartInfo psi;
            if (IsSdkStyle(csprojPath))
            {
                psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"build \"{csprojPath}\" -c {buildConfiguration}{platformArg}",
                };
            }
            else
            {
                string? msbuild = LocateFrameworkMsBuild();
                if (msbuild == null)
                {
                    string msg = $"'{projectName}' is an old-style csproj and no " +
                        "MSBuild.exe was found via vswhere. Install VS Build Tools.";
                    progress?.Invoke(msg);
                    return new BuildResult(false, null, 0, 1, msg);
                }
                string msbPlatform = string.IsNullOrEmpty(platform)
                    ? ""
                    : $" -p:Platform={platform}";
                psi = new ProcessStartInfo
                {
                    FileName = msbuild,
                    Arguments = $"\"{csprojPath}\" -restore -p:Configuration={buildConfiguration}{msbPlatform} -v:m -nologo",
                };
            }

            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = projectDir;

            var buildLog = new StringBuilder();
            int exitCode;
            try
            {
                using var proc = new Process { StartInfo = psi };
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
                string msg = $"Failed to start build: {ex.Message}";
                progress?.Invoke(msg);
                return new BuildResult(false, null, 0, 1, msg);
            }

            string log = buildLog.ToString();
            var summary = ParseBuildSummary(log);

            if (exitCode != 0)
            {
                progress?.Invoke($"Build FAILED — {summary.Errors} error(s), {summary.Warnings} warning(s).");
                foreach (var line in log.Split('\n').Where(l => l.Contains(": error ")).Take(10))
                    progress?.Invoke($"  {line.Trim()}");
                return new BuildResult(false, null, summary.Warnings, summary.Errors, log);
            }

            progress?.Invoke(summary.Warnings > 0
                ? $"Build succeeded — {summary.Warnings} warning(s)."
                : "Build succeeded.");

            if (!File.Exists(targetPath))
            {
                string msg = $"Build output not found at: {targetPath}";
                progress?.Invoke(msg);
                return new BuildResult(false, null, summary.Warnings, summary.Errors + 1, log);
            }

            progress?.Invoke($"Output: {targetPath}");
            return new BuildResult(true, targetPath, summary.Warnings, summary.Errors, log);
        }

        // Build output directory for a plugin selection (worktree + configuration),
        // or null when MSBuild can't resolve it yet (e.g. the worktree has never
        // been built/restored). NO fallback: null means "not resolvable / not
        // built" and the caller must handle it (e.g. tell the user to build first).
        public static string? ResolveBuildDir(
            string projectFilePath,
            string? activeWorktreePath,
            string buildConfiguration,
            string? platform)
        {
            string csproj = GitWorktreeService.ResolveActiveCsproj(
                projectFilePath, activeWorktreePath);
            string? targetPath = QueryMsBuildProperty(
                csproj, "TargetPath", buildConfiguration, platform);
            return string.IsNullOrEmpty(targetPath)
                ? null
                : Path.GetDirectoryName(targetPath);
        }

        // The configurations declared by a project (the `Configurations` MSBuild
        // property — e.g. "Debug;Release;IALCD;IALCR"). The .NET SDK seeds a
        // default of "Debug;Release" when a project doesn't set it explicitly, so
        // SDK-style projects always return at least those two. Worktree-aware via
        // the same active-csproj resolution as the build. Returns an empty list
        // when MSBuild can't be queried (e.g. the worktree was never restored) —
        // NO fallback list; the caller decides how to present that.
        public static IReadOnlyList<string> GetConfigurations(
            string projectFilePath,
            string? activeWorktreePath,
            string? platform)
        {
            string csproj = GitWorktreeService.ResolveActiveCsproj(
                projectFilePath, activeWorktreePath);

            // The Configuration value passed here is irrelevant to the result:
            // `Configurations` is a top-level property, not one gated on the
            // active configuration. "Debug" is always a valid value to evaluate.
            string? raw = QueryMsBuildProperty(
                csproj, "Configurations", "Debug", platform);
            if (string.IsNullOrWhiteSpace(raw))
                return Array.Empty<string>();

            return raw
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // Asks MSBuild for an evaluated property (e.g. TargetPath). Reading a
        // property does not invoke a full build, so this stays cheap.
        // -getProperty needs MSBuild 17.8+, satisfied by both the .NET 8 SDK
        // and VS2022 Build Tools.
        public static string? QueryMsBuildProperty(
            string csprojPath,
            string propertyName,
            string buildConfiguration,
            string? platform)
        {
            try
            {
                string platformArg = string.IsNullOrEmpty(platform)
                    ? ""
                    : $" -p:Platform={platform}";

                string fileName;
                string arguments;
                if (IsSdkStyle(csprojPath))
                {
                    fileName = "dotnet";
                    arguments = $"msbuild \"{csprojPath}\" -getProperty:{propertyName} -p:Configuration={buildConfiguration}{platformArg}";
                }
                else
                {
                    string? msbuild = LocateFrameworkMsBuild();
                    if (msbuild == null) return null;
                    fileName = msbuild;
                    arguments = $"\"{csprojPath}\" -getProperty:{propertyName} -p:Configuration={buildConfiguration}{platformArg}";
                }

                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(csprojPath)!,
                };

                using var proc = Process.Start(psi);
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

        // SDK-style detection: the Sdk attribute appears on the root <Project>
        // element within the first few hundred bytes. Old-style projects carry
        // the 2003 msbuild xmlns instead.
        public static bool IsSdkStyle(string csprojPath)
        {
            try
            {
                using var reader = new StreamReader(csprojPath);
                char[] buffer = new char[1024];
                int read = reader.Read(buffer, 0, buffer.Length);
                string head = new string(buffer, 0, read);
                return head.Contains("<Project Sdk=") || head.Contains("<Project  Sdk=");
            }
            catch
            {
                return false;
            }
        }

        private static string? _frameworkMsBuild;
        private static bool _frameworkMsBuildResolved;

        private static string? LocateFrameworkMsBuild()
        {
            if (_frameworkMsBuildResolved) return _frameworkMsBuild;
            _frameworkMsBuildResolved = true;

            try
            {
                string vswhere = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "Microsoft Visual Studio", "Installer", "vswhere.exe");
                if (!File.Exists(vswhere)) return null;

                var psi = new ProcessStartInfo
                {
                    FileName = vswhere,
                    Arguments = "-latest -products * -requires Microsoft.Component.MSBuild " +
                                "-find MSBuild\\**\\Bin\\MSBuild.exe",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                if (proc == null) return null;
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                _frameworkMsBuild = output
                    .Split('\n')
                    .Select(l => l.Trim())
                    .FirstOrDefault(l => l.Length > 0 && File.Exists(l));
            }
            catch
            {
                _frameworkMsBuild = null;
            }
            return _frameworkMsBuild;
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
    }
}
