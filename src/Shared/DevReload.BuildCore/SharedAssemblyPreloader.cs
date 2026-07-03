// Pre-loads the shared assemblies named in a SharedAssemblies.Config.json into
// the default (non-collectible) AssemblyLoadContext BEFORE a collectible plugin
// loads. Once a shared assembly is in the default ALC, name-based binding from
// the isolated plugin ALC (see IsolatedPluginContext.Load) resolves to that one
// instance — required for cross-ALC type identity (WPF XAML types, a REPL
// session's globals type, mixed-mode C++/CLI interop that cannot live in two
// contexts at once).
//
// Extracted verbatim from the AutoCAD PluginManager so the AutoCAD and Revit
// hosts share ONE copy of this subtle, hard-won logic (the same-name/different-
// path collision guard alone was paid for once already).
//
// net8-only: the whole concept is ALC-based. net48 hosts (the legacy Revit
// loader) compile this file to nothing and never call it — one AppDomain has no
// cross-ALC identity problem to solve, so there is nothing to pre-load.
#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace DevReload.Core
{
    public static class SharedAssemblyPreloader
    {
        /// <summary>
        /// Loads every configured shared assembly found in <paramref name="buildDir"/>
        /// into the default ALC, honouring mixed-mode (needs a generated
        /// runtimeconfig.json) and streamed (LoadFromStream, no file lock)
        /// categories. Idempotent: an assembly already present in the default
        /// ALC is skipped. <paramref name="log"/> receives progress/warning
        /// text (the AutoCAD host routes it to the Editor; Revit to its log buffer).
        /// </summary>
        public static void Preload(
            string buildDir,
            SharedAssembliesFile.Config config,
            Action<string>? log = null)
        {
            if (config.SharedAssemblies.Count == 0) return;

            var mixedSet = new HashSet<string>(
                config.MixedModeAssemblies, StringComparer.OrdinalIgnoreCase);
            var streamedSet = new HashSet<string>(
                config.StreamedAssemblies, StringComparer.OrdinalIgnoreCase);

            foreach (string asmName in config.SharedAssemblies)
            {
                string asmPath = Path.Combine(buildDir, asmName + ".dll");
                if (!File.Exists(asmPath)) continue;

                // If a shared assembly is already in the default ALC, reuse it —
                // never attempt a second load. One rule, two cases:
                //   • An external loader put it there: e.g. a Civil 3D object-
                //     enabler demand-load brings a mixed-mode interop into the
                //     default ALC at startup, from its own install path. We MUST
                //     bind to that exact instance for cross-ALC type identity
                //     (mandatory for C++/CLI, which cannot live in two contexts),
                //     and a second LoadFrom of the build-output copy would throw
                //     "Assembly with same name is already loaded" — a different
                //     path under a name the ALC already holds.
                //   • DevReload loaded it on a previous reload cycle: the default
                //     ALC is non-collectible, so the image is permanent.
                if (IsLoadedInDefaultAlc(asmName)) continue;

                if (mixedSet.Contains(asmName))
                {
                    EnsureRuntimeConfig(asmPath, asmName, log);
                    Assembly.LoadFrom(asmPath);
                }
                else if (streamedSet.Contains(asmName))
                {
                    LoadSharedFromStream(asmPath);
                }
                else
                {
                    Assembly.LoadFrom(asmPath);
                }
            }
        }

        /// <summary>
        /// True when an assembly with this simple name is already present in the
        /// default ALC — whether DevReload loaded it on a previous reload cycle,
        /// or an external loader (e.g. a Civil 3D object-enabler demand-load)
        /// brought it in at startup. An ALC holds at most one assembly per simple
        /// name; once present, name-based binding from the collectible plugin ALC
        /// already resolves to it, so any further load is at best a no-op and at
        /// worst — for a different on-disk path — a hard error.
        /// </summary>
        public static bool IsLoadedInDefaultAlc(string simpleName)
        {
            foreach (var asm in AssemblyLoadContext.Default.Assemblies)
            {
                if (string.Equals(
                        asm.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // Stream-loads a shared assembly INTO the default ALC.
        //
        // We must call AssemblyLoadContext.Default.LoadFromStream(...) explicitly.
        // Assembly.Load(byte[]) looks like the "right" API but it loads into a
        // brand-new anonymous AssemblyLoadContext (one per call) per the
        // documented .NET algorithm — the assembly never ends up in
        // AssemblyLoadContext.Default and is invisible to name-based binding
        // from the isolated plugin ALC.
        //
        // Default.LoadFromStream behaves like LoadFrom for binding purposes
        // (assembly is in Default.Assemblies, findable by name) but without the
        // file lock on the DLL on disk. The default ALC is non-collectible, so
        // the streamed image lives until the host exits.
        //
        // The caller guarantees this runs only when the simple name is NOT
        // already in the default ALC (IsLoadedInDefaultAlc), so there is no
        // idempotency check here — a re-load would throw.
        private static void LoadSharedFromStream(string asmPath)
        {
            byte[] asmBytes = File.ReadAllBytes(asmPath);
            string pdbPath = Path.ChangeExtension(asmPath, ".pdb");
            using var asmStream = new MemoryStream(asmBytes);
            if (File.Exists(pdbPath))
            {
                byte[] pdbBytes = File.ReadAllBytes(pdbPath);
                using var pdbStream = new MemoryStream(pdbBytes);
                AssemblyLoadContext.Default.LoadFromStream(asmStream, pdbStream);
            }
            else
            {
                AssemblyLoadContext.Default.LoadFromStream(asmStream);
            }
        }

        private static void EnsureRuntimeConfig(string asmPath, string asmName, Action<string>? log)
        {
            string asmDir = Path.GetDirectoryName(asmPath)!;
            string rcPath = Path.Combine(asmDir, asmName + ".runtimeconfig.json");
            if (!File.Exists(rcPath))
            {
                log?.Invoke($"Creating runtimeconfig.json for mixed-mode: {asmName}");
                File.WriteAllText(rcPath,
                    """
                    {
                      "runtimeOptions": {
                        "tfm": "net8.0",
                        "framework": {
                          "name": "Microsoft.NETCore.App",
                          "version": "8.0.0"
                        }
                      }
                    }
                    """);
            }

            string ijwPath = Path.Combine(asmDir, "Ijwhost.dll");
            if (!File.Exists(ijwPath))
                log?.Invoke($"WARNING: Ijwhost.dll not found in {asmDir}");
        }
    }
}
#endif
