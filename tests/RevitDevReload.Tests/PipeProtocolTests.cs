using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Tasks;

using RevitDevReload.Core;

using Xunit;

namespace RevitDevReload.Tests
{
    public class PipeProtocolTests
    {
        [Fact]
        public void Request_Parses_IdCmdAndArgs()
        {
            var req = PipeProtocol.ParseRequest(
                "{\"id\":7,\"cmd\":\"load\",\"args\":{\"name\":\"MyAddin\"}}");

            Assert.Equal(7, req.Id);
            Assert.Equal("load", req.Cmd);
            Assert.Equal("MyAddin", req.Args!.Value.GetProperty("name").GetString());
        }

        [Fact]
        public void Request_WithoutArgs_ParsesToNullArgs()
        {
            var req = PipeProtocol.ParseRequest("{\"id\":1,\"cmd\":\"ping\"}");
            Assert.Equal("ping", req.Cmd);
            Assert.Null(req.Args);
        }

        [Fact]
        public void OkResponse_SerializesResultAndId()
        {
            string json = PipeProtocol.SerializeOk(3, new { value = 42 });
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(3, doc.RootElement.GetProperty("id").GetInt32());
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(42, doc.RootElement.GetProperty("result").GetProperty("value").GetInt32());
        }

        [Fact]
        public void ErrorResponse_SerializesMessage()
        {
            string json = PipeProtocol.SerializeError(4, "boom");
            using var doc = JsonDocument.Parse(json);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("boom", doc.RootElement.GetProperty("error").GetString());
        }

        [Fact]
        public async Task PipeServer_DispatchesRequest_AndAnswers()
        {
            string pipeName = "rdr-test-" + Guid.NewGuid().ToString("N");
            using var server = new PipeServer(pipeName, (cmd, args) =>
                cmd switch
                {
                    "ping" => new Dictionary<string, object> { ["pong"] = true },
                    "echo" => new Dictionary<string, object>
                    {
                        ["text"] = args!.Value.GetProperty("text").GetString()!
                    },
                    _ => throw new InvalidOperationException("unknown cmd " + cmd),
                });
            server.Start();

            using var client = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await Task.Run(() => client.Connect(5000));

            // Writer is declared AFTER the reader so it disposes FIRST —
            // reversed, the reader's dispose closes the pipe and the writer's
            // dispose-flush throws ObjectDisposedException.
            using var reader = new System.IO.StreamReader(client);
            using var writer = new System.IO.StreamWriter(client) { AutoFlush = true };

            await writer.WriteLineAsync("{\"id\":1,\"cmd\":\"ping\"}");
            string? line1 = await ReadLineWithTimeout(reader);
            using (var doc = JsonDocument.Parse(line1!))
            {
                Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
                Assert.True(doc.RootElement.GetProperty("result").GetProperty("pong").GetBoolean());
            }

            await writer.WriteLineAsync("{\"id\":2,\"cmd\":\"echo\",\"args\":{\"text\":\"hi\"}}");
            string? line2 = await ReadLineWithTimeout(reader);
            using (var doc = JsonDocument.Parse(line2!))
            {
                Assert.Equal(2, doc.RootElement.GetProperty("id").GetInt32());
                Assert.Equal("hi", doc.RootElement.GetProperty("result").GetProperty("text").GetString());
            }

            await writer.WriteLineAsync("{\"id\":3,\"cmd\":\"nope\"}");
            string? line3 = await ReadLineWithTimeout(reader);
            using (var doc = JsonDocument.Parse(line3!))
            {
                Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
                Assert.Contains("unknown cmd", doc.RootElement.GetProperty("error").GetString());
            }
        }

        [Fact]
        public async Task PipeServer_SurvivesClientDisconnect_AcceptsNextClient()
        {
            string pipeName = "rdr-test-" + Guid.NewGuid().ToString("N");
            using var server = new PipeServer(pipeName, (cmd, args) => new { ok = true });
            server.Start();

            // The delay between clients matters: it gives the server time to
            // observe the disconnect (IsConnected -> false) before recycling.
            // Without an unconditional Disconnect() in the server loop this
            // exact sequence wedged the pipe in production (Revit answered the
            // first CLI call, then every later connect timed out).
            for (int i = 0; i < 3; i++)
            {
                using (var client = new NamedPipeClientStream(
                    ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    await Task.Run(() => client.Connect(5000));
                    using var reader = new System.IO.StreamReader(client);
                    using var writer = new System.IO.StreamWriter(client) { AutoFlush = true };
                    await writer.WriteLineAsync("{\"id\":1,\"cmd\":\"x\"}");
                    Assert.NotNull(await ReadLineWithTimeout(reader));
                }
                await Task.Delay(400);
            }
        }

        private static async Task<string?> ReadLineWithTimeout(System.IO.StreamReader reader)
        {
            var readTask = reader.ReadLineAsync();
            var done = await Task.WhenAny(readTask, Task.Delay(5000));
            Assert.Same(readTask, done);
            return await readTask;
        }
    }
}
