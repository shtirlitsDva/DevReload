using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DevReload.ViewModels
{
    public partial class SharedAssembliesViewModel : ObservableObject
    {
        public ObservableCollection<AssemblyItem> Assemblies { get; } = new();

        [ObservableProperty] private bool _saved;

        public SharedAssembliesViewModel(
            string pluginDir,
            IReadOnlyList<string> currentShared,
            IReadOnlyList<string> currentMixedMode,
            IReadOnlyList<string> currentStreamed)
        {
            if (!Directory.Exists(pluginDir)) return;

            var currentSet = new HashSet<string>(
                currentShared, StringComparer.OrdinalIgnoreCase);
            var mixedSet = new HashSet<string>(
                currentMixedMode, StringComparer.OrdinalIgnoreCase);
            var streamedSet = new HashSet<string>(
                currentStreamed, StringComparer.OrdinalIgnoreCase);

            foreach (string dll in Directory.GetFiles(pluginDir, "*.dll"))
            {
                string name = Path.GetFileNameWithoutExtension(dll);
                Assemblies.Add(new AssemblyItem
                {
                    Name = name,
                    IsSelected = currentSet.Contains(name),
                    IsMixedMode = mixedSet.Contains(name),
                    IsStreamed = streamedSet.Contains(name),
                });
            }
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
