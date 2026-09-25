using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Windows;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DevReload.Core;
using DevReload.Oarx;

namespace DevReload.ViewModels
{
    /// <summary>
    /// The OARX group window: the group's own fields at the top, its profiles
    /// on the left, the selected profile's editor on the right.
    /// </summary>
    /// <remarks>
    /// Edits are drafts until Save. Save goes through the same
    /// <see cref="OarxConfigLoader"/> methods the MCP tools use, so the window
    /// holds no rules of its own about what a valid group or profile is — it
    /// shows the loader's refusal instead. After every successful write the
    /// window re-reads plugins.json, so what it shows is what was saved.
    /// </remarks>
    public partial class OarxGroupEditorViewModel : ObservableObject
    {
        private const string StartEmpty = "(start empty)";

        /// <summary>Only set while creating a group: the solution picked in the palette.</summary>
        private readonly string? _newSolutionPath;

        private OarxPluginEntry? _entry;
        private bool _loading;

        // ── Group ────────────────────────────────────────────────────

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Title))]
        [NotifyPropertyChangedFor(nameof(IsExistingGroup))]
        [NotifyCanExecuteChangedFor(nameof(ShowNewProfileCommand))]
        [NotifyCanExecuteChangedFor(nameof(RemoveMissingCommand))]
        [NotifyCanExecuteChangedFor(nameof(MakeActiveCommand))]
        private bool _isNewGroup;

        [ObservableProperty] [NotifyPropertyChangedFor(nameof(Title))] private string _groupName = "";
        [ObservableProperty] private string _commandPrefix = "";
        [ObservableProperty] private bool _loadOnStartup;
        [ObservableProperty] private string _solutionDisplay = "";

        public string Title => IsNewGroup ? "New OARX group" : $"OARX group — {GroupName}";
        public bool IsExistingGroup => !IsNewGroup;

        partial void OnCommandPrefixChanged(string value) => MarkDirty();
        partial void OnLoadOnStartupChanged(bool value) => MarkDirty();
        partial void OnGroupNameChanged(string value) { if (IsNewGroup) MarkDirty(); }

        // ── Profiles ─────────────────────────────────────────────────

        public ObservableCollection<OarxProfileListItem> ProfileList { get; } = new();

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(MakeActiveCommand))]
        [NotifyCanExecuteChangedFor(nameof(DeleteProfileCommand))]
        private OarxProfileListItem? _selectedListItem;

        [ObservableProperty] private OarxProfileDraft? _draft;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsClean))]
        [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
        [NotifyCanExecuteChangedFor(nameof(DiscardCommand))]
        [NotifyCanExecuteChangedFor(nameof(MakeActiveCommand))]
        [NotifyCanExecuteChangedFor(nameof(ShowNewProfileCommand))]
        private bool _isDirty;

        /// <summary>The profile list is locked while there are unsaved edits —
        /// Save or Discard first. Simpler than asking on every click, and no
        /// edit is ever lost silently.</summary>
        public bool IsClean => !IsDirty;

        [ObservableProperty] private string _statusMessage = "";

        // ── New-profile panel ────────────────────────────────────────

        [ObservableProperty] private bool _isCreatingProfile;
        public ObservableCollection<WorktreeItem> Worktrees { get; } = new();
        [ObservableProperty] private WorktreeItem? _selectedWorktree;
        [ObservableProperty] private string _newProfileFolder = "";
        [ObservableProperty] private string _newProfileName = "";
        public ObservableCollection<string> CopyFromChoices { get; } = new();
        [ObservableProperty] private string _selectedCopyFrom = StartEmpty;

        partial void OnSelectedWorktreeChanged(WorktreeItem? value)
        {
            if (value != null) SetNewProfileFolder(value.Path);
        }

        private OarxGroupEditorViewModel(string? newSolutionPath) =>
            _newSolutionPath = newSolutionPath;

        /// <summary>Create mode: a group that does not exist yet, with one draft
        /// profile for the folder the solution sits in.</summary>
        public static OarxGroupEditorViewModel ForNewGroup(string solutionFilePath)
        {
            string folder = OarxConfigLoader.FolderForSolution(solutionFilePath);
            var vm = new OarxGroupEditorViewModel(solutionFilePath) { _loading = true };
            vm.IsNewGroup = true;
            vm.SolutionDisplay = Path.GetRelativePath(folder, solutionFilePath);
            var draft = new OarxProfileDraft(OarxConfigLoader.FolderName(folder), folder, isNew: true);
            vm.ShowDraft(draft);
            vm.ProfileList.Add(new OarxProfileListItem(draft.Name, folder, isActive: true, isMissing: false, isDraft: true));
            vm.SelectedListItem = vm.ProfileList[0];
            vm._loading = false;
            vm.IsDirty = true;
            return vm;
        }

        /// <summary>Edit mode for a registered group; null if it is not in plugins.json.</summary>
        public static OarxGroupEditorViewModel? ForGroup(string name)
        {
            var vm = new OarxGroupEditorViewModel(null);
            return vm.ReloadFromDisk(name, select: null) ? vm : null;
        }

        // ── Loading ──────────────────────────────────────────────────

        private bool ReloadFromDisk(string name, string? select)
        {
            var entry = (PluginConfigLoader.Load()?.OarxPlugins ?? new())
                .FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return false;

            _loading = true;
            try
            {
                _entry = entry;
                IsNewGroup = false;
                GroupName = entry.Name;
                CommandPrefix = entry.CommandPrefix ?? "";
                LoadOnStartup = entry.LoadOnStartup;
                SolutionDisplay = entry.Solution;

                ProfileList.Clear();
                foreach (var p in entry.Profiles)
                    ProfileList.Add(new OarxProfileListItem(
                        p.Name, p.WorktreePath,
                        isActive: p.Name.Equals(entry.ActiveProfile, StringComparison.OrdinalIgnoreCase),
                        isMissing: !p.FolderExists,
                        isDraft: false));

                SelectedListItem =
                    ProfileList.FirstOrDefault(i => i.Name.Equals(select ?? "", StringComparison.OrdinalIgnoreCase))
                    ?? ProfileList.FirstOrDefault(i => i.IsActive)
                    ?? ProfileList.FirstOrDefault();
                ShowSelected();
            }
            finally { _loading = false; }
            IsDirty = false;
            return true;
        }

        partial void OnSelectedListItemChanged(OarxProfileListItem? value)
        {
            if (_loading) return;
            ShowSelected();
        }

        private void ShowSelected()
        {
            var profile = _entry?.FindProfile(SelectedListItem?.Name);
            if (profile == null || SelectedListItem!.IsDraft) return;
            ShowDraft(OarxProfileDraft.From(profile));
        }

        private void ShowDraft(OarxProfileDraft draft)
        {
            Draft = draft;
            draft.Changed += MarkDirty;
        }

        private void MarkDirty()
        {
            if (!_loading) IsDirty = true;
        }

        // ── Module and companion editing ─────────────────────────────

        [RelayCommand]
        private void AddModule()
        {
            if (Draft == null) return;
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select the ObjectARX module project(s) — .dbx before .arx",
                Filter = "C++ project (*.vcxproj)|*.vcxproj",
                CheckFileExists = true,
                Multiselect = true,
                InitialDirectory = Draft.WorktreePath,
            };
            if (dialog.ShowDialog() != true) return;

            var outside = new List<string>();
            foreach (var file in dialog.FileNames)
            {
                string? rel = OarxConfigLoader.ToProfileRelative(Draft.WorktreePath, file);
                if (rel == null) { outside.Add(file); continue; }
                if (Draft.Modules.Any(m => m.RelativePath.Equals(rel, StringComparison.OrdinalIgnoreCase)))
                    continue;
                Draft.Modules.Add(new OarxModuleDraft(Draft.Modules, rel, Draft.WorktreePath));
            }

            if (IsNewGroup && string.IsNullOrWhiteSpace(GroupName) && Draft.Modules.Count > 0)
                GroupName = Draft.Modules[Draft.Modules.Count - 1].ProjectName;

            if (outside.Count > 0)
                Warn("Module projects must be inside the profile's folder, so the profile can " +
                     "be copied to another worktree. Skipped:\n\n" + string.Join("\n", outside));
        }

        [RelayCommand]
        private void AddProp()
        {
            if (Draft == null) return;
            string p = Draft.PropDraft.Trim();
            if (p.Length == 0) return;
            if (p.IndexOf('=') <= 0) { Warn($"'{p}' is not Name=Value."); return; }
            if (!Draft.Props.Any(r => r.Value.Equals(p, StringComparison.OrdinalIgnoreCase)))
                Draft.Props.Add(new OarxPathRow(Draft.Props, p));
            Draft.PropDraft = "";
        }

        [RelayCommand]
        private void AddPreloadNative() =>
            PickCompanions(Draft?.PreloadNative, "Select native DLL(s) to pin before the modules load");

        [RelayCommand]
        private void AddPreloadManaged() =>
            PickCompanions(Draft?.PreloadManaged, "Select managed assembly(ies) to load before the modules");

        [RelayCommand]
        private void AddPostloadManaged() =>
            PickCompanions(Draft?.PostloadManaged, "Select managed assembly(ies) to load after the modules");

        /// <summary>A companion inside the profile's folder is stored relative,
        /// so a copied profile loads ITS worktree's build of it; one elsewhere
        /// (a network share) stays absolute.</summary>
        private void PickCompanions(ObservableCollection<OarxPathRow>? target, string title)
        {
            if (Draft == null || target == null) return;
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = title,
                Filter = "DLL (*.dll)|*.dll",
                CheckFileExists = true,
                Multiselect = true,
            };
            if (dialog.ShowDialog() != true) return;

            foreach (var file in dialog.FileNames)
            {
                string stored = OarxConfigLoader.ToProfileRelative(Draft.WorktreePath, file) ?? file;
                if (!target.Any(r => r.Value.Equals(stored, StringComparison.OrdinalIgnoreCase)))
                    target.Add(new OarxPathRow(target, stored));
            }
        }

        [RelayCommand] private void MoveUp(IOrderedRow row) => row.MoveUp();
        [RelayCommand] private void MoveDown(IOrderedRow row) => row.MoveDown();
        [RelayCommand] private void RemoveRow(IOrderedRow row) => row.Remove();

        [RelayCommand]
        private void ChangeFolder()
        {
            if (Draft == null) return;
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select the worktree folder this profile builds from",
                InitialDirectory = Draft.WorktreePath,
            };
            if (dialog.ShowDialog() != true) return;
            Draft.WorktreePath = OarxConfigLoader.NormalizeFolder(dialog.FolderName);
        }

        // ── New profile ──────────────────────────────────────────────

        [RelayCommand(CanExecute = nameof(CanStartNewProfile))]
        private void ShowNewProfile()
        {
            Worktrees.Clear();
            // Any existing folder of this group leads to the repository, and git
            // lists every worktree of it — branch names play no part in picking.
            string? anyFolder = _entry?.Profiles.FirstOrDefault(p => p.FolderExists)?.WorktreePath;
            string? repoRoot = anyFolder == null ? null : GitWorktreeService.GetRepoRoot(anyFolder);
            if (repoRoot != null)
                foreach (var wt in GitWorktreeService.ListWorktrees(repoRoot))
                    Worktrees.Add(new WorktreeItem { Path = wt.Path.Replace('/', '\\'), Branch = wt.Branch, IsMain = wt.IsMain });

            CopyFromChoices.Clear();
            CopyFromChoices.Add(StartEmpty);
            foreach (var p in ProfileList.Where(i => !i.IsDraft))
                CopyFromChoices.Add(p.Name);
            SelectedCopyFrom = ProfileList.FirstOrDefault(i => i.IsActive)?.Name ?? StartEmpty;

            // Preselect the first worktree no profile uses yet.
            SelectedWorktree = Worktrees.FirstOrDefault(w => !ProfileList.Any(p =>
                p.WorktreePath.Equals(w.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)));
            if (SelectedWorktree == null) { NewProfileFolder = ""; NewProfileName = ""; }
            IsCreatingProfile = true;
        }

        private bool CanStartNewProfile() => !IsNewGroup && !IsDirty;

        [RelayCommand]
        private void BrowseNewProfileFolder()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select the worktree folder the new profile builds from",
            };
            if (dialog.ShowDialog() != true) return;
            SelectedWorktree = null;
            SetNewProfileFolder(dialog.FolderName);
        }

        private void SetNewProfileFolder(string folder)
        {
            NewProfileFolder = OarxConfigLoader.NormalizeFolder(folder);
            NewProfileName = OarxConfigLoader.FolderName(NewProfileFolder);
        }

        [RelayCommand]
        private void CancelNewProfile() => IsCreatingProfile = false;

        [RelayCommand]
        private void CreateProfile()
        {
            string name = NewProfileName.Trim();
            if (NewProfileFolder.Length == 0 || !Directory.Exists(NewProfileFolder))
            { Warn("Pick a worktree folder that exists."); return; }
            if (name.Length == 0) { Warn("Give the profile a name."); return; }
            if (ProfileList.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            { Warn($"A profile named '{name}' already exists."); return; }

            var source = SelectedCopyFrom == StartEmpty ? null : _entry?.FindProfile(SelectedCopyFrom);
            var draft = source == null
                ? new OarxProfileDraft(name, NewProfileFolder, isNew: true)
                : OarxProfileDraft.From(source.Clone(name, NewProfileFolder), isNew: true);

            var item = new OarxProfileListItem(name, NewProfileFolder, isActive: false, isMissing: false, isDraft: true);
            _loading = true;
            try
            {
                ProfileList.Add(item);
                SelectedListItem = item;
                ShowDraft(draft);
            }
            finally { _loading = false; }
            IsCreatingProfile = false;
            IsDirty = true;
            StatusMessage = source == null
                ? $"New profile '{name}' — add its modules, then Save."
                : $"New profile '{name}', copied from '{source.Name}' — Save to keep it.";
        }

        // ── Save / Discard / Activate / Delete ───────────────────────

        [RelayCommand(CanExecute = nameof(IsDirty))]
        private void Save()
        {
            if (Draft == null) return;
            var modules = Draft.Modules.Select(m => m.RelativePath).ToList();
            var props = Draft.Props.Select(r => r.Value).ToList();
            var preNative = Draft.PreloadNative.Select(r => r.Value).ToList();
            var preManaged = Draft.PreloadManaged.Select(r => r.Value).ToList();
            var postManaged = Draft.PostloadManaged.Select(r => r.Value).ToList();

            if (IsNewGroup)
            {
                var result = OarxConfigLoader.RegisterNewPlugin(
                    _newSolutionPath!,
                    modules.Select(Draft.Resolve).ToList(),
                    buildConfiguration: "Debug",
                    name: string.IsNullOrWhiteSpace(GroupName) ? null : GroupName,
                    commandPrefix: string.IsNullOrWhiteSpace(CommandPrefix) ? null : CommandPrefix,
                    loadOnStartup: LoadOnStartup,
                    msbuildProperties: props,
                    preloadNativeModules: preNative,
                    preloadManagedAssemblies: preManaged,
                    postloadManagedAssemblies: postManaged);
                if (!result.Success) { Warn(result.Message); return; }
                ReloadFromDisk(result.Name, select: null);
                StatusMessage = result.Message;
                return;
            }

            var messages = new List<string>();
            string group = _entry!.Name;
            string prefix = string.IsNullOrWhiteSpace(CommandPrefix) ? group : CommandPrefix.Trim();
            if (!prefix.Equals(_entry.CommandPrefix ?? _entry.Name, StringComparison.OrdinalIgnoreCase)
                || LoadOnStartup != _entry.LoadOnStartup)
            {
                var r = OarxConfigLoader.UpdatePlugin(group, new OarxGroupPatch(
                    CommandPrefix: prefix, LoadOnStartup: LoadOnStartup));
                if (!r.Success) { Warn(r.Message); return; }
                messages.Add(r.Message);
            }

            if (Draft.IsChanged)
            {
                var r = OarxConfigLoader.PublishProfile(new OarxProfilePublish(
                    group, Draft.WorktreePath, Draft.Name,
                    ProjectFilePaths: modules,
                    MsBuildProperties: props,
                    PreloadNativeModules: preNative,
                    PreloadManagedAssemblies: preManaged,
                    PostloadManagedAssemblies: postManaged));
                if (!r.Success) { Warn(r.Message); return; }
                messages.Add(r.Message);
            }

            ReloadFromDisk(group, select: Draft.Name);
            StatusMessage = string.Join(" · ", messages);
        }

        [RelayCommand(CanExecute = nameof(CanDiscard))]
        private void Discard()
        {
            if (_entry == null) return;
            ReloadFromDisk(_entry.Name, select: SelectedListItem?.IsDraft == true ? null : SelectedListItem?.Name);
            StatusMessage = "Changes discarded.";
        }

        private bool CanDiscard() => IsDirty && !IsNewGroup;

        [RelayCommand(CanExecute = nameof(CanMakeActive))]
        private void MakeActive()
        {
            if (_entry == null || SelectedListItem == null) return;
            var r = OarxConfigLoader.ActivateProfile(_entry.Name, SelectedListItem.Name);
            if (!r.Success) { Warn(r.Message); return; }
            ReloadFromDisk(_entry.Name, select: SelectedListItem.Name);
            StatusMessage = r.Message;
        }

        private bool CanMakeActive() =>
            !IsNewGroup && !IsDirty && SelectedListItem is { IsDraft: false, IsActive: false };

        [RelayCommand(CanExecute = nameof(CanDeleteProfile))]
        private void DeleteProfile()
        {
            if (_entry == null || SelectedListItem == null) return;
            if (SelectedListItem.IsDraft) { Discard(); return; }
            if (MessageBox.Show($"Delete the profile '{SelectedListItem.Name}'?\n\nIts worktree folder is not touched.",
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            var r = OarxConfigLoader.DeleteProfile(_entry.Name, SelectedListItem.Name);
            if (!r.Success) { Warn(r.Message); return; }
            ReloadFromDisk(_entry.Name, select: null);
            StatusMessage = r.Message;
        }

        private bool CanDeleteProfile() =>
            !IsNewGroup && SelectedListItem is { IsActive: false } item && (item.IsDraft || !IsDirty);

        [RelayCommand(CanExecute = nameof(CanRemoveMissing))]
        private void RemoveMissing()
        {
            if (_entry == null) return;
            var r = OarxConfigLoader.RemoveMissingProfiles(_entry.Name);
            if (!r.Success) { Warn(r.Message); return; }
            ReloadFromDisk(_entry.Name, select: SelectedListItem?.Name);
            StatusMessage = r.Message;
        }

        private bool CanRemoveMissing() => !IsNewGroup;

        partial void OnIsNewGroupChanged(bool value)
        {
            DeleteProfileCommand.NotifyCanExecuteChanged();
            DiscardCommand.NotifyCanExecuteChanged();
        }

        partial void OnIsDirtyChanged(bool value) => DeleteProfileCommand.NotifyCanExecuteChanged();

        /// <summary>Asked by the window before it closes.</summary>
        public bool ConfirmClose() =>
            !IsDirty || MessageBox.Show("Discard unsaved changes?", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

        private void Warn(string message)
        {
            StatusMessage = message;
            MessageBox.Show(message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>One row of the window's profile list.</summary>
    public sealed class OarxProfileListItem
    {
        public OarxProfileListItem(string name, string worktreePath, bool isActive, bool isMissing, bool isDraft)
        {
            Name = name;
            WorktreePath = worktreePath;
            IsActive = isActive;
            IsMissing = isMissing;
            IsDraft = isDraft;
        }

        public string Name { get; }
        public string WorktreePath { get; }
        public bool IsActive { get; }
        public bool IsMissing { get; }
        public bool IsDraft { get; }

        public string Marker => IsDraft ? "+" : IsMissing ? "⚠" : IsActive ? "★" : "";
        public string Note => IsDraft ? "unsaved" : IsMissing ? "folder missing" : IsActive ? "active" : "";
    }

    /// <summary>The editable copy of one profile.</summary>
    public sealed partial class OarxProfileDraft : ObservableObject
    {
        private readonly string _originalFolder;

        public OarxProfileDraft(string name, string worktreePath, bool isNew)
        {
            Name = name;
            _worktreePath = worktreePath;
            _originalFolder = worktreePath;
            IsNew = isNew;
            IsChanged = isNew;
            foreach (var c in new INotifyCollectionChanged[] { Modules, Props, PreloadNative, PreloadManaged, PostloadManaged })
                c.CollectionChanged += (_, _) => Touch();
        }

        public string Name { get; }
        public bool IsNew { get; }

        /// <summary>True once anything in the draft differs from what was loaded.</summary>
        public bool IsChanged { get; private set; }

        public event Action? Changed;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FolderMissing))]
        private string _worktreePath;

        [ObservableProperty] private string _propDraft = "";

        public bool FolderMissing => !Directory.Exists(WorktreePath);

        partial void OnWorktreePathChanged(string value)
        {
            if (!value.Equals(_originalFolder, StringComparison.OrdinalIgnoreCase)) Touch();
            foreach (var m in Modules) m.Folder = value;
        }

        public ObservableCollection<OarxModuleDraft> Modules { get; } = new();
        public ObservableCollection<OarxPathRow> Props { get; } = new();
        public ObservableCollection<OarxPathRow> PreloadNative { get; } = new();
        public ObservableCollection<OarxPathRow> PreloadManaged { get; } = new();
        public ObservableCollection<OarxPathRow> PostloadManaged { get; } = new();

        public string Resolve(string relative) =>
            Path.GetFullPath(Path.Combine(WorktreePath, relative));

        public static OarxProfileDraft From(OarxProfile p, bool isNew = false)
        {
            var d = new OarxProfileDraft(p.Name, p.WorktreePath, isNew);
            foreach (var m in p.ProjectFilePaths) d.Modules.Add(new OarxModuleDraft(d.Modules, m, p.WorktreePath));
            foreach (var x in p.MsBuildProperties) d.Props.Add(new OarxPathRow(d.Props, x));
            foreach (var x in p.PreloadNativeModules) d.PreloadNative.Add(new OarxPathRow(d.PreloadNative, x));
            foreach (var x in p.PreloadManagedAssemblies) d.PreloadManaged.Add(new OarxPathRow(d.PreloadManaged, x));
            foreach (var x in p.PostloadManagedAssemblies) d.PostloadManaged.Add(new OarxPathRow(d.PostloadManaged, x));
            d.IsChanged = isNew;
            return d;
        }

        private void Touch()
        {
            IsChanged = true;
            Changed?.Invoke();
        }
    }

    /// <summary>A row that knows its own list, so one set of ▲▼✕ commands
    /// serves every list in the window.</summary>
    public interface IOrderedRow
    {
        void MoveUp();
        void MoveDown();
        void Remove();
    }

    public abstract class OrderedRow<T> : ObservableObject, IOrderedRow where T : OrderedRow<T>
    {
        private readonly ObservableCollection<T> _owner;

        protected OrderedRow(ObservableCollection<T> owner) => _owner = owner;

        public void MoveUp()
        {
            int i = _owner.IndexOf((T)this);
            if (i > 0) _owner.Move(i, i - 1);
        }

        public void MoveDown()
        {
            int i = _owner.IndexOf((T)this);
            if (i >= 0 && i < _owner.Count - 1) _owner.Move(i, i + 1);
        }

        public void Remove() => _owner.Remove((T)this);
    }

    /// <summary>One module project in a profile draft, stored relative to the
    /// profile's folder.</summary>
    public sealed class OarxModuleDraft : OrderedRow<OarxModuleDraft>
    {
        private string _folder;

        public OarxModuleDraft(ObservableCollection<OarxModuleDraft> owner, string relativePath, string folder)
            : base(owner)
        {
            RelativePath = relativePath;
            _folder = folder;
        }

        public string RelativePath { get; }
        public string ProjectName => Path.GetFileNameWithoutExtension(RelativePath);

        /// <summary>Where the project resolves in the draft's CURRENT folder.</summary>
        public string FullPath => Path.GetFullPath(Path.Combine(_folder, RelativePath));
        public bool Exists => File.Exists(FullPath);

        internal string Folder
        {
            set
            {
                _folder = value;
                OnPropertyChanged(nameof(FullPath));
                OnPropertyChanged(nameof(Exists));
            }
        }
    }

    /// <summary>One row of an Advanced list (an MSBuild property or a companion path).</summary>
    public sealed class OarxPathRow : OrderedRow<OarxPathRow>
    {
        public OarxPathRow(ObservableCollection<OarxPathRow> owner, string value) : base(owner) => Value = value;

        public string Value { get; }

        /// <summary>File name for a path, the raw value for a Name=Value
        /// property — what the row shows; the tooltip carries the full value.</summary>
        public string Display =>
            Value.Contains('\\') || Value.Contains('/') ? Path.GetFileName(Value) : Value;
    }
}
