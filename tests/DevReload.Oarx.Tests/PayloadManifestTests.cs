using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using DevReload.Oarx.Payload;

using Xunit;

namespace DevReload.Oarx.Tests;

/// <summary>A payload folder on disk, deleted after the test.</summary>
internal sealed class PayloadDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "devreload-payload-" + Guid.NewGuid().ToString("N"));

    public PayloadDir() => Directory.CreateDirectory(Path);

    /// <summary>Write a file and return its lower-case SHA-256.</summary>
    public string Write(string relative, string content)
    {
        string full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return Hash(content);
    }

    public void Manifest(string json) =>
        File.WriteAllText(System.IO.Path.Combine(Path, PayloadManifestReader.FileName), json);

    public string Full(string relative) => System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, relative));

    public static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

public class PayloadManifestTests
{
    [Fact]
    public void Reads_the_native_group_schema_with_comments_in_order()
    {
        using var dir = new PayloadDir();
        string log = dir.Write("NorsynLogging.dll", "log");
        string ui = dir.Write("TraceUi.dll", "ui");
        string dbx = dir.Write("A.dbx", "dbx");
        string arx = dir.Write("A.arx", "arx");
        dir.Manifest($$"""
            {
              // the schema NSLOAD's native groups already use
              "preloadNative": ["NorsynLogging.dll"],
              "preloadManaged": ["TraceUi.dll"],
              "modules": ["A.dbx", "A.arx"],
              "sha256": {
                "NorsynLogging.dll": "{{log}}",
                "TraceUi.dll": "{{ui}}",
                "A.dbx": "{{dbx}}",
                "A.arx": "{{arx}}",
              },
            }
            """);

        var m = PayloadManifestReader.Read(dir.Path);

        Assert.Equal(new[] { dir.Full("A.dbx"), dir.Full("A.arx") }, m.Modules.Select(f => f.Path));
        Assert.Equal(dir.Full("NorsynLogging.dll"), Assert.Single(m.PreloadNative).Path);
        Assert.Equal(ui, Assert.Single(m.PreloadManaged).Sha256);
        Assert.Empty(m.PostloadManaged);
    }

    [Fact]
    public void Refuses_a_file_whose_hash_does_not_match()
    {
        using var dir = new PayloadDir();
        dir.Write("A.dbx", "dbx");
        dir.Manifest($$"""{ "modules": ["A.dbx"], "sha256": { "A.dbx": "{{PayloadDir.Hash("other")}}" } }""");

        var ex = Assert.Throws<PayloadException>(() => PayloadManifestReader.Read(dir.Path));
        Assert.Contains("A.dbx", ex.Message);
        Assert.Contains("hash", ex.Message);
    }

    [Fact]
    public void Refuses_an_entry_with_no_hash()
    {
        using var dir = new PayloadDir();
        dir.Write("A.dbx", "dbx");
        dir.Manifest("""{ "modules": ["A.dbx"], "sha256": {} }""");

        var ex = Assert.Throws<PayloadException>(() => PayloadManifestReader.Read(dir.Path));
        Assert.Contains("A.dbx", ex.Message);
    }

    [Fact]
    public void Refuses_a_missing_file()
    {
        using var dir = new PayloadDir();
        dir.Manifest("""{ "modules": ["A.dbx"], "sha256": { "A.dbx": "00" } }""");

        var ex = Assert.Throws<PayloadException>(() => PayloadManifestReader.Read(dir.Path));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void Refuses_a_relative_entry_that_leaves_the_payload_folder()
    {
        using var dir = new PayloadDir();
        dir.Manifest("""{ "modules": ["..\\evil.arx"], "sha256": { "..\\evil.arx": "00" } }""");

        var ex = Assert.Throws<PayloadException>(() => PayloadManifestReader.Read(dir.Path));
        Assert.Contains("outside", ex.Message);
    }

    [Fact]
    public void Refuses_a_payload_with_no_modules()
    {
        using var dir = new PayloadDir();
        dir.Manifest("""{ "modules": [], "sha256": {} }""");

        Assert.Throws<PayloadException>(() => PayloadManifestReader.Read(dir.Path));
    }

    [Fact]
    public void Refuses_a_module_that_is_not_a_dbx_or_arx()
    {
        using var dir = new PayloadDir();
        string h = dir.Write("A.dll", "x");
        dir.Manifest($$"""{ "modules": ["A.dll"], "sha256": { "A.dll": "{{h}}" } }""");

        var ex = Assert.Throws<PayloadException>(() => PayloadManifestReader.Read(dir.Path));
        Assert.Contains("A.dll", ex.Message);
    }

    [Fact]
    public void Refuses_a_folder_with_no_manifest()
    {
        using var dir = new PayloadDir();

        var ex = Assert.Throws<PayloadException>(() => PayloadManifestReader.Read(dir.Path));
        Assert.Contains(PayloadManifestReader.FileName, ex.Message);
    }

    [Fact]
    public void Accepts_an_absolute_entry_outside_the_folder()
    {
        using var dir = new PayloadDir();
        using var shared = new PayloadDir();
        string h = shared.Write("NorsynLogging.dll", "log");
        string abs = shared.Full("NorsynLogging.dll");
        string m = dir.Write("A.dbx", "dbx");
        string absJson = abs.Replace("\\", "\\\\");
        dir.Manifest($$"""
            { "preloadNative": ["{{absJson}}"], "modules": ["A.dbx"],
              "sha256": { "{{absJson}}": "{{h}}", "A.dbx": "{{m}}" } }
            """);

        var manifest = PayloadManifestReader.Read(dir.Path);

        Assert.Equal(abs, Assert.Single(manifest.PreloadNative).Path);
    }
}
