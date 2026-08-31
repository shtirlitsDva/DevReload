using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DevReload.Core;
using DevReload.Oarx;

namespace DevReload.ViewModels
{
    /// <summary>
    /// One OARX card in the palette's OARX tab.
    /// </summary>
    /// <remarks>
    /// A sibling of <see cref="PluginItemViewModel"/>, not a subclass and not a
    /// mode of it. The two cards look similar because both wrap a build+load
    /// cycle, but nothing behind them is shared: an OARX group is an ORDERED set
    /// of native modules built under one solution, it has no shared-assembly
    /// configuration and no production push, and its reload is unload-first
    /// rather than build-first. Merging them would mean a card whose controls
    /// are half-disabled either way.
    /// </remarks>
    public partial class OarxPluginItemViewModel : ObservableObject
    {
        private const string Platform = "x64";

        internal readonly OarxPluginEntry Entry;
        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

        public string Name => Entry.Name;
        public string CommandPrefix => (Entry.CommandPrefix ?? Entry.Name).ToUpperInvariant();

        /// <summary>Compact "props 2 · pre 2 · post 1" chip so the group's
        /// build properties and companions are visible without opening the edit
        /// form. Empty (chip collapsed) when the group has none.</summary>
        public string CompanionsSummary
        {
            get
            {
                int props = Entry.MsBuildProperties.Count;
                int pre = Entry.PreloadNativeModules.Count + Entry.PreloadManagedAssemblies.Count;
                int post = Entry.PostloadManagedAssemblies.Count;
                return props + pre + post == 0 ? "" : $"props {props} · pre {pre} · post {post}";
            }
        }

        public bool HasCompanions => CompanionsSummary.Length > 0;

        public string CompanionsToolTip => string.Join("\n",
            Entry.MsBuildProperties.Select(p => $"prop  {p}")
            .Concat(Entry.PreloadNativeModules.Select(p => $"pre (native)  {p}"))
            .Concat(Entry.PreloadManagedAssemblies.Select(p => $"pre (managed)  {p}"))
            .Concat(Entry.PostloadManagedAssemblies.Select(p => $"post (managed)  {p}")));

        [ObservableProperty] private bool _isLoaded;
        [ObservableProperty] private string _status = "Unloaded";
        [ObservableProperty] private string _modules = "";
        [ObservableProperty] private bool _loadOnStartup;
        [ObservableProperty] private string _selectedConfiguration = "Debug";
        [ObservableProperty] private WorktreeItem? _selectedWorktree;

        [ObservableProperty] private bool _isConfigPickerOpen;
        [ObservableProperty] private bool _isBuildMenuOpen;

        public ObservableCollection<string> AvailableConfigurations { get; } = new();
        public ObservableCollection<WorktreeItem> Worktrees { get; } = new();

        public OarxPluginItemViewModel(OarxPluginEntry entry)
        {
            Entry = entry;
            _loadOnStartup = entry.LoadOnStartup;
            _selectedConfiguration = entry.BuildConfiguration;

            RefreshConfigurations();
        }

        partial void OnLoadOnStartupChanged(bool value) => Entry.LoadOnStartup = value;

        // ── Lifecycle ────────────────────────────────────────────────
        //
        // Straight to OarxManager — the card is a view of the OARX registry, the
        // same way the .NET card is a view of PluginManager's. The refresh comes
        // back through OarxManager.StateChanged, so no explicit refresh here.

        [RelayCommand]
        private void Reload() => OarxManager.Reload(Name);

        [RelayCommand]
        private void Unload() => OarxManager.Unload(Name);

        [RelayCommand]
        private void Load() => OarxManager.Load(Name);

        [RelayCommand]
        private void BuildOnly()
        {
            OarxManager.BuildOnly(Name);
            IsBuildMenuOpen = false;
        }

        [RelayCommand]
        private void SelectConfiguration(string configuration)
        {
            SelectedConfiguration = configuration;
            IsConfigPickerOpen = false;
        }

        partial void OnSelectedConfigurationChanged(string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (string.Equals(Entry.BuildConfiguration, value, StringComparison.Ordinal))
                return;

            Entry.BuildConfiguration = value;
            OarxManager.UpdateBuildConfiguration(Name, value);
        }

        partial void OnSelectedWorktreeChanged(WorktreeItem? value)
        {
            if (value == null) return;

            string? newPath = value.IsMain ? null : value.Path;
            bool changed = !string.Equals(
                Entry.ActiveWorktreePath, newPath, StringComparison.OrdinalIgnoreCase);

            Entry.ActiveWorktreePath = newPath;
            if (!changed) return;

            OarxManager.UpdateActiveWorktree(Name, Entry.ActiveWorktreePath);
            // A different branch's projects may declare a different set of
            // Configuration|Platform pairs.
            RefreshConfigurations();
        }

        // ── Refresh ──────────────────────────────────────────────────

        public void RefreshState()
        {
            IsLoaded = OarxManager.IsRegistered(Name) && OarxManager.IsLoaded(Name);
            Status = IsLoaded ? "Loaded" : "Unloaded";
            // A config edit staged while the group was loaded — the next
            // load/reload applies it; say so until then.
            if (OarxManager.HasPendingConfig(Name))
                Status += " · config change staged";
            Modules = string.Join("  ->  ", OarxManager.DescribeModules(Name));
            RefreshWorktrees();
        }

        /// <summary>
        /// Re-enumerate the configurations the FIRST module's project declares.
        /// Off the UI thread — the query spawns MSBuild.
        /// </summary>
        /// <remarks>
        /// One project speaks for the group: the modules build under a single
        /// solution and are always built with one configuration, so a per-module
        /// list would only be able to disagree with itself.
        /// </remarks>
        public void RefreshConfigurations()
        {
            string? project = Entry.ProjectFilePaths.FirstOrDefault();
            if (string.IsNullOrEmpty(project)) return;

            string? worktree = Entry.ActiveWorktreePath;
            string current = SelectedConfiguration;
            // C++ output paths resolve through $(SolutionDir); querying the
            // .vcxproj standalone answers about a directory the build never uses.
            string? solutionDir = SolutionDirectory();

            Task.Run(() =>
            {
                IReadOnlyList<string> configs;
                try
                {
                    configs = BuildService.GetConfigurations(
                        project!, worktree, Platform, solutionDir);
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

        public void RefreshWorktrees()
        {
            string? solutionDir = SolutionDirectory();
            if (solutionDir == null) return;

            string? repoRoot = GitWorktreeService.GetRepoRoot(solutionDir);
            if (repoRoot == null) return;

            Worktrees.Clear();
            foreach (var wt in GitWorktreeService.ListWorktrees(repoRoot))
                Worktrees.Add(new WorktreeItem
                {
                    Path = wt.Path,
                    Branch = wt.Branch,
                    IsMain = wt.IsMain,
                });

            if (!string.IsNullOrEmpty(Entry.ActiveWorktreePath))
                SelectedWorktree = Worktrees.FirstOrDefault(
                        w => w.Path.Equals(Entry.ActiveWorktreePath,
                            StringComparison.OrdinalIgnoreCase))
                    ?? Worktrees.FirstOrDefault(w => w.IsMain);
            else
                SelectedWorktree = Worktrees.FirstOrDefault(w => w.IsMain);
        }

        /// <summary>The solution directory for the CURRENTLY SELECTED worktree.</summary>
        private string? SolutionDirectory()
        {
            if (string.IsNullOrEmpty(Entry.SolutionFilePath)) return null;
            return Path.GetDirectoryName(
                GitWorktreeService.ResolveActiveCsproj(
                    Entry.SolutionFilePath, Entry.ActiveWorktreePath));
        }
    }
}
