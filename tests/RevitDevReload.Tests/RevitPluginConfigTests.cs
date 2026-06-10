using System;
using System.IO;

using RevitDevReload.Core;

using Xunit;

namespace RevitDevReload.Tests
{
    public class RevitPluginConfigTests : IDisposable
    {
        private readonly string _dir;

        public RevitPluginConfigTests()
        {
            _dir = Path.Combine(Path.GetTempPath(),
                "rdr-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public void ConfigPath_IsPerRevitVersion()
        {
            string p22 = RevitPluginConfigLoader.GetConfigPath(2022, _dir);
            string p25 = RevitPluginConfigLoader.GetConfigPath(2025, _dir);

            Assert.NotEqual(p22, p25);
            Assert.EndsWith("plugins.R2022.json", p22);
            Assert.EndsWith("plugins.R2025.json", p25);
        }

        [Fact]
        public void Load_MissingFile_ReturnsNull()
        {
            Assert.Null(RevitPluginConfigLoader.Load(2024, _dir));
        }

        [Fact]
        public void SaveThenLoad_RoundTrips()
        {
            var config = new RevitPluginConfig();
            config.Plugins.Add(new RevitPluginEntry
            {
                Name = "MyAddin",
                ProjectFilePath = @"X:\repo\src\MyAddin\MyAddin-2024.csproj",
                DllPath = @"X:\repo\src\MyAddin\bin\Debug\MyAddin.dll",
                BuildConfiguration = "Debug",
                ActiveWorktreePath = @"X:\wt\feature-x",
                LoadOnStartup = true,
            });

            RevitPluginConfigLoader.Save(2024, config, _dir);
            var loaded = RevitPluginConfigLoader.Load(2024, _dir);

            Assert.NotNull(loaded);
            var entry = Assert.Single(loaded!.Plugins);
            Assert.Equal("MyAddin", entry.Name);
            Assert.Equal(@"X:\repo\src\MyAddin\MyAddin-2024.csproj", entry.ProjectFilePath);
            Assert.Equal(@"X:\repo\src\MyAddin\bin\Debug\MyAddin.dll", entry.DllPath);
            Assert.Equal("Debug", entry.BuildConfiguration);
            Assert.Equal(@"X:\wt\feature-x", entry.ActiveWorktreePath);
            Assert.True(entry.LoadOnStartup);
        }

        [Fact]
        public void Save_DoesNotCrossContaminateVersions()
        {
            var c22 = new RevitPluginConfig();
            c22.Plugins.Add(new RevitPluginEntry { Name = "Only22" });
            RevitPluginConfigLoader.Save(2022, c22, _dir);

            Assert.Null(RevitPluginConfigLoader.Load(2025, _dir));
            Assert.NotNull(RevitPluginConfigLoader.Load(2022, _dir));
        }
    }
}
