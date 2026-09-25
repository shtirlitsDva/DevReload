using System;
using System.Collections.ObjectModel;
using System.Linq;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DevReload.Oarx;
using DevReload.Views;

namespace DevReload.ViewModels
{
    /// <summary>
    /// The OARX half of the palette's view-model: the second tab's card list.
    /// </summary>
    /// <remarks>
    /// Split into its own file rather than mixed into the .NET half. The two
    /// tabs project two independent registries (<c>PluginManager</c> and
    /// <c>OarxManager</c>) and share only the window they live in.
    /// <para>Adding and editing a group happens in <see cref="OarxGroupWindow"/>,
    /// not in the palette: a group's profiles need more room than a palette
    /// has, and one editor means one set of rules for what a valid group is.</para>
    /// </remarks>
    public partial class DevReloadViewModel
    {
        public ObservableCollection<OarxPluginItemViewModel> OarxPlugins { get; } = new();

        [ObservableProperty] private bool _hasOarxPlugins;

        // ── Registry projection ───────────────────────────────────────

        private void InitializeOarx()
        {
            OarxManager.Registered += OnOarxRegistered;
            OarxManager.Unregistered += OnOarxUnregistered;
            OarxManager.StateChanged += OnOarxStateChanged;
        }

        private void OnOarxStateChanged(string name)
        {
            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.Invoke(() => OnOarxStateChanged(name));
                return;
            }

            OarxPlugins.FirstOrDefault(
                    p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?.RefreshState();
        }

        private void OnOarxRegistered(string name)
        {
            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.Invoke(() => OnOarxRegistered(name));
                return;
            }

            if (OarxPlugins.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return;

            var entry = OarxManager.GetEntry(name);
            if (entry == null) return;

            if (!_config.OarxPlugins.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                _config.OarxPlugins.Add(entry);

            AddOarxCard(entry);
            HasOarxPlugins = true;
        }

        private void OnOarxUnregistered(string name)
        {
            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.Invoke(() => OnOarxUnregistered(name));
                return;
            }

            _config.OarxPlugins.RemoveAll(
                p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            var vm = OarxPlugins.FirstOrDefault(
                p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (vm != null) OarxPlugins.Remove(vm);
            HasOarxPlugins = OarxPlugins.Count > 0;
        }

        private void LoadOarxFromConfig()
        {
            OarxPlugins.Clear();
            foreach (var entry in _config.OarxPlugins)
                AddOarxCard(entry);
            HasOarxPlugins = OarxPlugins.Count > 0;
        }

        private void AddOarxCard(OarxPluginEntry entry)
        {
            var vm = new OarxPluginItemViewModel(entry);
            vm.RefreshState();
            OarxPlugins.Add(vm);
        }

        // ── Add / Edit / Remove ──────────────────────────────────────

        [RelayCommand]
        private void ShowAddOarx()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select the solution the ObjectARX modules build under",
                Filter = "Visual Studio solution (*.sln)|*.sln",
                CheckFileExists = true,
            };
            if (dialog.ShowDialog() != true) return;

            ShowOarxWindow(OarxGroupEditorViewModel.ForNewGroup(dialog.FileName));
        }

        [RelayCommand]
        private void ShowEditOarx(string name)
        {
            var vm = OarxGroupEditorViewModel.ForGroup(name);
            if (vm == null) return;
            ShowOarxWindow(vm);
        }

        private static void ShowOarxWindow(OarxGroupEditorViewModel vm)
        {
            var win = new OarxGroupWindow(vm);
            win.ShowDialog();
        }

        [RelayCommand]
        private void RemoveOarxPlugin(string name)
        {
            // Tears down in memory (unloading any mapped modules first) AND drops
            // the plugins.json entry, raising Unregistered.
            OarxConfigLoader.Unregister(name);
        }

        // ── Reload Config ─────────────────────────────────────────────

        /// <summary>Resync the OARX registry to plugins.json. Called from
        /// <c>ReloadConfig</c> after <c>_config</c> has been re-read.</summary>
        private void ReloadOarxConfig(PluginConfig fresh)
        {
            var onDisk = new System.Collections.Generic.HashSet<string>(
                fresh.OarxPlugins.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

            // In-memory only — the public Unregister would also delete the entry
            // from the file we are syncing FROM.
            foreach (var name in OarxManager.GetRegisteredNames().ToList())
                if (!onDisk.Contains(name))
                    OarxManager.UnregisterInMemory(name);

            foreach (var entry in fresh.OarxPlugins)
            {
                if (!OarxManager.IsRegistered(entry.Name))
                {
                    OarxConfigLoader.RegisterFromConfig(entry);
                    continue;
                }
                // Same name, changed body: a hand-edited entry. Comparing by
                // name alone is how such edits used to be silently ignored
                // until restart.
                if (OarxManager.MatchesSource(entry.Name, entry))
                    continue;
                if (OarxManager.IsLoaded(entry.Name))
                {
                    // Never yank a loaded group's registration out from under
                    // its mapped modules — stage it; the next load/reload
                    // applies it, and the card says so until then.
                    OarxManager.StagePendingEntry(entry.Name, entry);
                }
                else
                {
                    OarxManager.UnregisterInMemory(entry.Name);
                    OarxConfigLoader.RegisterFromConfig(entry);
                }
            }
        }
    }
}
