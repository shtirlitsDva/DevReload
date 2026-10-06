using System;
using System.Collections.Generic;
using System.Linq;

using DevReload.Oarx.Payload;

using Xunit;

namespace DevReload.Oarx.Tests;

public class ModuleUnloaderTests
{
    /// <summary>A host linker that remembers what it was asked. A module is
    /// registered and mapped until its unload call is honoured; a refusing
    /// module stays registered, a pinned one stays mapped.</summary>
    private sealed class FakeLinker : IModuleLinker
    {
        private readonly Dictionary<string, string> _registered = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _mapped = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Refuses { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Pinned { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> CallThrows { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Asked { get; } = new();

        public FakeLinker Loaded(params string[] paths)
        {
            foreach (var p in paths)
            {
                string n = System.IO.Path.GetFileName(p);
                _registered[n] = p;
                _mapped[n] = p;
            }
            return this;
        }

        /// <summary>Mapped, but no longer listed by the linker.</summary>
        public FakeLinker MappedOnly(string path)
        {
            _mapped[System.IO.Path.GetFileName(path)] = path;
            return this;
        }

        public IEnumerable<string> MappedPaths => _mapped.Values;

        public bool IsRegistered(string m) => _registered.ContainsKey(m);
        public string? MappedPath(string m) => _mapped.TryGetValue(m, out var p) ? p : null;

        public string? TryUnload(string m)
        {
            Asked.Add(m);
            if (Refuses.Contains(m)) return "eInvalidInput";
            _registered.Remove(m);
            if (!Pinned.Contains(m)) _mapped.Remove(m);
            return CallThrows.Contains(m) ? "Operation is not valid" : null;
        }
    }

    private static PayloadFile F(string dir, string name, string sha = "aa") => new($@"C:\p\{dir}\{name}", sha);

    private static PayloadManifest Payload(string dir, string dbxSha = "d1", string arxSha = "a1") =>
        new($@"C:\p\{dir}",
            PreloadNative: new[] { F(dir, "NorsynLogging.dll", "l1") },
            PreloadManaged: Array.Empty<PayloadFile>(),
            Modules: new[] { F(dir, "A.dbx", dbxSha), F(dir, "A.arx", arxSha) },
            PostloadManaged: Array.Empty<PayloadFile>(),
            Files: Array.Empty<PayloadFile>());

    private static readonly string[] ArxThenDbx = { "A.arx", "A.dbx" };

    [Fact]
    public void Every_module_out_is_a_complete_run()
    {
        var linker = new FakeLinker().Loaded(@"C:\p\1\A.dbx", @"C:\p\1\A.arx");

        var run = ModuleUnloader.Run(ArxThenDbx, linker);

        Assert.True(run.Complete);
        Assert.Equal(ArxThenDbx, run.Unloaded);
        Assert.Empty(run.StillLoaded);
        Assert.Null(ModuleUnloader.Describe(run, "BricsCAD"));
    }

    [Fact]
    public void A_dbx_that_refuses_its_unload_is_not_reported_unloaded()
    {
        var linker = new FakeLinker().Loaded(@"C:\p\1\A.dbx", @"C:\p\1\A.arx");
        linker.Refuses.Add("A.dbx");

        var run = ModuleUnloader.Run(ArxThenDbx, linker);

        Assert.False(run.Complete);
        Assert.Equal("A.dbx", run.StoppedAt);
        Assert.Equal(ModuleUnloadState.Refused, run.State);
        Assert.Equal(new[] { "A.arx" }, run.Unloaded);
        Assert.Equal(new[] { "A.dbx" }, run.StillLoaded);
    }

    [Fact]
    public void A_refusing_arx_stops_the_run_before_the_dbx_it_needs()
    {
        var linker = new FakeLinker().Loaded(@"C:\p\1\A.dbx", @"C:\p\1\A.arx");
        linker.Refuses.Add("A.arx");

        var run = ModuleUnloader.Run(ArxThenDbx, linker);

        Assert.Equal(new[] { "A.arx" }, linker.Asked);
        Assert.Equal("A.arx", run.StoppedAt);
        Assert.Empty(run.Unloaded);
        Assert.Equal(ArxThenDbx, run.StillLoaded);
    }

    [Fact]
    public void A_module_the_linker_released_but_the_process_still_maps_stops_the_run()
    {
        var linker = new FakeLinker().Loaded(@"C:\p\1\A.dbx", @"C:\p\1\A.arx");
        linker.Pinned.Add("A.arx");

        var run = ModuleUnloader.Run(ArxThenDbx, linker);

        Assert.Equal(ModuleUnloadState.StillMapped, run.State);
        Assert.Equal("A.arx", run.StoppedAt);
        Assert.DoesNotContain("A.dbx", linker.Asked);
    }

    [Fact]
    public void A_module_mapped_but_no_longer_listed_is_not_asked_again()
    {
        var linker = new FakeLinker().MappedOnly(@"C:\p\1\A.dbx");

        var run = ModuleUnloader.Run(new[] { "A.dbx" }, linker);

        Assert.Empty(linker.Asked);
        Assert.Equal(ModuleUnloadState.StillMapped, run.State);
    }

    [Fact]
    public void A_failed_call_whose_module_left_anyway_counts_as_unloaded()
    {
        var linker = new FakeLinker().Loaded(@"C:\p\1\A.dbx", @"C:\p\1\A.arx");
        linker.CallThrows.Add("A.arx");

        var run = ModuleUnloader.Run(ArxThenDbx, linker);

        Assert.True(run.Complete);
        Assert.Equal(ArxThenDbx, run.Unloaded);
    }

    [Fact]
    public void A_module_already_out_is_skipped_without_a_call()
    {
        var linker = new FakeLinker().Loaded(@"C:\p\1\A.dbx");

        var run = ModuleUnloader.Run(ArxThenDbx, linker);

        Assert.True(run.Complete);
        Assert.Equal(new[] { "A.dbx" }, linker.Asked);
    }

    [Fact]
    public void The_refusal_names_the_module_says_it_refused_and_points_at_its_log()
    {
        var linker = new FakeLinker().Loaded(@"C:\p\1\A.dbx", @"C:\p\1\A.arx");
        linker.Refuses.Add("A.dbx");

        string msg = ModuleUnloader.Describe(ModuleUnloader.Run(ArxThenDbx, linker), "BricsCAD", " HINT")!;

        Assert.Contains("'A.dbx' refused its own unload", msg);
        Assert.Contains("not a DevReload failure", msg);
        Assert.Contains("module's own log", msg);
        Assert.Contains("Unloaded before it: A.arx.", msg);
        Assert.Contains("Still loaded: A.dbx", msg);
        Assert.DoesNotContain("A.dbx is unloaded", msg);
        Assert.DoesNotContain("modules are unloaded", msg);
        Assert.EndsWith(" HINT", msg);
    }

    [Fact]
    public void The_record_keeps_every_module_still_in_at_its_old_path()
    {
        var prev = Payload("1");
        var kept = ModuleUnloader.Remaining(prev, new[] { "A.arx" });

        Assert.Equal(new[] { F("1", "A.dbx", "d1") }, kept.Modules);
        Assert.Equal(prev.PreloadNative, kept.PreloadNative);
        Assert.Equal(prev.Directory, kept.Directory);
    }

    /// <summary>The live failure (BricsCAD V26, 2026-10-06): the dbx refused its
    /// unload, the record was dropped, and every later reload was refused as
    /// "already loaded ... not by this payload". With the record kept, the retry
    /// plans to unload just the dbx and carries on.</summary>
    [Fact]
    public void After_a_refusal_the_next_reload_continues_with_the_module_still_in()
    {
        var prev = Payload("1");
        var linker = new FakeLinker().Loaded(@"C:\p\1\A.dbx", @"C:\p\1\A.arx", @"C:\p\1\NorsynLogging.dll");
        linker.Refuses.Add("A.dbx");
        var next = Payload("2", dbxSha: "d2", arxSha: "a2");

        var first = PayloadPlanner.Decide(prev, next, ProcessImages.FromPaths(linker.MappedPaths, Array.Empty<string>()));
        var plan = Assert.IsType<PayloadDecision.Proceed>(first);
        var run = ModuleUnloader.Run(plan.UnloadModules, linker);
        Assert.False(run.Complete);
        var record = ModuleUnloader.Remaining(prev, run.Unloaded);

        // The module stops refusing (its drawings were closed), and the caller retries.
        linker.Refuses.Clear();
        var now = ProcessImages.FromPaths(linker.MappedPaths, Array.Empty<string>());
        // A dropped record is what made the live session unrecoverable.
        Assert.IsType<PayloadDecision.Refuse>(PayloadPlanner.Decide(null, next, now));
        var retry = PayloadPlanner.Decide(record, next, now);

        var proceed = Assert.IsType<PayloadDecision.Proceed>(retry);
        Assert.Equal(new[] { "A.dbx" }, proceed.UnloadModules);
        Assert.True(ModuleUnloader.Run(proceed.UnloadModules, linker).Complete);
    }
}
