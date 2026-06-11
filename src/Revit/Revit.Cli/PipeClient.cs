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
        //
        // PROTOCOL CONTRACT (mirrors PipeServer): one request per
        // connection, and the SERVER ends the connection. After reading the
        // response the client waits for the server's disconnect (EOF) before
        // disposing, so neither end ever reads/writes against a vanished
        // peer — normal operation is exception-free by construction.
        public static IReadOnlyList<int> RunningRevitPids()
        {
            var pids = new List<int>();
            foreach (var proc in Process.GetProcessesByName("Revit"))
                pids.Add(proc.Id);
            return pids;
        }

        // Expected absence (Revit still starting, host not loaded) is a
        // normal state — answered by enumerating the live pipe namespace,
        // never by connect-and-catch-timeout.
        public static bool PipeExists(int pid)
        {
            string name = $"RevitDevReload.{pid}";
            foreach (string pipe in Directory.EnumerateFiles(@"\\.\pipe\"))
            {
                if (pipe.EndsWith(name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public static int? FindPidWithPipe(int connectTimeoutMs = 300)
        {
            foreach (int pid in RunningRevitPids())
            {
                if (PipeExists(pid) && TryPing(pid, connectTimeoutMs))
                    return pid;
            }
            return null;
        }

        public static bool TryPing(int pid, int connectTimeoutMs = 300)
        {
            if (!PipeExists(pid)) return false;
            try
            {
                string response = Send(pid, "{\"id\":0,\"cmd\":\"ping\"}", connectTimeoutMs);
                return response.Contains("\"ok\":true");
            }
            catch
            {
                // Pipe existed but the exchange failed (host mid-shutdown,
                // stale pipe) — genuinely abnormal, reported as "no".
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

            // The writer is scoped to the write and disposed while the pipe
            // is still healthy: after the server disconnects, ANY write-side
            // operation — including StreamWriter's dispose-flush — throws on
            // net8. The tail of the connection is read-only by contract.
            using (var writer = new StreamWriter(client, utf8, 1024, leaveOpen: true)
                   { AutoFlush = true })
            {
                writer.WriteLine(requestLine);
            }

            string? response = reader.ReadLine();
            if (response == null)
                throw new IOException("pipe closed before response");

            // Contract: hold the connection until the server disconnects
            // (EOF) — it does so only after WaitForPipeDrain confirmed we
            // read the response. This ReadLine returns null on disconnect.
            reader.ReadLine();
            return response;
        }
    }
}
