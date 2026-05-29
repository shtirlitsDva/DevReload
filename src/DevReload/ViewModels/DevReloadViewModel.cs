using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Threading;

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

        private string? _pendingCsprojPath;

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

        // "Build only" flyout: build the current selection without loading, so a
        // freshly-selected worktree gets its DLLs and Shared can be configured.
        [RelayCommand]
        private void BuildOnlyPlugin(string name)
        {
            PluginManager.BuildOnly(name);
            RefreshStates();

            var vm = Plugins.FirstOrDefault(p => p.Name == name);
            vm?.RefreshSharedConfig();
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
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select a .csproj to register as a DevReload plugin",
                Filter = "C# project (*.csproj)|*.csproj",
                CheckFileExists = true,
            };
            if (dialog.ShowDialog() != true) return;

            _pendingCsprojPath = dialog.FileName;
            NewPluginName = Path.GetFileNameWithoutExtension(_pendingCsprojPath);
            NewPluginPrefix = "";
            NewPluginLoadOnStartup = false;
            IsAddingPlugin = true;
        }

        [RelayCommand]
        private void ConfirmAddPlugin()
        {
            if (string.IsNullOrWhiteSpace(_pendingCsprojPath))
                return;

            var ed = Application.DocumentManager.MdiActiveDocument?.Editor;

            // Same single entry point the MCP register_new_plugin tool uses:
            // derives name from the csproj filename, resolves dllPath via
            // MSBuild, persists to plugins.json, wires PluginManager and the
            // LOAD/DEV/UNLOAD commands.
            var result = PluginConfigLoader.RegisterNewPlugin(
                _pendingCsprojPath,
                buildConfiguration: "Debug",
                commandPrefix: string.IsNullOrWhiteSpace(NewPluginPrefix)
                    ? null : NewPluginPrefix,
                loadOnStartup: NewPluginLoadOnStartup);

            if (!result.Success)
            {
                ed?.WriteMessage($"\nAdd Plugin failed: {result.Message}");
                return;
            }

            // The disk + PluginManager + command tables are all current.
            // Mirror the registered entry into the bound _config so the
            // palette's other state (NSLOAD csv path, etc.) survives, and
            // append a single VM for the new plugin.
            var fresh = PluginConfigLoader.Load() ?? new PluginConfig();
            var entry = fresh.Plugins.FirstOrDefault(
                p => p.Name.Equals(result.Name, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                ed?.WriteMessage(
                    $"\nAdd Plugin: registration reported success but '{result.Name}' " +
                    "not found in plugins.json on re-read.");
                return;
            }

            _config.Plugins.Add(entry);

            var vm = new PluginItemViewModel(entry);
            vm.PropertyChanged += OnPluginPropertyChanged;
            vm.RefreshState();
            Plugins.Add(vm);
            HasPlugins = true;

            _pendingCsprojPath = null;
            IsAddingPlugin = false;
        }

        [RelayCommand]
        private void CancelAddPlugin()
        {
            _pendingCsprojPath = null;
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

            if (string.IsNullOrEmpty(entry.ProjectFilePath))
            {
                System.Windows.MessageBox.Show(
                    "No project file is recorded for this plugin, so its build " +
                    "directory can't be resolved.",
                    "Shared Assemblies",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            string? buildDir;
            try
            {
                buildDir = DevReloadService.ResolveBuildDir(
                    entry.ProjectFilePath, entry.ActiveWorktreePath,
                    entry.BuildConfiguration);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    ex.Message, "Shared Assemblies",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
                return;
            }

            // Build-first: the dialog configures the worktree's REAL DLLs, so the
            // selected branch/config must have been built. If not, send the user
            // to the Reload flyout's "Build only" — no guessing, no fallback dir.
            if (string.IsNullOrEmpty(buildDir)
                || !Directory.Exists(buildDir)
                || !Directory.EnumerateFiles(buildDir, "*.dll").Any())
            {
                System.Windows.MessageBox.Show(
                    "This branch / configuration hasn't been built yet, so there " +
                    "are no assemblies to configure.\n\n" +
                    "Build it first via the Reload ▾ menu → \"Build only\", " +
                    "then open Shared again.",
                    "Shared Assemblies",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }

            // Seed dialog state from the per-build config file. Missing file =
            // empty seed; no fallback to plugins.json.
            var current = SharedAssembliesFile.Read(buildDir);
            var sources = DiscoverCopyFromSources(entry, buildDir);

            var vm = new SharedAssembliesViewModel(
                buildDir,
                current.SharedAssemblies,
                current.MixedModeAssemblies,
                current.StreamedAssemblies,
                sources);
            var win = new SharedAssembliesWindow { DataContext = vm };

            if (win.ShowDialog() == true && vm.Saved)
            {
                SharedAssembliesFile.Write(
                    buildDir,
                    vm.GetSelectedNames(),
                    vm.GetMixedModeNames(),
                    vm.GetStreamedNames());

                Plugins.FirstOrDefault(p => p.Name == name)?.RefreshSharedConfig();
            }
        }

        // Other worktrees/branches whose build dir already holds a
        // SharedAssemblies.Config.json the user can copy from. The repo-relative
        // bin path is identical across worktrees, so we derive each candidate's
        // build dir by swapping the worktree root onto the current build dir — no
        // extra MSBuild queries. Only dirs whose config file actually exists are
        // offered.
        private static List<SharedConfigSource> DiscoverCopyFromSources(
            PluginEntry entry, string currentBuildDir)
        {
            var sources = new List<SharedConfigSource>();
            if (string.IsNullOrEmpty(entry.ProjectFilePath)) return sources;

            string? projectDir = Path.GetDirectoryName(entry.ProjectFilePath);
            if (projectDir == null) return sources;

            string? repoRoot = GitWorktreeService.GetRepoRoot(projectDir);
            if (repoRoot == null) return sources;

            string currentRoot = string.IsNullOrEmpty(entry.ActiveWorktreePath)
                ? repoRoot
                : entry.ActiveWorktreePath!;
            string relBin = Path.GetRelativePath(currentRoot, currentBuildDir);

            foreach (var wt in GitWorktreeService.ListWorktrees(repoRoot))
            {
                string candidateDir = Path.GetFullPath(Path.Combine(wt.Path, relBin));
                if (candidateDir.Equals(currentBuildDir, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!File.Exists(SharedAssembliesFile.PathFor(candidateDir)))
                    continue;

                string label = wt.IsMain ? $"{wt.Branch} (main)" : wt.Branch;
                sources.Add(new SharedConfigSource(label, candidateDir));
            }

            return sources;
        }

        [RelayCommand]
        private void PushToProduction(string name)
        {
            var entry = _config.Plugins.FirstOrDefault(
                p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return;

            if (string.IsNullOrEmpty(entry.ProjectFilePath))
            {
                System.Windows.MessageBox.Show(
                    "No project file is recorded for this plugin, so its build " +
                    "directory can't be resolved.",
                    "Push to Production",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            // The dev build's SharedAssemblies.Config.json is the source —
            // we copy its contents to the chosen production app's directory.
            string? pluginDir;
            try
            {
                pluginDir = DevReloadService.ResolveBuildDir(
                    entry.ProjectFilePath, entry.ActiveWorktreePath,
                    entry.BuildConfiguration);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    ex.Message, "Push to Production",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
                return;
            }

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

        // Green-tints the "Shared" button when the current branch+config build dir
        // already has a SharedAssemblies.Config.json.
        [ObservableProperty] private bool _hasSharedConfig;

        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

        public ObservableCollection<WorktreeItem> Worktrees { get; } = new();

        public PluginItemViewModel(PluginEntry entry)
        {
            Entry = entry;
            _loadOnStartup = entry.LoadOnStartup;
            _isReleaseBuild = entry.BuildConfiguration
                .Equals("Release", StringComparison.OrdinalIgnoreCase);

            RefreshSharedConfig();
        }

        partial void OnLoadOnStartupChanged(bool value)
        {
            Entry.LoadOnStartup = value;
        }

        partial void OnIsReleaseBuildChanged(bool value)
        {
            Entry.BuildConfiguration = value ? "Release" : "Debug";
            PluginManager.UpdateBuildConfiguration(Name, Entry.BuildConfiguration);
            RefreshSharedConfig();
        }

        partial void OnSelectedWorktreeChanged(WorktreeItem? value)
        {
            if (value == null) return;

            string? newPath = value.IsMain ? null : value.Path;
            bool changed = !string.Equals(
                Entry.ActiveWorktreePath, newPath, StringComparison.OrdinalIgnoreCase);

            Entry.ActiveWorktreePath = newPath;

            // RefreshWorktrees re-sets the selection on every refresh; only react
            // to a genuine branch switch (avoids redundant dotnet queries).
            if (!changed) return;

            PluginManager.UpdateActiveWorktree(Name, Entry.ActiveWorktreePath);
            RefreshSharedConfig();
        }

        // Recompute (off the UI thread) whether the current branch+config build dir
        // holds a SharedAssemblies.Config.json, then tint the Shared button. The
        // MSBuild query can spawn dotnet, so it must not run on the UI thread.
        // Best-effort decoration: on any resolution error we just show no tint.
        public void RefreshSharedConfig()
        {
            if (string.IsNullOrEmpty(Entry.ProjectFilePath))
            {
                HasSharedConfig = false;
                return;
            }

            string projectFile = Entry.ProjectFilePath!;
            string? worktree = Entry.ActiveWorktreePath;
            string config = Entry.BuildConfiguration;

            Task.Run(() =>
            {
                bool present = false;
                try
                {
                    string? buildDir = DevReloadService.ResolveBuildDir(
                        projectFile, worktree, config);
                    present = !string.IsNullOrEmpty(buildDir)
                        && File.Exists(SharedAssembliesFile.PathFor(buildDir));
                }
                catch
                {
                    present = false;
                }

                _dispatcher.Invoke(() => HasSharedConfig = present);
            });
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
