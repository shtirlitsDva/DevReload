using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Xunit;

namespace Acad.Rpc.Core.Tests;

/// <summary>
/// The plugins.json format, read and written through DevReload's own
/// <c>PluginConfigLoader.Deserialize</c> / <c>Serialize</c> (the methods
/// <c>Load</c> / <c>Save</c> use), reached by reflection from the built
/// DevReload.dll the same way <see cref="DevReloadSurfaceTests"/> reaches its
/// tools, so no AutoCAD assembly enters the test build.
/// </summary>
public class PluginConfigRoundTripTests
{
    private const string Json = """
        {
          "plugins": [
            {
              "name": "HostSwitched",
              "commandPrefix": "HS",
              "buildConfiguration": "Debug",
              "projectFilePath": "C:\\repo\\HostSwitched.csproj",
              "msBuildProperties": [ "NorsynHost=BricsCAD", "NorsynManagedOutRoot=C:\\repo\\x64\\lane\\" ]
            },
            {
              "name": "Plain",
              "buildConfiguration": "Debug",
              "projectFilePath": "C:\\repo\\Plain.csproj"
            },
            {
              "name": "EmptyList",
              "buildConfiguration": "Debug",
              "projectFilePath": "C:\\repo\\EmptyList.csproj",
              "msBuildProperties": []
            }
          ],
          "oarxPlugins": []
        }
        """;

    [Fact]
    public void MsBuildProperties_AreRead()
    {
        object config = Deserialize(Json);

        Assert.Equal(new[] { "NorsynHost=BricsCAD", @"NorsynManagedOutRoot=C:\repo\x64\lane\" },
            Properties(Plugin(config, "HostSwitched")));
        Assert.Null(Properties(Plugin(config, "Plain")));
    }

    [Fact]
    public void MsBuildProperties_SurviveARoundTrip_AndAreOmittedWhenEmpty()
    {
        var plugins = JsonNode.Parse(Serialize(Deserialize(Json)))!["plugins"]!.AsArray()
            .OfType<JsonObject>()
            .ToDictionary(p => p["name"]!.GetValue<string>());

        Assert.Equal(new[] { "NorsynHost=BricsCAD", @"NorsynManagedOutRoot=C:\repo\x64\lane\" },
            plugins["HostSwitched"]["msBuildProperties"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.False(plugins["Plain"].ContainsKey("msBuildProperties"));
        Assert.False(plugins["EmptyList"].ContainsKey("msBuildProperties"));
    }

    // ── Reflection over the built DevReload.dll ──────────────────────

    private static readonly Assembly DevReload = Assembly.LoadFrom(ResolveDevReloadDllPath());

    private static Type Loader => DevReload.GetType("DevReload.PluginConfigLoader", throwOnError: true)!;

    private static object Deserialize(string json) =>
        Loader.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object[] { json })!;

    private static string Serialize(object config) =>
        (string)Loader.GetMethod("Serialize", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new[] { config })!;

    private static object Plugin(object config, string name) =>
        ((IEnumerable)config.GetType().GetProperty("Plugins")!.GetValue(config)!)
            .Cast<object>()
            .Single(p => (string)p.GetType().GetProperty("Name")!.GetValue(p)! == name);

    private static string[]? Properties(object entry) =>
        ((IEnumerable?)entry.GetType().GetProperty("MsBuildProperties")!.GetValue(entry))
            ?.Cast<string>().ToArray();

    private static string ResolveDevReloadDllPath()
    {
        string testDir = Path.GetDirectoryName(typeof(PluginConfigRoundTripTests).Assembly.Location)!;
        string? cursor = testDir;
        while (cursor != null && !File.Exists(Path.Combine(cursor, "DevReload.sln")))
            cursor = Directory.GetParent(cursor)?.FullName;
        if (cursor == null)
            throw new InvalidOperationException("Could not locate DevReload.sln walking up from " + testDir);

        string candidate = Path.Combine(cursor, "src", "Autocad", "DevReload", "bin", "Debug", "DevReload.dll");
        if (!File.Exists(candidate))
            throw new InvalidOperationException(
                $"DevReload.dll not found at {candidate}. Build DevReload (Debug|x64) before running tests.");
        return candidate;
    }
}
