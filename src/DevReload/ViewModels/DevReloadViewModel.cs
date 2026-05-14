using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Data;

using Autodesk.AutoCAD.ApplicationServices;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DevReload.Views;

namespace DevReload
{
    public class WorktreeItem
    {
        public string Path { get; set; } = "";
        public string Branch { get; set; } = "";
        public bool IsMain { get; set; }
        public override string ToString() => Branch;
    }
}

namespace DevReload.ViewModels
{
    public partial class DevReloadViewModel : ObservableObject
    {
        private PluginConfig _config = new();

        public ObservableCollection<PluginItemViewModel> Plugins { get; } = new();

        [ObservableProperty] private bool _hasPlugins;
        [ObservableProperty] private bool _isAddingPlugin;

        // ── Add Plugin form fields ────────────────────────────────────

        [ObservableProperty] private string _newPluginName = "";
        [ObservableProperty] private string _newPluginPrefix = "";
        [ObservableProperty] private bool _newPluginLoadOnStartup;

        private VsProjectInfo? _selectedProject;

        // ── Construction ──────────────────────────────────────────────

        public DevReloadViewModel()
        {
            LoadFromConfig();
        }

        private void LoadFromConfig()
        {
            foreach (var item in Plugins)
                item.PropertyChanged -= OnPluginPropertyChanged;
            Plugins.Clear();

            _config = PluginConfigLoader.Load() ?? new PluginConfig();
            PluginConfigLoader.MigrateIfNeeded(_config);

            foreach (var entry in _config.Plugins)
            {
                var vm = new PluginItemViewModel(entry);
                vm.PropertyChanged += OnPluginPropertyChanged;
                vm.RefreshState();
                Plugins.Add(vm);
            }

            HasPlugins = Plugins.Count > 0;
        }

        private void OnPluginPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // LoadOnStartup is the only mutable property NOT routed through
            // PluginManager — it has no runtime state, only file state. The
            // other properties (IsReleaseBuild, SelectedWorktree) are
            // already persisted inside PluginManager.UpdateX; saving here
            // would be a redundant duplicate write. Keep the safety net
            // tight so the UI and RPC genuinely share one persistence path.
            if (e.PropertyName == nameof(PluginItemViewModel.LoadOnStartup))
                SaveConfig();
        }

        // ── Plugin lifecycle commands ─────────────────────────────────

        [RelayCommand]
        private void LoadPlugin(string name)
        {
            PluginManager.Load(name);
            RefreshStates();
        }

        [RelayCommand]
        private void DevReloadPlugin(string name)
        {
            PluginManager.DevReload(name);
            RefreshStates();

            var vm = Plugins.FirstOrDefault(p => p.Name == name);
            vm?.RefreshWorktrees();
        }

        [RelayCommand]
        private void UnloadPlugin(string name)
        {
            PluginManager.Unload(name);
            RefreshStates();
        }

        // ── Add / Remove ─────────────────────────────────────────────

        [RelayCommand]
        private void ShowAddPlugin()
        {
            var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            var projects = DevReloadService.GetAvailableProjects(ed);
            if (projects.Count == 0) return;

            bool multipleInstances = projects.Select(p => p.SolutionName).Distinct().Count() > 1;
            var displayNames = projects.Select(p =>
                multipleInstances ? $"{p.SolutionName}:{p.Name}" : p.Name).ToList();

            string? selection = IntersectUtilities.StringGridFormCaller.Call(
                displayNames, "Select a project to register:");
            if (string.IsNullOrEmpty(selection)) return;

            int idx = displayNames.IndexOf(selection);
            if (idx < 0) return;

            _selectedProject = projects[idx];
            NewPluginName = _selectedProject.Name;
            NewPluginPrefix = "";
            NewPluginLoadOnStartup = false;
            IsAddingPlugin = true;
        }

        [RelayCommand]
        private void ConfirmAddPlugin()
        {
            if (_selectedProject == null || string.IsNullOrWhiteSpace(NewPluginName))
                return;

            string name = NewPluginName.Trim();

            var entry = new PluginEntry
            {
                Name = name,
                CommandPrefix = string.IsNullOrWhiteSpace(NewPluginPrefix)
                    ? null : NewPluginPrefix.Trim().ToUpperInvariant(),
                DllPath = _selectedProject.DebugDllPath,
                VsProject = _selectedProject.Name,
                ProjectFilePath = _selectedProject.ProjectFilePath,
                LoadOnStartup = NewPluginLoadOnStartup,
            };

            // Same unified call the RPC tool register_new_plugin uses:
            // persists to plugins.json AND wires the registration into
            // PluginManager + creates the LOAD/DEV/UNLOAD commands. No
            // separate "palette persistence" path.
            var result = PluginConfigLoader.RegisterNewPlugin(entry);
            if (!result.Success) return;

            // Refresh the binding source (_config) so the new entry shows
            // up in the palette. The disk file is already current.
            _config.Plugins.Add(entry);

            var vm = new PluginItemViewModel(entry);
            vm.PropertyChanged += OnPluginPropertyChanged;
            vm.RefreshState();
            Plugins.Add(vm);
            HasPlugins = true;

            IsAddingPlugin = false;
        }

        [RelayCommand]
        private void CancelAddPlugin()
        {
            IsAddingPlugin = false;
        }

        [RelayCommand]
        private void RemovePlugin(string name)
        {
            var vm = Plugins.FirstOrDefault(p => p.Name == name);
            if (vm == null) return;

            // PluginManager.Unregister handles both the in-memory teardown
            // AND the plugins.json removal — same call the RPC unregister
            // tool makes. We only need to refresh the binding source.
            PluginManager.Unregister(name);
            _config.Plugins.RemoveAll(e =>
                e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            vm.PropertyChanged -= OnPluginPropertyChanged;
            Plugins.Remove(vm);
            HasPlugins = Plugins.Count > 0;
        }

        // ── Settings ─────────────────────────────────────────────────

        [RelayCommand]
        private void Settings()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select NSLOAD Register CSV",
                Filter = "CSV files|*.csv|All files|*.*",
                CheckFileExists = true,
                InitialDirectory = !string.IsNullOrEmpty(_config.NsloadCsvPath)
                    ? Path.GetDirectoryName(_config.NsloadCsvPath) ?? ""
                    : "",
            };

            if (dlg.ShowDialog() != true) return;

            _config.NsloadCsvPath = dlg.FileName;
            SaveConfig();

            System.Windows.MessageBox.Show(
                $"NSLOAD CSV path set to:\n{dlg.FileName}",
                "Settings",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }

        // ── Shared Assemblies + Push to Production ────────────────────

        [RelayCommand]
        private void SharedAssemblies(string name)
        {
            var entry = _config.Plugins.FirstOrDefault(
                p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return;

            // Resolve the build directory via the csproj's MSBuild TargetPath
            // (worktree-aware). The file we read/write lives inside that dir,
            // so the dialog automatically reflects the active worktree.
            string pluginDir = ResolveEffectivePluginDir(entry);

            if (string.IsNullOrEmpty(pluginDir) || !Directory.Exists(pluginDir))
            {
                System.Windows.MessageBox.Show(
                    $"Plugin directory not found:\n{pluginDir}",
                    "Shared Assemblies",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            // Seed dialog state from the per-build config file. Missing file =
            // empty seed; no fallback to plugins.json.
            var current = SharedAssembliesFile.Read(pluginDir);
            var vm = new SharedAssembliesViewModel(
                pluginDir,
                current.SharedAssemblies,
                current.MixedModeAssemblies,
                current.StreamedAssemblies);
            var win = new SharedAssembliesWindow { DataContext = vm };

            if (win.ShowDialog() == true && vm.Saved)
            {
                SharedAssembliesFile.Write(
                    pluginDir,
                    vm.GetSelectedNames(),
                    vm.GetMixedModeNames(),
                    vm.GetStreamedNames());
            }
        }

        // Returns the directory whose SharedAssemblies.Config.json is the
        // configuration source for the currently-selected build of this
        // plugin: csproj → worktree-remapped csproj → MSBuild TargetPath →
        // parent directory. Last-resort safety net: entry.DllPath's directory
        // for cases where MSBuild can't be queried (no csproj recorded,
        // repo-root lookup fails, dotnet not on PATH, etc.).
        private static string ResolveEffectivePluginDir(PluginEntry entry)
        {
            string Fallback() =>
                !string.IsNullOrEmpty(entry.DllPath)
                    ? Path.GetDirectoryName(entry.DllPath)!
                    : "";

            if (string.IsNullOrEmpty(entry.ProjectFilePath))
                return Fallback();

            string csproj = entry.ProjectFilePath!;

            if (!string.IsNullOrEmpty(entry.ActiveWorktreePath))
            {
                string? repoRoot = GitWorktreeService.GetRepoRoot(
                    Path.GetDirectoryName(csproj)!);
                if (repoRoot != null)
                {
                    csproj = GitWorktreeService.RemapToWorktree(
                        entry.ProjectFilePath!, repoRoot, entry.ActiveWorktreePath!);
                }
            }

            string? targetPath = DevReloadService.QueryMsBuildProperty(
                csproj, "TargetPath", entry.BuildConfiguration);

            if (string.IsNullOrEmpty(targetPath))
                return Fallback();

            return Path.GetDirectoryName(targetPath)!;
        }

        [RelayCommand]
        private void PushToProduction(string name)
        {
            var entry = _config.Plugins.FirstOrDefault(
                p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return;

            // The dev build's SharedAssemblies.Config.json is the source —
            // we copy its contents to the chosen production app's directory.
            string pluginDir = ResolveEffectivePluginDir(entry);
            if (string.IsNullOrEmpty(pluginDir) || !Directory.Exists(pluginDir))
            {
                System.Windows.MessageBox.Show(
                    $"Plugin build directory not found:\n{pluginDir}\n\n" +
                    "Build the plugin first (DEV / LOAD).",
                    "Push to Production",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            var devConfig = SharedAssembliesFile.Read(pluginDir);
            if (devConfig.SharedAssemblies.Count == 0)
            {
                System.Windows.MessageBox.Show(
                    "No shared assemblies configured for this plugin's current build.\n" +
                    "Use the \"Shared\" button to configure them first.",
                    "Push to Production",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }

            // Read NSLOAD's production app registry (CSV + user config)
            var apps = NsloadAppRegistry.Load(_config.NsloadCsvPath);
            if (apps.Count == 0)
            {
                System.Windows.MessageBox.Show(
                    "No NSLOAD production apps found.\n" +
                    "Check that \"nsloadCsvPath\" is set in plugins.json.",
                    "Push to Production",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            // Build display list and let the developer pick the target app
            var displayNames = apps.Select(a => a.Name).ToList();
            string? selection = IntersectUtilities.StringGridFormCaller.Call(
                displayNames,
                $"Push shared assemblies for '{name}' to which production app?");
            if (string.IsNullOrEmpty(selection)) return;

            var target = apps.FirstOrDefault(a => a.Name == selection);
            if (string.IsNullOrEmpty(target.DllDir) || !Directory.Exists(target.DllDir))
            {
                System.Windows.MessageBox.Show(
                    $"Target directory not found:\n{target.DllDir}",
                    "Push to Production",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            SharedAssembliesFile.Write(
                target.DllDir,
                devConfig.SharedAssemblies,
                devConfig.MixedModeAssemblies,
                devConfig.StreamedAssemblies);

            // Remember which production app this plugin targets
            entry.ProductionTarget = selection;
            SaveConfig();

            System.Windows.MessageBox.Show(
                $"SharedAssemblies.Config.json written to:\n{target.DllDir}\n\n" +
                $"Target app: {selection}\n" +
                $"Assemblies: {string.Join(", ", devConfig.SharedAssemblies)}",
                "Push to Production",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }

        // ── Reload Config ─────────────────────────────────────────────

        [RelayCommand]
        private void ReloadConfig()
        {
            // Unregister all current plugins
            foreach (var name in PluginManager.GetRegisteredPluginNames().ToList())
                PluginManager.Unregister(name);

            // Re-read config and register fresh
            LoadFromConfig();

            foreach (var entry in _config.Plugins)
            {
                if (!PluginManager.IsRegistered(entry.Name))
                    DevReloaderCommands.RegisterFromConfig(entry);
            }

            // Auto-load plugins with loadOnStartup
            foreach (var entry in _config.Plugins.Where(e => e.LoadOnStartup))
            {
                if (!PluginManager.IsLoaded(entry.Name))
                    PluginManager.Load(entry.Name);
            }

            RefreshStates();
        }

        // ── Helpers ───────────────────────────────────────────────────

        private void RefreshStates()
        {
            foreach (var p in Plugins)
                p.RefreshState();
        }

        private void SaveConfig()
        {
            PluginConfigLoader.Save(_config);
        }
    }

    // ══════════════════════════════════════════════════════════════════

    public partial class PluginItemViewModel : ObservableObject
    {
        internal readonly PluginEntry Entry;

        public string Name => Entry.Name;
        public string CommandPrefix => (Entry.CommandPrefix ?? Entry.Name).ToUpperInvariant();

        [ObservableProperty] private bool _isLoaded;
        [ObservableProperty] private string _status = "Unloaded";
        [ObservableProperty] private bool _loadOnStartup;
        [ObservableProperty] private bool _isReleaseBuild;
        [ObservableProperty] private WorktreeItem? _selectedWorktree;

        public ObservableCollection<WorktreeItem> Worktrees { get; } = new();

        public PluginItemViewModel(PluginEntry entry)
        {
            Entry = entry;
            _loadOnStartup = entry.LoadOnStartup;
            _isReleaseBuild = entry.BuildConfiguration
                .Equals("Release", StringComparison.OrdinalIgnoreCase);
        }

        partial void OnLoadOnStartupChanged(bool value)
        {
            Entry.LoadOnStartup = value;
        }

        partial void OnIsReleaseBuildChanged(bool value)
        {
            Entry.BuildConfiguration = value ? "Release" : "Debug";
            PluginManager.UpdateBuildConfiguration(Name, Entry.BuildConfiguration);
        }

        partial void OnSelectedWorktreeChanged(WorktreeItem? value)
        {
            if (value == null) return;
            Entry.ActiveWorktreePath = value.IsMain ? null : value.Path;
            PluginManager.UpdateActiveWorktree(Name, Entry.ActiveWorktreePath);
        }

        public void RefreshState()
        {
            IsLoaded = PluginManager.IsRegistered(Name) && PluginManager.IsLoaded(Name);
            Status = IsLoaded ? "Loaded" : "Unloaded";
            RefreshWorktrees();
        }

        public void RefreshWorktrees()
        {
            if (string.IsNullOrEmpty(Entry.ProjectFilePath)) return;

            string? projectDir = Path.GetDirectoryName(Entry.ProjectFilePath);
            if (projectDir == null) return;

            string? repoRoot = GitWorktreeService.GetRepoRoot(projectDir);
            if (repoRoot == null) return;

            var worktrees = GitWorktreeService.ListWorktrees(repoRoot);

            var previousSelection = SelectedWorktree;
            Worktrees.Clear();

            foreach (var wt in worktrees)
                Worktrees.Add(new WorktreeItem
                {
                    Path = wt.Path,
                    Branch = wt.Branch,
                    IsMain = wt.IsMain,
                });

            // Restore selection: match ActiveWorktreePath, or fall back to main
            if (!string.IsNullOrEmpty(Entry.ActiveWorktreePath))
                SelectedWorktree = Worktrees.FirstOrDefault(
                    w => w.Path.Equals(Entry.ActiveWorktreePath,
                        StringComparison.OrdinalIgnoreCase))
                    ?? Worktrees.FirstOrDefault(w => w.IsMain);
            else
                SelectedWorktree = Worktrees.FirstOrDefault(w => w.IsMain);
        }
    }

    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Simple IValueConverter that inverts a boolean.
    /// Used in XAML as {x:Static vm:InvertBoolConverter.Instance}.
    /// </summary>
    public class InvertBoolConverter : IValueConverter
    {
        public static readonly InvertBoolConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter,
            CultureInfo culture)
            => value is bool b ? !b : value;

        public object ConvertBack(object value, Type targetType, object parameter,
            CultureInfo culture)
            => value is bool b ? !b : value;
    }

}
