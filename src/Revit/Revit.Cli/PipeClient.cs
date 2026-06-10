using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;

namespace Revit.Cli
{
    public static class PipeClient
    {
        // Pipe names are RevitDevReload.{pid}; the CLI keys on Revit
        // processes, not on a registry of sessions.
        public static IReadOnlyList<int> RunningRevitPids()
        {
            var pids = new List<int>();
            foreach (var proc in Process.GetProcessesByName("Revit"))
                pids.Add(proc.Id);
            return pids;
        }

        public static int? FindPidWithPipe(int connectTimeoutMs = 300)
        {
            foreach (int pid in RunningRevitPids())
            {
                if (TryPing(pid, connectTimeoutMs))
                    return pid;
            }
            return null;
        }

        public static bool TryPing(int pid, int connectTimeoutMs = 300)
        {
            try
            {
                string response = Send(pid, "{\"id\":0,\"cmd\":\"ping\"}", connectTimeoutMs);
                return response.Contains("\"ok\":true");
            }
            catch
            {
                return false;
            }
        }

        public static string Send(int pid, string requestLine, int connectTimeoutMs = 5000)
        {
            using var client = new NamedPipeClientStream(
                ".", $"RevitDevReload.{pid}", PipeDirection.InOut,
                PipeOptions.Asynchronous);
            client.Connect(connectTimeoutMs);

            var utf8 = new UTF8Encoding(false);
            using var reader = new StreamReader(client, utf8, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(client, utf8, 1024, leaveOpen: true)
            { AutoFlush = true };

            writer.WriteLine(requestLine);
            string? response = reader.ReadLine();
            return response ?? throw new IOException("pipe closed before response");
        }
    }
}
