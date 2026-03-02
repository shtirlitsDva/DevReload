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

        public SharedAssembliesViewModel(string pluginDir, IReadOnlyList<string> currentShared)
        {
            if (!Directory.Exists(pluginDir)) return;

            var currentSet = new HashSet<string>(
                currentShared, StringComparer.OrdinalIgnoreCase);

            foreach (string dll in Directory.GetFiles(pluginDir, "*.dll"))
            {
                string name = Path.GetFileNameWithoutExtension(dll);
                Assemblies.Add(new AssemblyItem
                {
                    Name = name,
                    IsSelected = currentSet.Contains(name),
                });
            }
        }

        public string[] GetSelectedNames()
            => Assemblies.Where(a => a.IsSelected)
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
        [ObservableProperty] private bool _isSelected;
    }
}
