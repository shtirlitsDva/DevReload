using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DevReload.Core;

namespace DevReload.ViewModels
{
    // A branch/worktree whose build dir already holds a SharedAssemblies.Config.json
    // the user can copy from. BuildDir is that build directory (the file lives in it).
    // ToString returns Label so the ComboBox shows the branch name (the custom
    // ComboBox template doesn't honour DisplayMemberPath for the selection box).
    public sealed record SharedConfigSource(string Label, string BuildDir)
    {
        public override string ToString() => Label;
    }

    public partial class SharedAssembliesViewModel : ObservableObject
    {
        public ObservableCollection<AssemblyItem> Assemblies { get; } = new();
        public ObservableCollection<SharedConfigSource> Sources { get; } = new();

        [ObservableProperty] private bool _saved;
        [ObservableProperty] private SharedConfigSource? _selectedSource;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasCopyStatus))]
        private string _copyStatus = "";

        public bool HasSources => Sources.Count > 0;
        public bool HasCopyStatus => !string.IsNullOrEmpty(CopyStatus);

        public SharedAssembliesViewModel(
            string pluginDir,
            IReadOnlyList<string> currentShared,
            IReadOnlyList<string> currentMixedMode,
            IReadOnlyList<string> currentStreamed,
            IReadOnlyList<CsprojReferenceScanner.ExternalReference> externalRefs,
            IReadOnlyList<SharedConfigSource> sources)
        {
            var currentSet = new HashSet<string>(
                currentShared, StringComparer.OrdinalIgnoreCase);
            var mixedSet = new HashSet<string>(
                currentMixedMode, StringComparer.OrdinalIgnoreCase);
            var streamedSet = new HashSet<string>(
                currentStreamed, StringComparer.OrdinalIgnoreCase);
            var externalNames = new HashSet<string>(
                externalRefs.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);

            // Local DLLs in the build dir. An external HintPath ref of the same name
            // WINS (skip the local copy) — the point is to load from the referenced
            // location, not a private build-dir copy.
            if (Directory.Exists(pluginDir))
            {
                foreach (string dll in Directory.GetFiles(pluginDir, "*.dll"))
                {
                    string name = Path.GetFileNameWithoutExtension(dll);
                    if (externalNames.Contains(name)) continue;
                    Assemblies.Add(new AssemblyItem
                    {
                        Name = name,
                        IsSelected = currentSet.Contains(name),
                        IsMixedMode = mixedSet.Contains(name),
                        IsStreamed = streamedSet.Contains(name),
                    });
                }
            }

            // External (csproj HintPath) candidates — loadable from their referenced dir.
            foreach (var ext in externalRefs)
            {
                Assemblies.Add(new AssemblyItem
                {
                    Name = ext.Name,
                    ExternalDir = ext.Directory,
                    IsSelected = currentSet.Contains(ext.Name),
                    IsMixedMode = mixedSet.Contains(ext.Name),
                    IsStreamed = streamedSet.Contains(ext.Name),
                });
            }

            foreach (var src in sources)
                Sources.Add(src);

            SelectedSource = Sources.FirstOrDefault();
        }

        public string[] GetSelectedNames()
            => Assemblies.Where(a => a.IsSelected)
                         .Select(a => a.Name)
                         .ToArray();

        public string[] GetMixedModeNames()
            => Assemblies.Where(a => a.IsSelected && a.IsMixedMode)
                         .Select(a => a.Name)
                         .ToArray();

        public string[] GetStreamedNames()
            => Assemblies.Where(a => a.IsSelected && a.IsStreamed)
                         .Select(a => a.Name)
                         .ToArray();

        // Selected external assemblies mapped to their referenced dir — becomes the
        // config's assemblyLocations. Only ticked external candidates contribute.
        public IReadOnlyDictionary<string, string> GetAssemblyLocations()
            => Assemblies.Where(a => a.IsSelected && a.IsExternal)
                         .ToDictionary(a => a.Name, a => a.ExternalDir!,
                                       StringComparer.OrdinalIgnoreCase);

        // Copy the chosen branch's shared-assembly selection onto this build's
        // assembly list. Only DLLs that actually exist in THIS build dir are
        // applied — a worktree may not contain every DLL the source branch did.
        // Missing ones are reported, not silently dropped.
        [RelayCommand]
        private void CopyFrom()
        {
            if (SelectedSource == null) return;

            var cfg = SharedAssembliesFile.Read(SelectedSource.BuildDir);

            var byName = Assemblies.ToDictionary(
                a => a.Name, StringComparer.OrdinalIgnoreCase);
            var mixedSet = new HashSet<string>(
                cfg.MixedModeAssemblies, StringComparer.OrdinalIgnoreCase);
            var streamedSet = new HashSet<string>(
                cfg.StreamedAssemblies, StringComparer.OrdinalIgnoreCase);

            // Reset so the copy fully replaces the current selection.
            foreach (var item in Assemblies)
            {
                item.IsSelected = false;
                item.IsMixedMode = false;
                item.IsStreamed = false;
            }

            var missing = new List<string>();
            foreach (string name in cfg.SharedAssemblies)
            {
                if (!byName.TryGetValue(name, out var item))
                {
                    missing.Add(name);
                    continue;
                }

                item.IsSelected = true;
                if (mixedSet.Contains(name)) item.IsMixedMode = true;
                else if (streamedSet.Contains(name)) item.IsStreamed = true;
            }

            int applied = cfg.SharedAssemblies.Count - missing.Count;
            CopyStatus = missing.Count == 0
                ? $"Copied {applied} assembl{(applied == 1 ? "y" : "ies")} from {SelectedSource.Label}."
                : $"Copied {applied} from {SelectedSource.Label}. " +
                  $"Not present in this build (skipped): {string.Join(", ", missing)}.";
        }

        [RelayCommand]
        private void Save()
        {
            Saved = true;
        }
    }

    public partial class AssemblyItem : ObservableObject
    {
        [ObservableProperty] private string _name = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsExternal))]
        private string? _externalDir;

        // Loaded from a csproj-referenced dir (HintPath), not the plugin build dir.
        public bool IsExternal => !string.IsNullOrEmpty(ExternalDir);

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanEnableMixedMode))]
        [NotifyPropertyChangedFor(nameof(CanEnableStreamed))]
        private bool _isSelected;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanEnableStreamed))]
        private bool _isMixedMode;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanEnableMixedMode))]
        private bool _isStreamed;

        // Mixed-mode and streamed are mutually exclusive: mixed-mode (C++/CLI)
        // relies on directory-adjacent native deps (Ijwhost.dll, satellites),
        // which the streamed/"location-unknown" load path cannot probe.
        public bool CanEnableMixedMode => IsSelected && !IsStreamed;
        public bool CanEnableStreamed => IsSelected && !IsMixedMode;

        partial void OnIsMixedModeChanged(bool value)
        {
            if (value) IsStreamed = false;
        }

        partial void OnIsStreamedChanged(bool value)
        {
            if (value) IsMixedMode = false;
        }
    }
}
