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
    /// its add/edit form.
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

        // ── Add/Edit OARX form fields ─────────────────────────────────
        //
        // ONE form serves both add and edit: EditingOarxName == null is add
        // mode; otherwise the form was pre-filled from that group's entry and
        // Confirm patches it through the same seam the MCP update tool uses.

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(OarxFormTitle))]
        [NotifyPropertyChangedFor(nameof(IsOarxNameEditable))]
        private string? _editingOarxName;

        /// <summary>True when the group being edited has modules mapped right
        /// now — the form shows a banner saying when the changes take effect.</summary>
        [ObservableProperty] private bool _isEditingLoadedOarx;

        public string OarxFormTitle => EditingOarxName == null
            ? "Add OARX Group" : $"Edit OARX Group — {EditingOarxName}";

        /// <summary>The name is the group's identity: free in add mode, fixed in
        /// edit mode (rename = remove + re-add).</summary>
        public bool IsOarxNameEditable => EditingOarxName == null;

        [ObservableProperty] private string _newOarxSolution = "";
        [ObservableProperty] private string _newOarxName = "";
        [ObservableProperty] private string _newOarxPrefix = "";
        [ObservableProperty] private bool _newOarxLoadOnStartup;
        [ObservableProperty] private string _newOarxPropDraft = "";

        /// <summary>Module projects in LOAD order — the .dbx that owns the custom
        /// classes before the .arx that uses them. The order is the reason this
        /// form exists at all, so it is editable in place.</summary>
        public ObservableCollection<OarxModuleDraft> NewOarxModules { get; } = new();

        // Advanced: extra MSBuild properties and the three companion lists.
        // Companion order matters (they run in list order), so those rows carry
        // the same ▲▼✕ affordance as the modules.
        public ObservableCollection<OarxPathRow> NewOarxProps { get; } = new();
        public ObservableCollection<OarxPathRow> NewOarxPreloadNative { get; } = new();
        public ObservableCollection<OarxPathRow> NewOarxPreloadManaged { get; } = new();
        public ObservableCollection<OarxPathRow> NewOarxPostloadManaged { get; } = new();

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
            // Through the same seam as the palette's edit form and the MCP
            // update tool, so the manager's Source snapshot stays in sync and a
            // later config resync does not see a phantom diff. (Configuration
            // and worktree already persist through OarxManager.UpdateX.)
            if (e.PropertyName == nameof(OarxPluginItemViewModel.LoadOnStartup)
                && sender is OarxPluginItemViewModel vm)
                OarxConfigLoader.UpdatePlugin(
                    vm.Name, new OarxPluginPatch(LoadOnStartup: vm.LoadOnStartup));
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

            ResetOarxForm();
            NewOarxSolution = dialog.FileName;
            IsAddingOarx = true;
        }

        [RelayCommand]
        private void ShowEditOarx(string name)
        {
            // Pre-fill from disk, not from the card — plugins.json is the
            // authority and may carry edits the card's entry object predates.
            var entry = (PluginConfigLoader.Load()?.OarxPlugins ?? new())
                .FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return;

            ResetOarxForm();
            EditingOarxName = entry.Name;
            IsEditingLoadedOarx = OarxManager.IsLoaded(entry.Name);
            NewOarxSolution = entry.SolutionFilePath;
            NewOarxName = entry.Name;
            NewOarxPrefix = entry.CommandPrefix ?? "";
            NewOarxLoadOnStartup = entry.LoadOnStartup;
            foreach (var p in entry.ProjectFilePaths)
                NewOarxModules.Add(new OarxModuleDraft(p));
            foreach (var p in entry.MsBuildProperties)
                NewOarxProps.Add(new OarxPathRow(NewOarxProps, p));
            foreach (var p in entry.PreloadNativeModules)
                NewOarxPreloadNative.Add(new OarxPathRow(NewOarxPreloadNative, p));
            foreach (var p in entry.PreloadManagedAssemblies)
                NewOarxPreloadManaged.Add(new OarxPathRow(NewOarxPreloadManaged, p));
            foreach (var p in entry.PostloadManagedAssemblies)
                NewOarxPostloadManaged.Add(new OarxPathRow(NewOarxPostloadManaged, p));
            IsAddingOarx = true;
        }

        private void ResetOarxForm()
        {
            EditingOarxName = null;
            IsEditingLoadedOarx = false;
            NewOarxSolution = "";
            NewOarxName = "";
            NewOarxPrefix = "";
            NewOarxLoadOnStartup = false;
            NewOarxPropDraft = "";
            NewOarxModules.Clear();
            NewOarxProps.Clear();
            NewOarxPreloadNative.Clear();
            NewOarxPreloadManaged.Clear();
            NewOarxPostloadManaged.Clear();
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

        // ── Advanced rows ────────────────────────────────────────────

        [RelayCommand]
        private void AddOarxProp()
        {
            string p = NewOarxPropDraft.Trim();
            if (p.Length == 0) return;
            if (p.IndexOf('=') <= 0)
            {
                System.Windows.MessageBox.Show(
                    $"'{p}' is not Name=Value.", "MSBuild property",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }
            if (!NewOarxProps.Any(r => r.Value.Equals(p, StringComparison.OrdinalIgnoreCase)))
                NewOarxProps.Add(new OarxPathRow(NewOarxProps, p));
            NewOarxPropDraft = "";
        }

        [RelayCommand]
        private void AddOarxPreloadNative() =>
            PickCompanions(NewOarxPreloadNative,
                "Select native DLL(s) to pin before the modules load");

        [RelayCommand]
        private void AddOarxPreloadManaged() =>
            PickCompanions(NewOarxPreloadManaged,
                "Select managed assembly(ies) to load before the modules");

        [RelayCommand]
        private void AddOarxPostloadManaged() =>
            PickCompanions(NewOarxPostloadManaged,
                "Select managed assembly(ies) to load after the modules");

        private void PickCompanions(ObservableCollection<OarxPathRow> target, string title)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = title,
                Filter = "DLL (*.dll)|*.dll",
                CheckFileExists = true,
                Multiselect = true,
            };
            if (dialog.ShowDialog() != true) return;

            foreach (var file in dialog.FileNames)
                if (!target.Any(r => r.Value.Equals(file, StringComparison.OrdinalIgnoreCase)))
                    target.Add(new OarxPathRow(target, file));
        }

        [RelayCommand]
        private void MoveOarxRowUp(OarxPathRow row) => row.MoveUp();

        [RelayCommand]
        private void MoveOarxRowDown(OarxPathRow row) => row.MoveDown();

        [RelayCommand]
        private void RemoveOarxRow(OarxPathRow row) => row.Remove();

        // ── Confirm / Cancel ─────────────────────────────────────────

        [RelayCommand]
        private void ConfirmAddOarx()
        {
            var ed = Autodesk.AutoCAD.ApplicationServices.Application
                .DocumentManager.MdiActiveDocument?.Editor;

            bool ok;
            string message;
            if (EditingOarxName == null)
            {
                // Same single entry point the MCP surface uses: validates, writes
                // plugins.json, builds the live registration and its LOAD/DEV/UNLOAD
                // commands — which raises Registered, and OnOarxRegistered adds the card.
                var result = OarxConfigLoader.RegisterNewPlugin(
                    NewOarxSolution,
                    NewOarxModules.Select(m => m.ProjectPath).ToList(),
                    buildConfiguration: "Debug",
                    name: string.IsNullOrWhiteSpace(NewOarxName) ? null : NewOarxName,
                    commandPrefix: string.IsNullOrWhiteSpace(NewOarxPrefix) ? null : NewOarxPrefix,
                    loadOnStartup: NewOarxLoadOnStartup,
                    msbuildProperties: NewOarxProps.Select(r => r.Value).ToList(),
                    preloadNativeModules: NewOarxPreloadNative.Select(r => r.Value).ToList(),
                    preloadManagedAssemblies: NewOarxPreloadManaged.Select(r => r.Value).ToList(),
                    postloadManagedAssemblies: NewOarxPostloadManaged.Select(r => r.Value).ToList());
                ok = result.Success;
                message = result.Message;
            }
            else
            {
                // The edit seam, shared with the MCP update tool. Empty lists
                // CLEAR — exactly what an emptied form section means.
                var result = OarxConfigLoader.UpdatePlugin(EditingOarxName, new OarxPluginPatch(
                    CommandPrefix: string.IsNullOrWhiteSpace(NewOarxPrefix)
                        ? EditingOarxName : NewOarxPrefix,
                    LoadOnStartup: NewOarxLoadOnStartup,
                    ProjectFilePaths: NewOarxModules.Select(m => m.ProjectPath).ToList(),
                    MsBuildProperties: NewOarxProps.Select(r => r.Value).ToList(),
                    PreloadNativeModules: NewOarxPreloadNative.Select(r => r.Value).ToList(),
                    PreloadManagedAssemblies: NewOarxPreloadManaged.Select(r => r.Value).ToList(),
                    PostloadManagedAssemblies: NewOarxPostloadManaged.Select(r => r.Value).ToList()));
                ok = result.Success;
                message = result.Message;
                if (ok) RefreshOarxCardFromDisk(EditingOarxName);
            }

            if (!ok)
            {
                ed?.WriteMessage($"\n{OarxFormTitle} failed: {message}");
                System.Windows.MessageBox.Show(
                    message, OarxFormTitle,
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            if (EditingOarxName != null)
                ed?.WriteMessage($"\n{EditingOarxName}: {message}");
            IsAddingOarx = false;
            ResetOarxForm();
        }

        /// <summary>Rebuild one card from the entry as it now sits on disk —
        /// after an edit the card's old entry object is stale.</summary>
        private void RefreshOarxCardFromDisk(string name)
        {
            var entry = (PluginConfigLoader.Load()?.OarxPlugins ?? new())
                .FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return;

            int cfg = _config.OarxPlugins.FindIndex(
                p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (cfg >= 0) _config.OarxPlugins[cfg] = entry;

            var vm = OarxPlugins.FirstOrDefault(
                p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (vm == null) return;
            int i = OarxPlugins.IndexOf(vm);
            vm.PropertyChanged -= OnOarxPropertyChanged;
            var fresh = new OarxPluginItemViewModel(entry);
            fresh.PropertyChanged += OnOarxPropertyChanged;
            fresh.RefreshState();
            OarxPlugins[i] = fresh;
        }

        [RelayCommand]
        private void CancelAddOarx()
        {
            IsAddingOarx = false;
            ResetOarxForm();
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

    /// <summary>One row of the "add OARX group" form's ordered module list.
    /// A type rather than a bare path string so the row can show the project
    /// name while the reorder commands still carry the full path.</summary>
    public sealed class OarxModuleDraft
    {
        public OarxModuleDraft(string projectPath) => ProjectPath = projectPath;

        public string ProjectPath { get; }
        public string ProjectName => Path.GetFileNameWithoutExtension(ProjectPath);
    }

    /// <summary>One row of an Advanced list (an MSBuild property or a companion
    /// path). Knows its owning collection so the form's single set of
    /// move/remove commands works across all four lists.</summary>
    public sealed class OarxPathRow
    {
        private readonly ObservableCollection<OarxPathRow> _owner;

        public OarxPathRow(ObservableCollection<OarxPathRow> owner, string value)
        {
            _owner = owner;
            Value = value;
        }

        public string Value { get; }

        /// <summary>File name for a path, the raw value for a Name=Value
        /// property — what the row shows; the tooltip carries the full value.</summary>
        public string Display =>
            Value.Contains('\\') || Value.Contains('/') ? Path.GetFileName(Value) : Value;

        public void MoveUp()
        {
            int i = _owner.IndexOf(this);
            if (i > 0) _owner.Move(i, i - 1);
        }

        public void MoveDown()
        {
            int i = _owner.IndexOf(this);
            if (i >= 0 && i < _owner.Count - 1) _owner.Move(i, i + 1);
        }

        public void Remove() => _owner.Remove(this);
    }
}
