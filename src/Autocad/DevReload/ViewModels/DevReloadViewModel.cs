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

using DevReload.Core;
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
        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

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

            // The card list is a projection of PluginManager's registry. By
            // subscribing here, a plugin registered out-of-band — e.g. via the
            // MCP register_new_plugin tool while this palette is open — shows up
            // as a card without a restart, and an unregister drops its card.
            // Both the palette singleton and PluginManager live for the whole
            // AutoCAD session, so no unsubscribe is needed.
            PluginManager.PluginRegistered += OnPluginRegistered;
            PluginManager.PluginUnregistered += OnPluginUnregistered;
            PluginManager.PluginStateChanged += OnPluginStateChanged;
        }

        // Load/reload/unload can be driven out-of-band — by the MCP tool surface
        // an agent uses, not just the palette buttons. The card's loaded
        // indicator is a projection of PluginManager's registry, so refresh the
        // matching card whenever the registry reports a state change, whoever
        // caused it. Marshal to the UI thread for the same reason as the other
        // registry handlers.
        private void OnPluginStateChanged(string name)
        {
            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.Invoke(() => OnPluginStateChanged(name));
                return;
            }

            Plugins.FirstOrDefault(
                    p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?.RefreshState();
        }

        // Registry events can fire on any thread (MCP tools run on the AutoCAD
        // main thread, but treat the source as untrusted and marshal to the UI
        // thread before touching the ObservableCollection).
        private void OnPluginRegistered(string name)
        {
            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.Invoke(() => OnPluginRegistered(name));
                return;
            }

            if (Plugins.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return; // already projected (e.g. registered from this palette)

            // The registry is the trigger; plugins.json is the entry store and
            // is always written before registration, so the entry is on disk.
            var entry = (PluginConfigLoader.Load()?.Plugins ?? new())
                .FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return;

            if (!_config.Plugins.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                _config.Plugins.Add(entry);

            var vm = new PluginItemViewModel(entry);
            vm.PropertyChanged += OnPluginPropertyChanged;
            vm.RefreshState();
            Plugins.Add(vm);
            HasPlugins = true;
        }

        private void OnPluginUnregistered(string name)
        {
            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.Invoke(() => OnPluginUnregistered(name));
                return;
            }

            _config.Plugins.RemoveAll(
                p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            var vm = Plugins.FirstOrDefault(
                p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (vm != null)
            {
                vm.PropertyChanged -= OnPluginPropertyChanged;
                Plugins.Remove(vm);
            }
            HasPlugins = Plugins.Count > 0;
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
            // other properties (SelectedConfiguration, SelectedWorktree) are
            // already persisted inside PluginManager.UpdateX; saving here
            // would be a redundant duplicate write. Keep the safety net
            // tight so the UI and RPC genuinely share one persistence path.
            if (e.PropertyName == nameof(PluginItemViewModel.LoadOnStartup))
                SaveConfig();
        }

        // Per-plugin lifecycle commands (Reload / Unload / BuildOnly / config
        // pick) live on PluginItemViewModel — each card binds to its own VM. The
        // root VM keeps only collection-level and cross-cutting flows below.

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

            // RegisterNewPlugin → RegisterFromConfig → PluginManager registry,
            // which raised PluginRegistered; OnPluginRegistered already added the
            // card and mirrored the entry into _config. Just close the form.
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
            // PluginManager.Unregister tears down in memory AND removes the
            // plugins.json entry — same call the RPC unregister tool makes — and
            // raises PluginUnregistered, which OnPluginUnregistered handles by
            // dropping the card and the _config entry. Nothing else to do here.
            PluginManager.Unregister(name);
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
                buildDir = BuildService.ResolveBuildDir(
                    entry.ProjectFilePath, entry.ActiveWorktreePath,
                    entry.BuildConfiguration, AcadBuild.Platform);
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
                pluginDir = BuildService.ResolveBuildDir(
                    entry.ProjectFilePath, entry.ActiveWorktreePath,
                    entry.BuildConfiguration, AcadBuild.Platform);
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
            // Resync the in-memory registry to plugins.json on disk.
            //
            // CRITICAL: use UnregisterInMemory, NOT Unregister. The public
            // Unregister ALSO deletes the entry from plugins.json, so calling it
            // in a loop here wiped the entire file before LoadFromConfig could
            // re-read it — leaving an empty config and zero cards.
            var fresh = PluginConfigLoader.Load() ?? new PluginConfig();
            PluginConfigLoader.MigrateIfNeeded(fresh);

            var onDisk = new HashSet<string>(
                fresh.Plugins.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

            // Drop registry entries that are no longer in the file (memory only).
            foreach (var name in PluginManager.GetRegisteredPluginNames().ToList())
                if (!onDisk.Contains(name))
                    PluginManager.UnregisterInMemory(name);

            // Register entries present in the file but not yet in the registry.
            foreach (var entry in fresh.Plugins)
                if (!PluginManager.IsRegistered(entry.Name))
                    DevReloaderCommands.RegisterFromConfig(entry);

            // Rebuild every card from the freshly-read file so edits to existing
            // entries (build config, worktree) are reflected too.
            LoadFromConfig();

            // Auto-load plugins with loadOnStartup
            foreach (var entry in _config.Plugins.Where(e => e.LoadOnStartup))
                if (!PluginManager.IsLoaded(entry.Name))
                    PluginManager.Load(entry.Name);

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
        [ObservableProperty] private string _selectedConfiguration = "Debug";
        [ObservableProperty] private WorktreeItem? _selectedWorktree;

        // Open-state for the two card flyouts. Both the toggle button and its
        // popup bind here (TwoWay), so the picker/build commands can close the
        // flyout by flipping the flag — no code-behind popup-hunting required.
        [ObservableProperty] private bool _isConfigPickerOpen;
        [ObservableProperty] private bool _isBuildMenuOpen;

        // The configurations declared by the plugin's project (Debug, Release,
        // and any custom ones such as IALCD/IALCR). Populated off the UI thread
        // from MSBuild; the compact config dropdown on the card binds to it.
        public ObservableCollection<string> AvailableConfigurations { get; } = new();

        // Green-tints the "Shared" button when the current branch+config build dir
        // already has a SharedAssemblies.Config.json.
        [ObservableProperty] private bool _hasSharedConfig;

        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

        public ObservableCollection<WorktreeItem> Worktrees { get; } = new();

        public PluginItemViewModel(PluginEntry entry)
        {
            Entry = entry;
            _loadOnStartup = entry.LoadOnStartup;
            _selectedConfiguration = entry.BuildConfiguration;

            RefreshSharedConfig();
            RefreshConfigurations();
        }

        partial void OnLoadOnStartupChanged(bool value)
        {
            Entry.LoadOnStartup = value;
        }

        // Per-plugin lifecycle. Each card binds straight to its own VM; the
        // card refresh afterwards is driven reactively by PluginManager's
        // PluginStateChanged event (the same path the MCP tool surface uses),
        // so there's no explicit refresh here.
        [RelayCommand]
        private void Reload() => PluginManager.DevReload(Name);

        [RelayCommand]
        private void Unload() => PluginManager.Unload(Name);

        // Pick a build configuration from the card's dropdown. Lives on the item
        // VM (not code-behind) so the popup's item buttons bind to it directly;
        // assigning SelectedConfiguration persists via OnSelectedConfigurationChanged.
        [RelayCommand]
        private void SelectConfiguration(string configuration)
        {
            SelectedConfiguration = configuration;
            IsConfigPickerOpen = false;
        }

        // "Build only" flyout: build the current selection without loading, so a
        // freshly-selected worktree gets its DLLs and Shared can be configured.
        // PluginManager.BuildOnly is the single shared entry point.
        [RelayCommand]
        private void BuildOnly()
        {
            PluginManager.BuildOnly(Name);

            // Build state isn't load state: the event-driven RefreshState won't
            // pick up that this build just produced the shared-assemblies config,
            // so refresh that decoration explicitly.
            RefreshSharedConfig();
            IsBuildMenuOpen = false;
        }

        partial void OnSelectedConfigurationChanged(string value)
        {
            // RefreshConfigurations re-publishes the list but never reassigns the
            // selection, so this fires only on a genuine user pick. Guard against
            // a no-op anyway to avoid a redundant persist (mirrors the worktree
            // handler's pattern).
            if (string.IsNullOrEmpty(value)) return;
            if (string.Equals(Entry.BuildConfiguration, value, StringComparison.Ordinal))
                return;

            Entry.BuildConfiguration = value;
            PluginManager.UpdateBuildConfiguration(Name, value);
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
            // A different branch's project may declare a different set of
            // configurations, so re-enumerate against the now-active csproj.
            RefreshConfigurations();
        }

        // Re-enumerate the project's configurations (off the UI thread — the
        // MSBuild query can spawn dotnet) and publish them to the dropdown. The
        // currently-selected configuration is always kept in the list, even if
        // enumeration fails or the project no longer declares it, so the card
        // never shows an empty picker or silently loses the active value.
        public void RefreshConfigurations()
        {
            if (string.IsNullOrEmpty(Entry.ProjectFilePath))
                return;

            string projectFile = Entry.ProjectFilePath!;
            string? worktree = Entry.ActiveWorktreePath;
            string current = SelectedConfiguration;

            Task.Run(() =>
            {
                IReadOnlyList<string> configs;
                try
                {
                    configs = BuildService.GetConfigurations(
                        projectFile, worktree, AcadBuild.Platform);
                }
                catch
                {
                    configs = Array.Empty<string>();
                }

                _dispatcher.Invoke(() =>
                {
                    AvailableConfigurations.Clear();
                    foreach (var c in configs)
                        AvailableConfigurations.Add(c);

                    if (!AvailableConfigurations.Contains(
                            current, StringComparer.OrdinalIgnoreCase))
                        AvailableConfigurations.Insert(0, current);
                });
            });
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
                    string? buildDir = BuildService.ResolveBuildDir(
                        projectFile, worktree, config, AcadBuild.Platform);
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
