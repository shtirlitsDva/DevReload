using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DevReload.Oarx;

namespace DevReload.ViewModels
{
    /// <summary>
    /// The OARX half of the palette's view-model: the second tab's card list and
    /// its "add group" form.
    /// </summary>
    /// <remarks>
    /// Split into its own file rather than mixed into the .NET half. The two
    /// tabs project two independent registries (<c>PluginManager</c> and
    /// <c>OarxManager</c>) and share only the window they live in.
    /// </remarks>
    public partial class DevReloadViewModel
    {
        public ObservableCollection<OarxPluginItemViewModel> OarxPlugins { get; } = new();

        [ObservableProperty] private bool _hasOarxPlugins;
        [ObservableProperty] private bool _isAddingOarx;

        // ── Add OARX form fields ──────────────────────────────────────

        [ObservableProperty] private string _newOarxSolution = "";
        [ObservableProperty] private string _newOarxName = "";
        [ObservableProperty] private string _newOarxPrefix = "";
        [ObservableProperty] private bool _newOarxLoadOnStartup;

        /// <summary>Module projects in LOAD order — the .dbx that owns the custom
        /// classes before the .arx that uses them. The order is the reason this
        /// form exists at all, so it is editable in place.</summary>
        public ObservableCollection<OarxModuleDraft> NewOarxModules { get; } = new();

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

            var entry = (PluginConfigLoader.Load()?.OarxPlugins ?? new())
                .FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
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
            if (vm != null)
            {
                vm.PropertyChanged -= OnOarxPropertyChanged;
                OarxPlugins.Remove(vm);
            }
            HasOarxPlugins = OarxPlugins.Count > 0;
        }

        private void LoadOarxFromConfig()
        {
            foreach (var item in OarxPlugins)
                item.PropertyChanged -= OnOarxPropertyChanged;
            OarxPlugins.Clear();

            foreach (var entry in _config.OarxPlugins)
                AddOarxCard(entry);

            HasOarxPlugins = OarxPlugins.Count > 0;
        }

        private void AddOarxCard(OarxPluginEntry entry)
        {
            var vm = new OarxPluginItemViewModel(entry);
            vm.PropertyChanged += OnOarxPropertyChanged;
            vm.RefreshState();
            OarxPlugins.Add(vm);
        }

        private void OnOarxPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Same split as the .NET cards: configuration and worktree persist
            // through OarxManager, so only the pure file-state flag is saved here.
            if (e.PropertyName == nameof(OarxPluginItemViewModel.LoadOnStartup))
                SaveConfig();
        }

        // ── Add / Remove ─────────────────────────────────────────────

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

            NewOarxSolution = dialog.FileName;
            NewOarxModules.Clear();
            NewOarxName = "";
            NewOarxPrefix = "";
            NewOarxLoadOnStartup = false;
            IsAddingOarx = true;
        }

        [RelayCommand]
        private void AddOarxProject()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select the ObjectARX module project(s) — .dbx before .arx",
                Filter = "C++ project (*.vcxproj)|*.vcxproj",
                CheckFileExists = true,
                Multiselect = true,
                InitialDirectory = Path.GetDirectoryName(NewOarxSolution) ?? "",
            };
            if (dialog.ShowDialog() != true) return;

            foreach (var file in dialog.FileNames)
            {
                if (NewOarxModules.Any(m => m.ProjectPath.Equals(
                        file, StringComparison.OrdinalIgnoreCase)))
                    continue;
                NewOarxModules.Add(new OarxModuleDraft(file));
            }

            if (string.IsNullOrWhiteSpace(NewOarxName) && NewOarxModules.Count > 0)
                NewOarxName = NewOarxModules[NewOarxModules.Count - 1].ProjectName;
        }

        [RelayCommand]
        private void MoveOarxModuleUp(OarxModuleDraft module)
        {
            int i = NewOarxModules.IndexOf(module);
            if (i > 0) NewOarxModules.Move(i, i - 1);
        }

        [RelayCommand]
        private void MoveOarxModuleDown(OarxModuleDraft module)
        {
            int i = NewOarxModules.IndexOf(module);
            if (i >= 0 && i < NewOarxModules.Count - 1) NewOarxModules.Move(i, i + 1);
        }

        [RelayCommand]
        private void RemoveOarxModule(OarxModuleDraft module) => NewOarxModules.Remove(module);

        [RelayCommand]
        private void ConfirmAddOarx()
        {
            var ed = Autodesk.AutoCAD.ApplicationServices.Application
                .DocumentManager.MdiActiveDocument?.Editor;

            // Same single entry point the MCP surface will use: validates, writes
            // plugins.json, builds the live registration and its LOAD/DEV/UNLOAD
            // commands — which raises Registered, and OnOarxRegistered adds the card.
            var result = OarxConfigLoader.RegisterNewPlugin(
                NewOarxSolution,
                NewOarxModules.Select(m => m.ProjectPath).ToList(),
                buildConfiguration: "Debug",
                name: string.IsNullOrWhiteSpace(NewOarxName) ? null : NewOarxName,
                commandPrefix: string.IsNullOrWhiteSpace(NewOarxPrefix) ? null : NewOarxPrefix,
                loadOnStartup: NewOarxLoadOnStartup);

            if (!result.Success)
            {
                ed?.WriteMessage($"\nAdd OARX group failed: {result.Message}");
                System.Windows.MessageBox.Show(
                    result.Message, "Add OARX group",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            IsAddingOarx = false;
        }

        [RelayCommand]
        private void CancelAddOarx()
        {
            NewOarxModules.Clear();
            IsAddingOarx = false;
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
                if (!OarxManager.IsRegistered(entry.Name))
                    OarxConfigLoader.RegisterFromConfig(entry);
        }
    }

    /// <summary>One row of the "add OARX group" form's ordered module list.
    /// A type rather than a bare path string so the row can show the project
    /// name while the reorder commands still carry the full path.</summary>
    public sealed class OarxModuleDraft
    {
        public OarxModuleDraft(string projectPath) => ProjectPath = projectPath;

        public string ProjectPath { get; }
        public string ProjectName => Path.GetFileNameWithoutExtension(ProjectPath);
    }
}
