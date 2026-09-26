using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

using DevReload.Diagnostics;

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
                foreach (var line in log.Split('\n').Where(IsErrorLine).Take(10))
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

        /// <summary>
        /// Build several MSBuild.exe projects (the modules of an OARX group) in ONE
        /// msbuild run with <c>-m</c>, so projects that do not depend on each other
        /// compile side by side instead of one after the other.
        /// </summary>
        /// <remarks>
        /// One <see cref="BuildProject"/> per module is strictly serial: the second
        /// module cannot start compiling until the first has linked, although
        /// neither needs the other (a .dbx and the .arx that binds it by name at
        /// run time). Measured on NorsynDrawingTools' NdhPipeline group, a Core
        /// header edit went from ~50 s to ~40 s with the two modules in one run.
        /// The run goes through a generated traversal project over the modules;
        /// every global property (Configuration, Platform, SolutionDir, the
        /// registration's extras) reaches each module exactly as a single-project
        /// build would pass it, and MSBuild builds a shared reference (a Core
        /// static lib) once. A group of one is just <see cref="BuildProject"/>.
        /// </remarks>
        public static GroupBuildResult BuildProjects(
            IReadOnlyList<string> projectPaths,
            string buildConfiguration,
            string? platform,
            Action<string>? progress,
            string? solutionDir = null,
            IBuildProcessRunner? runner = null,
            IReadOnlyList<string>? extraProperties = null)
        {
            if (projectPaths.Count == 0)
                throw new ArgumentException("A group build needs at least one project.", nameof(projectPaths));

            if (projectPaths.Count == 1)
            {
                var single = BuildProject(projectPaths[0], buildConfiguration, platform, progress,
                    solutionDir, runner, extraProperties);
                return new GroupBuildResult(single.Success, new[] { single.OutputPath },
                    single.Success ? Array.Empty<string>() : new[] { Path.GetFileName(projectPaths[0]) },
                    single.Warnings, single.Errors, single.Log);
            }

            GroupBuildResult Refused(string msg)
            {
                progress?.Invoke(msg);
                return new GroupBuildResult(false, new string?[projectPaths.Count],
                    Array.Empty<string>(), 0, 1, msg);
            }

            // dotnet build takes one project; the traversal is an MSBuild.exe run.
            var sdkStyle = projectPaths.Where(IsSdkStyle).Select(Path.GetFileName).ToList();
            if (sdkStyle.Count > 0)
                return Refused("A group build takes MSBuild.exe projects only; SDK-style: " +
                               string.Join(", ", sdkStyle) + ".");

            var targets = new List<string>(projectPaths.Count);
            foreach (string proj in projectPaths)
            {
                string? target = QueryMsBuildProperty(
                    proj, "TargetPath", buildConfiguration, platform, solutionDir, extraProperties);
                if (string.IsNullOrEmpty(target))
                    return Refused($"Failed to resolve output path for '{Path.GetFileNameWithoutExtension(proj)}'.");
                targets.Add(target!);
            }

            string? msbuild = LocateFrameworkMsBuild();
            if (msbuild == null)
                return Refused("No MSBuild.exe was found via vswhere. Install VS Build Tools.");

            string traversal = WriteTraversalProject(projectPaths);
            string names = string.Join(", ", projectPaths.Select(Path.GetFileNameWithoutExtension));
            progress?.Invoke($"Building {names} ({buildConfiguration}) in one parallel run...");

            string platformArg = string.IsNullOrEmpty(platform) ? "" : $" -p:Platform={platform}";
            // -nr:false: this runs inside AutoCAD; worker nodes left behind by a
            // reload would outlive it as orphan MSBuild.exe processes.
            var psi = new ProcessStartInfo
            {
                FileName = msbuild,
                Arguments = $"\"{traversal}\" -m -nr:false -p:Configuration={buildConfiguration}{platformArg}" +
                            $"{SolutionDirArg(solutionDir)}{ExtraPropsArg(extraProperties)} -v:m -nologo",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = !string.IsNullOrEmpty(solutionDir)
                    ? solutionDir!
                    : Path.GetDirectoryName(projectPaths[0])!,
            };

            var buildLog = new StringBuilder();
            int exitCode;
            try
            {
                exitCode = (runner ?? DefaultBuildProcessRunner.Instance)
                    .Run(psi, line => buildLog.AppendLine(line));
            }
            catch (Exception ex)
            {
                return Refused($"Failed to start build: {ex.Message}");
            }

            string log = buildLog.ToString();
            var summary = ParseBuildSummary(log);
            var outputs = targets.Select(t => File.Exists(t) ? t : null).ToList();

            if (exitCode != 0)
            {
                var failed = ProjectsWithErrors(log);
                progress?.Invoke($"Build FAILED — {summary.Errors} error(s), {summary.Warnings} warning(s)" +
                                 (failed.Count > 0 ? $" in {string.Join(", ", failed)}." : "."));
                foreach (var line in log.Split('\n').Where(IsErrorLine).Take(10))
                    progress?.Invoke($"  {line.Trim()}");
                return new GroupBuildResult(false, outputs, failed, summary.Warnings, summary.Errors, log);
            }

            var absent = projectPaths.Where((_, i) => outputs[i] == null).Select(Path.GetFileName).ToList();
            if (absent.Count > 0)
            {
                string msg = "Build output not found for: " + string.Join(", ", absent);
                progress?.Invoke(msg);
                return new GroupBuildResult(false, outputs, absent!, summary.Warnings, summary.Errors + 1, log);
            }

            progress?.Invoke(summary.Warnings > 0
                ? $"Build succeeded — {summary.Warnings} warning(s)."
                : "Build succeeded.");
            return new GroupBuildResult(true, outputs, Array.Empty<string>(), summary.Warnings, summary.Errors, log);
        }

        // The traversal a group build runs: the modules, built in parallel with the
        // caller's global properties. A bare <Project> imports nothing, so no
        // Directory.Build.* next to the temp file can leak into it; each module
        // still imports its own.
        internal static string TraversalProjectXml(IEnumerable<string> projectPaths)
        {
            var sb = new StringBuilder();
            sb.Append("<Project>\r\n");
            sb.Append("  <!-- Generated by DevReload (BuildService.BuildProjects). Do not edit. -->\r\n");
            sb.Append("  <ItemGroup>\r\n");
            foreach (string p in projectPaths)
                sb.Append("    <DevReloadModule Include=\"")
                  .Append(System.Security.SecurityElement.Escape(Path.GetFullPath(p)))
                  .Append("\" />\r\n");
            sb.Append("  </ItemGroup>\r\n");
            sb.Append("  <Target Name=\"Build\">\r\n");
            sb.Append("    <MSBuild Projects=\"@(DevReloadModule)\" BuildInParallel=\"true\" />\r\n");
            sb.Append("  </Target>\r\n");
            sb.Append("</Project>\r\n");
            return sb.ToString();
        }

        // Content-addressed, so one group always runs the same file and two groups
        // (or two AutoCAD sessions building different groups) never share one.
        private static string WriteTraversalProject(IReadOnlyList<string> projectPaths)
        {
            string xml = TraversalProjectXml(projectPaths);
            string dir = Path.Combine(Path.GetTempPath(), "DevReload");
            Directory.CreateDirectory(dir);

            string hash;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                hash = string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(xml))
                                        .Take(8).Select(b => b.ToString("x2")));
            string path = Path.Combine(dir, $"group-{hash}.proj");
            if (!File.Exists(path) || File.ReadAllText(path) != xml)
                File.WriteAllText(path, xml);
            return path;
        }

        // The projects MSBuild attributed an error to. MSBuild ends every error
        // line with " [<full project path>]", naming the project that failed -
        // which is often a referenced static lib, not a module.
        internal static IReadOnlyList<string> ProjectsWithErrors(string log)
        {
            var found = new List<string>();
            foreach (string raw in log.Split('\n'))
            {
                string line = raw.TrimEnd();
                if (!IsErrorLine(line) || !line.EndsWith("]"))
                    continue;
                int open = line.LastIndexOf('[');
                if (open < 0) continue;
                string name = Path.GetFileName(line.Substring(open + 1, line.Length - open - 2));
                if (name.Length > 0 && !found.Contains(name, StringComparer.OrdinalIgnoreCase))
                    found.Add(name);
            }
            return found;
        }

        // An MSBuild error line. "fatal error" counts: LNK1104 on a module a running
        // AutoCAD holds open is the commonest native build failure there is.
        internal static bool IsErrorLine(string line) =>
            line.IndexOf(": error ", StringComparison.OrdinalIgnoreCase) >= 0 ||
            line.IndexOf(": fatal error ", StringComparison.OrdinalIgnoreCase) >= 0;

        // Build output directory for a plugin selection (worktree + configuration),
        // or null when MSBuild can't resolve it yet (e.g. the worktree has never
        // been built/restored). NO fallback: null means "not resolvable / not
        // built" and the caller must handle it (e.g. tell the user to build first).
        /// <summary>
        /// The assembly the given project+configuration produces, resolved in the
        /// ACTIVE worktree. MSBuild's TargetPath is the only authority on this —
        /// it is what the build itself will write.
        /// </summary>
        /// <remarks>
        /// This exists so no caller has to REMEMBER an output path. A remembered
        /// one is derived state with three inputs (project, configuration,
        /// worktree) and no invalidation, which is how a plugin ended up loading
        /// the main checkout's DLL while its worktree was selected.
        /// Returns null when MSBuild cannot be asked (never restored, wrong
        /// platform, no dotnet) — the caller decides how to report that; there is
        /// deliberately no guessed path.
        /// </remarks>
        public static string? ResolveTargetPath(
            string projectFilePath,
            string? activeWorktreePath,
            string buildConfiguration,
            string? platform,
            string? solutionDir = null)
        {
            string csproj = GitWorktreeService.ResolveActiveCsproj(
                projectFilePath, activeWorktreePath);
            return QueryMsBuildProperty(
                csproj, "TargetPath", buildConfiguration, platform, solutionDir);
        }

        public static string? ResolveBuildDir(
            string projectFilePath,
            string? activeWorktreePath,
            string buildConfiguration,
            string? platform,
            string? solutionDir = null)
        {
            string? targetPath = ResolveTargetPath(
                projectFilePath, activeWorktreePath, buildConfiguration,
                platform, solutionDir);
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
            catch (JsonException ex)
            {
                // Category B - report, do not rethrow. An empty list is what the
                // caller turns into "could not resolve configurations"; the shape
                // MSBuild actually returned is only knowable from the log.
                DevReloadDiagnostics.Report(
                    $"BuildService.GetCppConfigurations({vcxproj})", ex);
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
                if (proc == null)
                {
                    DevReloadDiagnostics.Info(
                        $"MSBuild query {getArg}: could not start '{fileName}'.");
                    return null;
                }

                // Both streams are read before the wait: MSBuild writes its
                // diagnostics to stderr, and a full pipe buffer on either stream
                // deadlocks a process that is waiting to write more.
                string output = proc.StandardOutput.ReadToEnd().Trim();
                string error = proc.StandardError.ReadToEnd().Trim();
                proc.WaitForExit();

                if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output)) return output;

                // The failure reason used to end here, discarded, and the caller
                // could only say "could not resolve". MSBuild's own words are the
                // whole diagnosis — project never restored, configuration the
                // project does not declare, wrong platform.
                DevReloadDiagnostics.Info(
                    $"MSBuild query {getArg} for '{csprojPath}' " +
                    $"({buildConfiguration}|{platform ?? "AnyCPU"}) returned nothing " +
                    $"(exit {proc.ExitCode}). " +
                    (error.Length > 0 ? error : "no output on stderr."));
                return null;
            }
            catch (Exception ex)
            {
                // Category B - report, do not rethrow. A property query is a
                // question; the callers all handle "no answer" and turn it into
                // their own message. What must not happen is the reason vanishing,
                // which is what the bare catch here used to do.
                DevReloadDiagnostics.Report(
                    $"BuildService.QueryMsBuild({getArg}, {csprojPath})", ex);
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
            catch (Exception ex)
            {
                // Category B - report, do not rethrow. An unreadable project file
                // answers "not SDK-style", which routes the caller to MSBuild.exe;
                // that is the better guess for a file we cannot read, but it is a
                // guess and the reason belongs in the log.
                DevReloadDiagnostics.Report($"BuildService.IsSdkStyle({csprojPath})", ex);
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
            catch (Exception ex)
            {
                // Category B - report, do not rethrow. No vswhere means no
                // full-framework MSBuild, which the caller already reports as
                // "install VS Build Tools" — but only the log can say whether
                // vswhere was missing or failed.
                DevReloadDiagnostics.Report("BuildService.LocateFrameworkMsBuild", ex);
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
