using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

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
        // MSBuild synthesises SolutionDir as the PROJECT directory when a project is
        // evaluated or built standalone. For C++ projects that is not cosmetic: the
        // default OutDir is $(SolutionDir)$(Platform)\$(Configuration)\, so TargetPath
        // silently resolves to a directory the solution build never writes to. Callers
        // that know the solution must pass solutionDir; see docs/oarx-port/research.md F7.
        //
        // Trailing backslash is required by MSBuild convention, and a trailing backslash
        // immediately before the closing quote would escape it — hence the doubling.
        private static string SolutionDirArg(string? solutionDir)
        {
            if (string.IsNullOrEmpty(solutionDir)) return "";
            string dir = solutionDir!.TrimEnd('\\', '/') + "\\";
            return $" -p:SolutionDir=\"{dir}\\\"";
        }

        // Extra "Name=Value" MSBuild properties a registration carries (e.g. a
        // repo's fast-dev-loop switch). Applied to the build AND to property
        // queries — a property can steer where TargetPath lands, so resolving
        // with one set and building with another would be the wrong-but-plausible
        // split this code refuses elsewhere.
        private static string ExtraPropsArg(IReadOnlyList<string>? extraProperties)
        {
            if (extraProperties == null || extraProperties.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (var pair in extraProperties)
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue; // no name — nothing sane to pass
                string name = pair[..eq].Trim();
                string value = pair[(eq + 1)..].Trim();
                sb.Append($" -p:{name}=\"{value}\"");
            }
            return sb.ToString();
        }

        public static BuildResult BuildProject(
            string csprojPath,
            string buildConfiguration,
            string? platform,
            Action<string>? progress,
            string? solutionDir = null,
            IBuildProcessRunner? runner = null,
            IReadOnlyList<string>? extraProperties = null)
        {
            string projectDir = Path.GetDirectoryName(csprojPath)!;
            string projectName = Path.GetFileNameWithoutExtension(csprojPath);

            string? targetPath = QueryMsBuildProperty(
                csprojPath, "TargetPath", buildConfiguration, platform, solutionDir,
                extraProperties);

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
                    Arguments = $"build \"{csprojPath}\" -c {buildConfiguration}{platformArg}{ExtraPropsArg(extraProperties)}",
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
                // -restore drives NuGet's PackageReference path. A C++ project either
                // has no packages or uses packages.config, which -restore does not
                // handle; running it there is noise at best, so it is skipped.
                string restore = IsCppProject(csprojPath) ? "" : " -restore";
                psi = new ProcessStartInfo
                {
                    FileName = msbuild,
                    Arguments = $"\"{csprojPath}\"{restore} -p:Configuration={buildConfiguration}{msbPlatform}{SolutionDirArg(solutionDir)}{ExtraPropsArg(extraProperties)} -v:m -nologo",
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
                exitCode = (runner ?? DefaultBuildProcessRunner.Instance)
                    .Run(psi, line => buildLog.AppendLine(line));
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
            string? platform,
            string? solutionDir = null)
        {
            string csproj = GitWorktreeService.ResolveActiveCsproj(
                projectFilePath, activeWorktreePath);
            string? targetPath = QueryMsBuildProperty(
                csproj, "TargetPath", buildConfiguration, platform, solutionDir);
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
            string? platform,
            string? solutionDir = null)
        {
            string csproj = GitWorktreeService.ResolveActiveCsproj(
                projectFilePath, activeWorktreePath);

            // C++ projects do not define the `Configurations` property at all — they
            // declare a ProjectConfiguration item per Configuration|Platform pair.
            // Asking for the property returns empty, which used to surface as a bare
            // "could not resolve configurations".
            if (IsCppProject(csproj))
                return GetCppConfigurations(csproj, platform, solutionDir);

            // The Configuration value passed here is irrelevant to the result:
            // `Configurations` is a top-level property, not one gated on the
            // active configuration. "Debug" is always a valid value to evaluate.
            string? raw = QueryMsBuildProperty(
                csproj, "Configurations", "Debug", platform, solutionDir);
            if (string.IsNullOrWhiteSpace(raw))
                return Array.Empty<string>();

            return raw
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // .vcxproj / .vcxitems and friends. Kept as an explicit test rather than
        // "not SDK-style", because an old-style .csproj is also not SDK-style and
        // must keep the C# behaviour.
        public static bool IsCppProject(string projectPath) =>
            projectPath.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase);

        private static IReadOnlyList<string> GetCppConfigurations(
            string vcxproj, string? platform, string? solutionDir)
        {
            string? json = QueryMsBuild(
                vcxproj, "-getItem:ProjectConfiguration", "Debug", platform, solutionDir);
            if (string.IsNullOrWhiteSpace(json))
                return Array.Empty<string>();

            try
            {
                using var doc = JsonDocument.Parse(json!);
                if (!doc.RootElement.TryGetProperty("Items", out var items) ||
                    !items.TryGetProperty("ProjectConfiguration", out var configs))
                    return Array.Empty<string>();

                var result = new List<string>();
                foreach (var entry in configs.EnumerateArray())
                {
                    // Only configurations declared for the platform we build are
                    // selectable; offering Win32 for an x64-only host is a lie.
                    if (!string.IsNullOrEmpty(platform) &&
                        entry.TryGetProperty("Platform", out var p) &&
                        !string.Equals(p.GetString(), platform, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (entry.TryGetProperty("Configuration", out var c))
                    {
                        string? name = c.GetString();
                        if (!string.IsNullOrWhiteSpace(name) &&
                            !result.Contains(name!, StringComparer.OrdinalIgnoreCase))
                            result.Add(name!);
                    }
                }
                return result;
            }
            catch (JsonException)
            {
                return Array.Empty<string>();
            }
        }

        // Asks MSBuild for an evaluated property (e.g. TargetPath). Reading a
        // property does not invoke a full build, so this stays cheap.
        // -getProperty needs MSBuild 17.8+, satisfied by both the .NET 8 SDK
        // and VS2022 Build Tools.
        public static string? QueryMsBuildProperty(
            string csprojPath,
            string propertyName,
            string buildConfiguration,
            string? platform,
            string? solutionDir = null,
            IReadOnlyList<string>? extraProperties = null)
            => QueryMsBuild(csprojPath, $"-getProperty:{propertyName}",
                            buildConfiguration, platform, solutionDir, extraProperties);

        // Shared plumbing for -getProperty / -getItem. Both return on stdout and both
        // need the same toolchain selection and SolutionDir handling.
        private static string? QueryMsBuild(
            string csprojPath,
            string getArg,
            string buildConfiguration,
            string? platform,
            string? solutionDir,
            IReadOnlyList<string>? extraProperties = null)
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
                    arguments = $"msbuild \"{csprojPath}\" {getArg} -p:Configuration={buildConfiguration}{platformArg}{ExtraPropsArg(extraProperties)}";
                }
                else
                {
                    string? msbuild = LocateFrameworkMsBuild();
                    if (msbuild == null) return null;
                    fileName = msbuild;
                    arguments = $"\"{csprojPath}\" {getArg} -p:Configuration={buildConfiguration}{platformArg}{SolutionDirArg(solutionDir)}{ExtraPropsArg(extraProperties)}";
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
