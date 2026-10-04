// The one way DevReload loads an assembly file without locking it: its bytes
// (and its .pdb's) into a load context. A stream-loaded assembly has no
// Location, so the loader also publishes the file the bytes came from, where a
// plugin can read it without referencing DevReload:
//
//     AppContext.GetData("DevReload.AssemblyFile:" + assembly.GetName().Name) as string
//
// A plugin that hands its own file to another process (a plotting console that
// NETLOADs it, a tool started beside it) reads Location first and this second.
// Under NETLOAD or a bundle Location is the file and nothing is published.
//
// net8-only, like the contexts it serves.
#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace DevReload.Core
{
    public static class StreamedAssembly
    {
        /// <summary>The AppContext key prefix; the simple assembly name follows it.</summary>
        public const string FileKeyPrefix = "DevReload.AssemblyFile:";

        /// <summary>
        /// Loads <paramref name="assemblyPath"/> (and the .pdb beside it, when
        /// there is one) into <paramref name="context"/> from memory, and
        /// publishes the path under the assembly's simple name. A reload
        /// publishes again, over the earlier path.
        /// </summary>
        public static Assembly Load(AssemblyLoadContext context, string assemblyPath)
        {
            byte[] asmBytes = File.ReadAllBytes(assemblyPath);
            string pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
            Assembly assembly;
            using (var asmStream = new MemoryStream(asmBytes))
            {
                if (File.Exists(pdbPath))
                {
                    using var pdbStream = new MemoryStream(File.ReadAllBytes(pdbPath));
                    assembly = context.LoadFromStream(asmStream, pdbStream);
                }
                else
                {
                    assembly = context.LoadFromStream(asmStream);
                }
            }

            string? name = assembly.GetName().Name;
            if (name != null)
                AppContext.SetData(FileKeyPrefix + name, Path.GetFullPath(assemblyPath));
            return assembly;
        }
    }
}
#endif
