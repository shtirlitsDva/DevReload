using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

using DevReload.Core;

using Xunit;

namespace RevitDevReload.Tests
{
    // BuildService.BuildProjects: the OARX group build, all modules in ONE
    // msbuild -m run through a generated traversal project.
    public class GroupBuildTests
    {
        [Fact]
        public void Traversal_lists_every_module_by_full_path_and_builds_them_in_parallel()
        {
            string a = Path.Combine(Path.GetTempPath(), "g", "A.vcxproj");
            string b = Path.Combine(Path.GetTempPath(), "g", "B.vcxproj");

            var doc = XDocument.Parse(BuildService.TraversalProjectXml(new[] { a, b }));

            var modules = doc.Descendants("DevReloadModule")
                             .Select(e => (string)e.Attribute("Include")!).ToList();
            Assert.Equal(new[] { a, b }, modules);

            var call = Assert.Single(doc.Descendants("MSBuild"));
            Assert.Equal("@(DevReloadModule)", (string)call.Attribute("Projects")!);
            Assert.Equal("true", (string)call.Attribute("BuildInParallel")!);
        }

        [Fact]
        public void Traversal_imports_nothing_so_no_directory_props_leak_into_it()
        {
            var doc = XDocument.Parse(BuildService.TraversalProjectXml(new[] { @"C:\x\A.vcxproj" }));
            Assert.Null(doc.Root!.Attribute("Sdk"));
            Assert.Empty(doc.Descendants("Import"));
        }

        [Fact]
        public void Traversal_escapes_xml_special_characters_in_paths()
        {
            string odd = @"C:\R&D\it's <here>\A.vcxproj";
            var doc = XDocument.Parse(BuildService.TraversalProjectXml(new[] { odd }));
            Assert.Equal(odd, (string)doc.Descendants("DevReloadModule").Single().Attribute("Include")!);
        }

        [Fact]
        public void Errors_are_attributed_to_the_project_msbuild_names_including_referenced_libs()
        {
            const string log =
                "  Types.h(12,1): error C2143: syntax error: missing ';' [C:\\r\\src\\Core\\Core.vcxproj]\r\n" +
                "  Types.h(14,1): error C4430: missing type specifier [C:\\r\\src\\Core\\Core.vcxproj]\r\n" +
                "  Pipe.cpp(3,1): warning C4100: unreferenced parameter [C:\\r\\src\\Dbx\\Dbx.vcxproj]\r\n" +
                "LINK : fatal error LNK1104: cannot open file 'x.dbx' [C:\\r\\src\\Dbx\\Dbx.vcxproj]\r\n" +
                "  Done building.\r\n";

            Assert.Equal(new[] { "Core.vcxproj", "Dbx.vcxproj" }, BuildService.ProjectsWithErrors(log));
        }

        [Fact]
        public void An_error_line_without_a_project_suffix_names_no_project()
        {
            Assert.Empty(BuildService.ProjectsWithErrors("MSBUILD : error MSB1009: Project file does not exist."));
        }

        [Fact]
        public void An_empty_group_is_refused()
        {
            Assert.Throws<ArgumentException>(() =>
                BuildService.BuildProjects(Array.Empty<string>(), "Debug", "x64", null));
        }

        // End to end against real native projects, off by default: set
        // DEVRELOAD_GROUP_E2E to "<solutionDir>|<proj1>;<proj2>" to run it. Builds
        // for real, so it belongs to a manual verification, not the CI loop.
        [Fact]
        public void Real_group_builds_in_one_run_when_configured()
        {
            string? spec = Environment.GetEnvironmentVariable("DEVRELOAD_GROUP_E2E");
            if (string.IsNullOrEmpty(spec)) return;

            string[] parts = spec!.Split('|');
            string[] projects = parts[1].Split(';');
            string? props = Environment.GetEnvironmentVariable("DEVRELOAD_GROUP_E2E_PROPS");

            var result = BuildService.BuildProjects(projects, "Debug", "x64", Console.WriteLine,
                parts[0], null, props?.Split(';'));

            Assert.True(result.Success, result.Log);
            Assert.Equal(projects.Length, result.OutputPaths.Count);
            Assert.All(result.OutputPaths, p => Assert.True(File.Exists(p), p));
        }
    }
}
