using System;
using System.Collections.Generic;
using System.Linq;

using DevReload.Oarx.Payload;

using Xunit;

namespace DevReload.Oarx.Tests;

public class PayloadPlannerTests
{
    private static PayloadFile F(string dir, string name, string sha = "aa") => new($@"C:\p\{dir}\{name}", sha);

    private static PayloadManifest Payload(string dir, string logSha = "l1", string uiSha = "u1",
                                           string dbxSha = "d1", string arxSha = "a1") =>
        new($@"C:\p\{dir}",
            PreloadNative: new[] { F(dir, "NorsynLogging.dll", logSha) },
            PreloadManaged: new[] { F(dir, "TraceUi.dll", uiSha) },
            Modules: new[] { F(dir, "A.dbx", dbxSha), F(dir, "A.arx", arxSha) },
            PostloadManaged: Array.Empty<PayloadFile>());

    private static ProcessImages Images(IEnumerable<string>? native = null, IEnumerable<string>? managed = null) =>
        ProcessImages.FromPaths(native ?? Array.Empty<string>(), managed ?? Array.Empty<string>());

    private static ProcessImages MappedFrom(PayloadManifest p) =>
        Images(p.PreloadNative.Concat(p.Modules).Select(f => f.Path), p.PreloadManaged.Select(f => f.Path));

    [Fact]
    public void First_load_maps_everything_in_order_and_unloads_nothing()
    {
        var next = Payload("1");

        var plan = Assert.IsType<PayloadDecision.Proceed>(PayloadPlanner.Decide(null, next, Images()));

        Assert.Empty(plan.UnloadModules);
        Assert.Equal(next.PreloadNative, plan.PinNative);
        Assert.Equal(next.PreloadManaged, plan.PreloadManaged);
        Assert.Equal(next.Modules, plan.Modules);
    }

    [Fact]
    public void Reload_unloads_the_previous_modules_in_reverse_load_order()
    {
        var prev = Payload("1");
        var next = Payload("2", dbxSha: "d2", arxSha: "a2");

        var plan = Assert.IsType<PayloadDecision.Proceed>(PayloadPlanner.Decide(prev, next, MappedFrom(prev)));

        Assert.Equal(new[] { "A.arx", "A.dbx" }, plan.UnloadModules);
        Assert.Equal(next.Modules, plan.Modules);
    }

    [Fact]
    public void An_unchanged_companion_already_mapped_from_the_previous_folder_is_kept()
    {
        var prev = Payload("1");
        var next = Payload("2", dbxSha: "d2");

        var plan = Assert.IsType<PayloadDecision.Proceed>(PayloadPlanner.Decide(prev, next, MappedFrom(prev)));

        Assert.Empty(plan.PinNative);
        Assert.Empty(plan.PreloadManaged);
    }

    [Fact]
    public void A_changed_native_companion_needs_a_restart()
    {
        var prev = Payload("1");
        var next = Payload("2", logSha: "l2");

        var refuse = Assert.IsType<PayloadDecision.Refuse>(PayloadPlanner.Decide(prev, next, MappedFrom(prev)));

        Assert.True(refuse.RestartRequired);
        Assert.Equal(next.PreloadNative[0].Path, refuse.File);
    }

    [Fact]
    public void A_changed_managed_companion_needs_a_restart()
    {
        var prev = Payload("1");
        var next = Payload("2", uiSha: "u2");

        var refuse = Assert.IsType<PayloadDecision.Refuse>(PayloadPlanner.Decide(prev, next, MappedFrom(prev)));

        Assert.True(refuse.RestartRequired);
        Assert.Equal(next.PreloadManaged[0].Path, refuse.File);
    }

    [Fact]
    public void A_companion_mapped_by_someone_else_needs_a_restart_because_its_bytes_are_unknown()
    {
        var next = Payload("1");
        var images = Images(native: new[] { @"X:\Appload\NorsynLogging.dll" });

        var refuse = Assert.IsType<PayloadDecision.Refuse>(PayloadPlanner.Decide(null, next, images));

        Assert.True(refuse.RestartRequired);
        Assert.Contains(@"X:\Appload\NorsynLogging.dll", refuse.Reason);
    }

    [Fact]
    public void A_companion_already_mapped_from_the_exact_payload_path_is_kept()
    {
        var next = Payload("1");
        var images = Images(native: new[] { next.PreloadNative[0].Path });

        var plan = Assert.IsType<PayloadDecision.Proceed>(PayloadPlanner.Decide(null, next, images));

        Assert.Empty(plan.PinNative);
    }

    [Fact]
    public void A_module_file_name_loaded_from_another_path_is_a_clash_not_a_restart()
    {
        var next = Payload("1");
        var images = Images(native: new[] { @"C:\elsewhere\A.dbx" });

        var refuse = Assert.IsType<PayloadDecision.Refuse>(PayloadPlanner.Decide(null, next, images));

        Assert.False(refuse.RestartRequired);
        Assert.Contains(@"C:\elsewhere\A.dbx", refuse.Reason);
        Assert.Contains(next.Modules[0].Path, refuse.Reason);
    }

    [Fact]
    public void A_previous_module_that_is_no_longer_mapped_is_not_unloaded()
    {
        var prev = Payload("1");
        var next = Payload("2", dbxSha: "d2");
        var images = Images(native: new[] { prev.PreloadNative[0].Path, prev.Modules[0].Path },
                            managed: new[] { prev.PreloadManaged[0].Path });

        var plan = Assert.IsType<PayloadDecision.Proceed>(PayloadPlanner.Decide(prev, next, images));

        Assert.Equal(new[] { "A.dbx" }, plan.UnloadModules);
    }

    [Fact]
    public void File_names_match_case_insensitively()
    {
        var next = Payload("1");
        var images = Images(native: new[] { @"C:\elsewhere\a.DBX" });

        Assert.IsType<PayloadDecision.Refuse>(PayloadPlanner.Decide(null, next, images));
    }
}
