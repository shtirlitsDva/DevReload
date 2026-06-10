using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

using RevitDevReload.Core;

using Xunit;

namespace RevitDevReload.Tests
{
    // Builds the fixture plugin (with its dependency) once for the whole test
    // class set, into a scratch copy so tests can overwrite/delete files.
    public sealed class FixtureBuild : IDisposable
    {
        public string BuildDir { get; }
        public string PluginDll { get; }

        public FixtureBuild()
        {
            string repoRoot = FindRepoRoot();
            string fixtureProj = Path.Combine(
                repoRoot, "tests", "fixtures", "FixturePlugin", "FixturePlugin.csproj");

#if NET48
            const string tfm = "net48";
#else
            const string tfm = "net8.0";
#endif
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build \"{fixtureProj}\" -c Debug -f {tfm} --nologo -v q",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using (var proc = Process.Start(psi)!)
            {
                string log = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                if (proc.ExitCode != 0)
                    throw new InvalidOperationException("fixture build failed:\n" + log);
            }

            string builtDir = Path.Combine(
                Path.GetDirectoryName(fixtureProj)!, "bin", "Debug", tfm);

            // Copy to scratch so tests can overwrite the DLL without fighting
            // other test runs.
            BuildDir = Path.Combine(Path.GetTempPath(),
                "rdr-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(BuildDir);
            foreach (var file in Directory.GetFiles(builtDir))
                File.Copy(file, Path.Combine(BuildDir, Path.GetFileName(file)));

            PluginDll = Path.Combine(BuildDir, "FixturePlugin.dll");
            Assert.True(File.Exists(PluginDll), "fixture DLL missing: " + PluginDll);
        }

        public void Dispose()
        {
            try { Directory.Delete(BuildDir, true); } catch { }
        }

        private static string FindRepoRoot()
        {
            string? cursor = AppContext.BaseDirectory;
            while (cursor != null &&
                   !File.Exists(Path.Combine(cursor, "DevReload.sln")))
                cursor = Path.GetDirectoryName(cursor);
            return cursor ?? throw new InvalidOperationException(
                "DevReload.sln not found above " + AppContext.BaseDirectory);
        }
    }

    public class PluginLoaderTests : IClassFixture<FixtureBuild>
    {
        private readonly FixtureBuild _fixture;

        public PluginLoaderTests(FixtureBuild fixture) => _fixture = fixture;

        private static string InvokeEntry(System.Reflection.Assembly asm)
        {
            var type = asm.GetType("FixturePlugin.Entry")!;
            var method = type.GetMethod("GetDepMessage")!;
            return (string)method.Invoke(null, null)!;
        }

#if NET48
        [Fact]
        public void LegacyLoader_LoadsPlugin_AndResolvesDependencyFromBuildDir()
        {
            var loader = new LegacyPluginLoader();
            var handle = loader.Load(_fixture.PluginDll, Array.Empty<string>());
            try
            {
                Assert.Equal("hello-from-dep", InvokeEntry(handle.Assembly));
            }
            finally
            {
                loader.Unload(handle);
            }
        }

        [Fact]
        public void LegacyLoader_DoesNotLockTheDllFile()
        {
            var loader = new LegacyPluginLoader();
            var handle = loader.Load(_fixture.PluginDll, Array.Empty<string>());
            try
            {
                // Overwrite-in-place must succeed while loaded.
                byte[] bytes = File.ReadAllBytes(_fixture.PluginDll);
                File.WriteAllBytes(_fixture.PluginDll, bytes);
            }
            finally
            {
                loader.Unload(handle);
            }
        }

        [Fact]
        public void LegacyLoader_ReloadGivesFreshAssemblyInstance()
        {
            var loader = new LegacyPluginLoader();
            var first = loader.Load(_fixture.PluginDll, Array.Empty<string>());
            var second = loader.Load(_fixture.PluginDll, Array.Empty<string>());
            try
            {
                Assert.NotSame(first.Assembly, second.Assembly);
                Assert.Equal("hello-from-dep", InvokeEntry(second.Assembly));
            }
            finally
            {
                loader.Unload(first);
                loader.Unload(second);
            }
        }
#else
        [Fact]
        public void AlcLoader_LoadsPlugin_AndResolvesDependencyFromBuildDir()
        {
            var loader = new AlcPluginLoader();
            var handle = loader.Load(_fixture.PluginDll, Array.Empty<string>());
            try
            {
                Assert.Equal("hello-from-dep", InvokeEntry(handle.Assembly));
            }
            finally
            {
                loader.Unload(handle);
            }
        }

        [Fact]
        public void AlcLoader_DoesNotLockTheDllFile()
        {
            var loader = new AlcPluginLoader();
            var handle = loader.Load(_fixture.PluginDll, Array.Empty<string>());
            try
            {
                byte[] bytes = File.ReadAllBytes(_fixture.PluginDll);
                File.WriteAllBytes(_fixture.PluginDll, bytes);
            }
            finally
            {
                loader.Unload(handle);
            }
        }

        [Fact]
        public void AlcLoader_Unload_CollectsTheContext()
        {
            var loader = new AlcPluginLoader();
            WeakReference contextRef = LoadAndUnload(loader);

            for (int i = 0; contextRef.IsAlive && i < 10; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.False(contextRef.IsAlive,
                "collectible ALC still alive after unload + GC");
        }

        // Separate non-inlined method so no local in the test frame roots
        // the context/assembly during the GC loop above.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private WeakReference LoadAndUnload(AlcPluginLoader loader)
        {
            var handle = loader.Load(_fixture.PluginDll, Array.Empty<string>());
            Assert.Equal("hello-from-dep", InvokeEntry(handle.Assembly));
            var weak = new WeakReference(handle.Context);
            loader.Unload(handle);
            return weak;
        }
#endif
    }
}
