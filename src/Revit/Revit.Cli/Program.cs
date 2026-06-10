using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

using DevReload.Core;

using RevitDevReload.Core;

namespace Revit.Cli
{
    // Test/agent driver for RevitDevReload — the Revit counterpart of the
    // acad_start/send-command tooling. Commands:
    //
    //   list-installs
    //   deploy    --rvt <year> [--config Debug|Release]
    //   undeploy  --rvt <year>
    //   start     --rvt <year> [--watch-dialogs] [--wait-pipe <seconds>]
    //   wait-pipe [--timeout <seconds>]
    //   send      --cmd <name> [--args <json>]
    //   stop
    //
    // send/wait-pipe/stop target the (single) running Revit that answers the
    // RevitDevReload.{pid} pipe.
    public static class Program
    {
        private const string AddinId = "cab77f49-ae73-4f4e-a014-39b717f0691b";

        public static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0) { PrintUsage(); return 2; }
                return args[0] switch
                {
                    "list-installs" => ListInstalls(),
                    "deploy" => Deploy(ParseOptions(args)),
                    "undeploy" => Undeploy(ParseOptions(args)),
                    "start" => Start(ParseOptions(args)),
                    "wait-pipe" => WaitPipe(ParseOptions(args)),
                    "send" => Send(ParseOptions(args)),
                    "stop" => Stop(),
                    _ => Fail($"unknown command '{args[0]}'"),
                };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                return 1;
            }
        }

        private static int ListInstalls()
        {
            foreach (var install in RevitInstalls.Discover())
                Console.WriteLine($"{install.Year}\t{install.ExePath}");
            return 0;
        }

        private static int Deploy(Dictionary<string, string> opts)
        {
            int year = RequireYear(opts);
            string config = opts.TryGetValue("config", out var c) ? c : "Debug";
            string yy = year.ToString().Substring(2);

            string repoRoot = FindRepoRoot();
            string csproj = Path.Combine(
                repoRoot, "src", "Revit", $"RevitDevReload.R{yy}",
                $"RevitDevReload.R{yy}.csproj");
            if (!File.Exists(csproj))
                return Fail($"no host project for Revit {year}: {csproj}");

            Console.WriteLine($"building RevitDevReload.R{yy} ({config})...");
            var build = BuildService.BuildProject(csproj, config, "x64", Console.WriteLine);
            if (!build.Success || build.OutputPath == null)
                return Fail("host build failed");

            string manifest = ManifestPath(year);
            AddinManifestWriter.Write(
                manifest,
                assemblyPath: build.OutputPath,
                addInId: AddinId,
                fullClassName: "RevitDevReload.RevitDevReloadApp",
                name: "RevitDevReload",
                vendorId: "DVRL",
                vendorDescription: "RevitDevReload hot-reload host");
            Console.WriteLine($"manifest written: {manifest}");
            Console.WriteLine($"assembly: {build.OutputPath}");
            return 0;
        }

        private static int Undeploy(Dictionary<string, string> opts)
        {
            int year = RequireYear(opts);
            string manifest = ManifestPath(year);
            if (File.Exists(manifest))
            {
                File.Delete(manifest);
                Console.WriteLine($"deleted {manifest}");
            }
            else
            {
                Console.WriteLine("nothing to undeploy");
            }
            return 0;
        }

        private static int Start(Dictionary<string, string> opts)
        {
            int year = RequireYear(opts);
            var install = RevitInstalls.Require(year);

            var psi = new ProcessStartInfo
            {
                FileName = install.ExePath,
                WorkingDirectory = install.InstallDir,
                UseShellExecute = true,
            };
            var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("failed to start Revit");
            Console.WriteLine($"started Revit {year} (pid {proc.Id})");

            DialogWatcher? watcher = null;
            if (opts.ContainsKey("watch-dialogs"))
            {
                watcher = new DialogWatcher(proc.Id);
                watcher.Clicked += msg => Console.WriteLine("[dialog-watcher] " + msg);
                watcher.Start();
                Console.WriteLine("[dialog-watcher] watching for security dialogs");
            }

            using (watcher)
            {
                if (opts.TryGetValue("wait-pipe", out var w))
                {
                    int timeout = int.Parse(w);
                    return WaitForPipe(timeout);
                }
                else if (watcher != null)
                {
                    // Keep watching through the startup window even when the
                    // caller didn't ask to wait for the pipe.
                    int? pid = WaitForPipeCore(300);
                    Console.WriteLine(pid.HasValue
                        ? $"pipe up (pid {pid})"
                        : "pipe did not come up within 300 s");
                    return pid.HasValue ? 0 : 1;
                }
            }
            return 0;
        }

        private static int WaitPipe(Dictionary<string, string> opts)
        {
            int timeout = opts.TryGetValue("timeout", out var t) ? int.Parse(t) : 300;
            return WaitForPipe(timeout);
        }

        private static int WaitForPipe(int timeoutSeconds)
        {
            int? pid = WaitForPipeCore(timeoutSeconds);
            if (pid == null)
                return Fail($"no RevitDevReload pipe within {timeoutSeconds} s");
            Console.WriteLine($"pipe up (pid {pid})");
            return 0;
        }

        private static int? WaitForPipeCore(int timeoutSeconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                int? pid = PipeClient.FindPidWithPipe();
                if (pid.HasValue) return pid;
                Thread.Sleep(2000);
            }
            return null;
        }

        private static int Send(Dictionary<string, string> opts)
        {
            if (!opts.TryGetValue("cmd", out var cmd))
                return Fail("send requires --cmd <name>");
            string argsJson = opts.TryGetValue("args", out var a) ? a : "null";

            int pid = PipeClient.FindPidWithPipe()
                ?? throw new InvalidOperationException(
                    "no running Revit answers the RevitDevReload pipe");

            string request = $"{{\"id\":1,\"cmd\":\"{cmd}\",\"args\":{argsJson}}}";
            string response = PipeClient.Send(pid, request, connectTimeoutMs: 5000);
            Console.WriteLine(response);
            return response.Contains("\"ok\":true") ? 0 : 1;
        }

        private static int Stop()
        {
            int? pid = PipeClient.FindPidWithPipe();
            if (pid.HasValue)
            {
                try
                {
                    PipeClient.Send(pid.Value, "{\"id\":1,\"cmd\":\"quit\"}");
                    Console.WriteLine($"quit sent to pid {pid}");
                }
                catch
                {
                    // response may be cut off by the exit — that's fine
                }
                // Give the graceful path a moment, then make sure.
                Thread.Sleep(3000);
            }

            foreach (var proc in Process.GetProcessesByName("Revit"))
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    Console.WriteLine($"killed pid {proc.Id}");
                }
            }
            return 0;
        }

        // ── helpers ──────────────────────────────────────────────────

        private static string ManifestPath(int year) => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Autodesk", "Revit", "Addins", year.ToString(), "RevitDevReload.addin");

        private static int RequireYear(Dictionary<string, string> opts)
        {
            if (opts.TryGetValue("rvt", out var y) && int.TryParse(y, out int year))
                return year;
            throw new InvalidOperationException("--rvt <year> is required");
        }

        private static string FindRepoRoot()
        {
            string? cursor = AppContext.BaseDirectory;
            while (cursor != null && !File.Exists(Path.Combine(cursor, "DevReload.sln")))
                cursor = Path.GetDirectoryName(cursor);
            return cursor ?? throw new InvalidOperationException(
                "DevReload.sln not found above " + AppContext.BaseDirectory);
        }

        private static Dictionary<string, string> ParseOptions(string[] args)
        {
            var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--")) continue;
                string key = args[i].Substring(2);
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    opts[key] = args[++i];
                }
                else
                {
                    opts[key] = "true";
                }
            }
            return opts;
        }

        private static int Fail(string message)
        {
            Console.Error.WriteLine("error: " + message);
            return 1;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("revit-cli — RevitDevReload test/agent driver");
            Console.WriteLine("  list-installs");
            Console.WriteLine("  deploy --rvt <year> [--config Debug|Release]");
            Console.WriteLine("  undeploy --rvt <year>");
            Console.WriteLine("  start --rvt <year> [--watch-dialogs] [--wait-pipe <seconds>]");
            Console.WriteLine("  wait-pipe [--timeout <seconds>]");
            Console.WriteLine("  send --cmd <name> [--args <json>]");
            Console.WriteLine("  stop");
        }
    }
}
