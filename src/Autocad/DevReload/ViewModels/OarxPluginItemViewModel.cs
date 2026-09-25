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
using DevReload.Diagnostics;
using DevReload.Oarx;

namespace DevReload.ViewModels
{
    /// <summary>One entry of an OARX card's profile dropdown.</summary>
    public sealed class OarxProfileChoice
    {
        public OarxProfileChoice(OarxProfile profile)
        {
            Name = profile.Name;
            WorktreePath = profile.WorktreePath;
            IsMissing = !profile.FolderExists;
        }

        public string Name { get; }
        public string WorktreePath { get; }
        public bool IsMissing { get; }

        public override string ToString() => IsMissing ? $"{Name} (missing)" : Name;
    }

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
    /// <para>The card reads the group's configuration from
    /// <see cref="OarxManager.GetEntry"/> on every refresh, so an edit made
    /// anywhere — the profiles window, an MCP tool, Reload Config — shows up
    /// without the card being rebuilt.</para>
    /// </remarks>
    public partial class OarxPluginItemViewModel : ObservableObject
    {
        private const string Platform = "x64";

        private OarxPluginEntry _entry;
        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

        /// <summary>Set while the card is writing its own controls from the
        /// entry, so those writes are not mistaken for the user choosing.</summary>
        private bool _syncing;

        public string Name => _entry.Name;
        public string CommandPrefix => (_entry.CommandPrefix ?? _entry.Name).ToUpperInvariant();

        private OarxProfile? ActiveProfile => _entry.FindProfile(_entry.ActiveProfile);

        /// <summary>Compact "props 2 · pre 2 · post 1" chip for the ACTIVE
        /// profile, so its build properties and companions are visible without
        /// opening the profiles window. Empty (chip collapsed) when it has none.</summary>
        public string CompanionsSummary
        {
            get
            {
                var p = ActiveProfile;
                if (p == null) return "";
                int props = p.MsBuildProperties.Count;
                int pre = p.PreloadNativeModules.Count + p.PreloadManagedAssemblies.Count;
                int post = p.PostloadManagedAssemblies.Count;
                return props + pre + post == 0 ? "" : $"props {props} · pre {pre} · post {post}";
            }
        }

        public bool HasCompanions => CompanionsSummary.Length > 0;

        public string CompanionsToolTip
        {
            get
            {
                var p = ActiveProfile;
                if (p == null) return "";
                return string.Join("\n",
                    p.MsBuildProperties.Select(x => $"prop  {x}")
                    .Concat(p.PreloadNativeModules.Select(x => $"pre (native)  {x}"))
                    .Concat(p.PreloadManagedAssemblies.Select(x => $"pre (managed)  {x}"))
                    .Concat(p.PostloadManagedAssemblies.Select(x => $"post (managed)  {x}")));
            }
        }

        [ObservableProperty] private bool _isLoaded;
        [ObservableProperty] private string _status = "Unloaded";
        [ObservableProperty] private string _modules = "";
        [ObservableProperty] private bool _loadOnStartup;
        [ObservableProperty] private string _selectedConfiguration = "Debug";
        [ObservableProperty] private OarxProfileChoice? _selectedProfile;

        [ObservableProperty] private bool _isConfigPickerOpen;
        [ObservableProperty] private bool _isBuildMenuOpen;

        public ObservableCollection<string> AvailableConfigurations { get; } = new();
        public ObservableCollection<OarxProfileChoice> Profiles { get; } = new();

        public OarxPluginItemViewModel(OarxPluginEntry entry)
        {
            _entry = entry;
            _loadOnStartup = entry.LoadOnStartup;
            _selectedConfiguration = entry.BuildConfiguration;
            RefreshProfiles();
            RefreshConfigurations();
        }

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

        // ── Settings ─────────────────────────────────────────────────
        //
        // Every write goes through OarxConfigLoader — the same methods the
        // profiles window and the MCP tools use.

        [RelayCommand]
        private void SelectConfiguration(string configuration)
        {
            SelectedConfiguration = configuration;
            IsConfigPickerOpen = false;
        }

        partial void OnSelectedConfigurationChanged(string value)
        {
            if (_syncing || string.IsNullOrEmpty(value)) return;
            if (string.Equals(_entry.BuildConfiguration, value, StringComparison.Ordinal))
                return;
            OarxConfigLoader.UpdatePlugin(Name, new OarxGroupPatch(BuildConfiguration: value));
        }

        partial void OnLoadOnStartupChanged(bool value)
        {
            if (_syncing || _entry.LoadOnStartup == value) return;
            OarxConfigLoader.UpdatePlugin(Name, new OarxGroupPatch(LoadOnStartup: value));
        }

        partial void OnSelectedProfileChanged(OarxProfileChoice? value)
        {
            if (_syncing || value == null) return;
            if (value.Name.Equals(_entry.ActiveProfile, StringComparison.OrdinalIgnoreCase))
                return;

            var result = OarxConfigLoader.ActivateProfile(Name, value.Name);
            var ed = Autodesk.AutoCAD.ApplicationServices.Application
                .DocumentManager.MdiActiveDocument?.Editor;
            ed?.WriteMessage($"\n{Name}: {result.Message}");
            if (!result.Success)
            {
                // Refused (e.g. the folder is gone): put the dropdown back on the
                // profile that is actually active.
                RefreshState();
                System.Windows.MessageBox.Show(result.Message, $"{Name} — profile",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
        }

        // ── Refresh ──────────────────────────────────────────────────

        public void RefreshState()
        {
            var fresh = OarxManager.GetEntry(Name);
            bool activeChanged = false;
            if (fresh != null && !ReferenceEquals(fresh, _entry))
            {
                activeChanged = !fresh.ActiveProfile.Equals(
                    _entry.ActiveProfile, StringComparison.OrdinalIgnoreCase);
                _entry = fresh;
            }

            _syncing = true;
            try
            {
                LoadOnStartup = _entry.LoadOnStartup;
                SelectedConfiguration = _entry.BuildConfiguration;
                RefreshProfiles();
            }
            finally { _syncing = false; }

            IsLoaded = OarxManager.IsRegistered(Name) && OarxManager.IsLoaded(Name);
            Status = IsLoaded ? "Loaded" : "Unloaded";
            // A change staged while the group was loaded — the next load/reload
            // applies it; say so until then.
            string? pending = OarxManager.DescribePending(Name);
            if (pending != null)
                Status += " · " + pending;
            Modules = string.Join("  ->  ", OarxManager.DescribeModules(Name));

            OnPropertyChanged(nameof(CommandPrefix));
            OnPropertyChanged(nameof(CompanionsSummary));
            OnPropertyChanged(nameof(HasCompanions));
            OnPropertyChanged(nameof(CompanionsToolTip));

            // Another folder's projects may declare other Configuration|Platform pairs.
            if (activeChanged) RefreshConfigurations();
        }

        /// <summary>Rebuild the dropdown from the entry. Folder existence is
        /// re-checked each time, so a removed worktree shows as missing.</summary>
        public void RefreshProfiles()
        {
            bool wasSyncing = _syncing;
            _syncing = true;
            try
            {
                Profiles.Clear();
                foreach (var p in _entry.Profiles)
                    Profiles.Add(new OarxProfileChoice(p));
                SelectedProfile = Profiles.FirstOrDefault(p =>
                    p.Name.Equals(_entry.ActiveProfile, StringComparison.OrdinalIgnoreCase));
            }
            finally { _syncing = wasSyncing; }
        }

        /// <summary>
        /// Re-enumerate the configurations the active profile's FIRST module
        /// declares. Off the UI thread — the query spawns MSBuild.
        /// </summary>
        /// <remarks>
        /// One project speaks for the group: the modules build under a single
        /// solution and are always built with one configuration, so a per-module
        /// list would only be able to disagree with itself.
        /// </remarks>
        public void RefreshConfigurations()
        {
            var profile = ActiveProfile;
            string? first = profile?.ProjectFilePaths.FirstOrDefault();
            if (profile == null || first == null || !profile.FolderExists) return;

            string project = profile.Resolve(first);
            string current = SelectedConfiguration;
            // C++ output paths resolve through $(SolutionDir); querying the
            // .vcxproj standalone answers about a directory the build never uses.
            string? solutionDir = Path.GetDirectoryName(profile.Resolve(_entry.Solution));

            Task.Run(() =>
            {
                IReadOnlyList<string> configs;
                try
                {
                    configs = BuildService.GetConfigurations(
                        project, null, Platform, solutionDir);
                }
                catch (Exception ex)
                {
                    // Category B — the picker falls back to showing only the
                    // current configuration; reported so the cause is findable.
                    DevReloadDiagnostics.Report($"{Name}: configuration query", ex);
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
    }
}
