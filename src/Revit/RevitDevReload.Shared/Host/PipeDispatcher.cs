using System;
using System.Linq;
using System.Text.Json;

using DevReload.Core;

using RevitDevReload.Core;

namespace RevitDevReload
{
    // Maps pipe commands onto RevitPluginManager. Runs on the pipe thread —
    // the manager marshals API-context work itself, so dispatch stays thin.
    // Same single-entry-point rule as the AutoCAD side: UI and pipe both call
    // RevitPluginManager; nothing is duplicated here.
    public static class PipeDispatcher
    {
        public static object? Dispatch(string cmd, JsonElement? args)
        {
            switch (cmd)
            {
                case "ping":
                    return new
                    {
                        app = "RevitDevReload",
                        revitVersion = RevitContext.RevitVersionYear,
                        trueUnload = RevitPluginManager.SupportsTrueUnload,
                    };

                case "get_state":
                    return RevitPluginManager.All.Select(r => new
                    {
                        name = r.Entry.Name,
                        loaded = r.IsLoaded,
                        dllPath = r.Entry.DllPath,
                        projectFilePath = r.Entry.ProjectFilePath,
                        buildConfiguration = r.Entry.BuildConfiguration,
                        activeWorktreePath = r.Entry.ActiveWorktreePath,
                        loadOnStartup = r.Entry.LoadOnStartup,
                        commands = r.Commands.Select(c => c.FullClassName).ToList(),
                    }).ToList();

                case "register_plugin":
                {
                    string csproj = RequireString(args, "projectFilePath");
                    string name = System.IO.Path.GetFileNameWithoutExtension(csproj);
                    string config = OptionalString(args, "buildConfiguration") ?? "Debug";
                    string? platform = BuildService.IsSdkStyle(csproj) ? "x64" : null;
                    string? dllPath = BuildService.QueryMsBuildProperty(
                        csproj, "TargetPath", config, platform);
                    var entry = new RevitPluginEntry
                    {
                        Name = name,
                        ProjectFilePath = csproj,
                        DllPath = dllPath,
                        BuildConfiguration = config,
                    };
                    return ToWire(RevitPluginManager.Register(entry));
                }

                case "unregister_plugin":
                    return ToWire(RevitPluginManager.Unregister(
                        RequireString(args, "name")));

                case "load":
                    return ToWire(RevitPluginManager.Load(RequireString(args, "name")));

                case "reload":
                    return ToWire(RevitPluginManager.Reload(RequireString(args, "name")));

                case "unload":
                    return ToWire(RevitPluginManager.Unload(RequireString(args, "name")));

                case "run_command":
                    return ToWire(RevitPluginManager.RunCommand(
                        RequireString(args, "plugin"),
                        RequireString(args, "command")));

                case "get_log":
                {
                    int tail = args.HasValue
                               && args.Value.TryGetProperty("tail", out var t)
                        ? t.GetInt32() : 200;
                    return DevReloadLogBuffer.Snapshot(tail);
                }

                case "open_window":
                    RevitDevReloadApp.ShowManagerWindowFromAnyThread();
                    return new { shown = true };

                case "quit":
                    // Automation-only hard exit (the CLI's stop command).
                    // Delayed so the response reaches the client first.
                    System.Threading.Tasks.Task.Run(async () =>
                    {
                        await System.Threading.Tasks.Task.Delay(500);
                        Environment.Exit(0);
                    });
                    return new { quitting = true };

                default:
                    throw new InvalidOperationException($"unknown command '{cmd}'");
            }
        }

        private static object ToWire(RevitActionResult r) => new
        {
            pluginName = r.PluginName,
            success = r.Success,
            message = r.Message,
            commandCount = r.CommandCount,
            loaded = r.Loaded,
            build = r.Build == null ? null : new
            {
                success = r.Build.Success,
                outputPath = r.Build.OutputPath,
                warnings = r.Build.Warnings,
                errors = r.Build.Errors,
                log = r.Build.Success ? null : r.Build.Log,
            },
        };

        private static string RequireString(JsonElement? args, string name)
        {
            if (args.HasValue && args.Value.TryGetProperty(name, out var v)
                && v.ValueKind == JsonValueKind.String)
                return v.GetString()!;
            throw new InvalidOperationException($"missing required arg '{name}'");
        }

        private static string? OptionalString(JsonElement? args, string name)
        {
            return args.HasValue && args.Value.TryGetProperty(name, out var v)
                   && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
    }
}
