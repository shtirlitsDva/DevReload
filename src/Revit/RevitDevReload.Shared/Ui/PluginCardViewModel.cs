using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DevReload.Core;

using RevitDevReload.Core;

namespace RevitDevReload.Ui
{
    public partial class PluginCardViewModel : ObservableObject
    {
        public string Name { get; }

        [ObservableProperty]
        private bool _isLoaded;

        [ObservableProperty]
        private bool _isDebug = true;

        [ObservableProperty]
        private bool _loadOnStartup;

        [ObservableProperty]
        private bool _isBusy;

        public ObservableCollection<string> Worktrees { get; } = new();

        [ObservableProperty]
        private string? _selectedWorktree;

        public ObservableCollection<DiscoveredCommand> Commands { get; } = new();

        public PluginCardViewModel(RevitPluginRegistration reg)
        {
            Name = reg.Entry.Name;
            Refresh(reg);
        }

        public void Refresh(RevitPluginRegistration reg)
        {
            IsLoaded = reg.IsLoaded;
            IsDebug = !string.Equals(
                reg.Entry.BuildConfiguration, "Release", StringComparison.OrdinalIgnoreCase);
            LoadOnStartup = reg.Entry.LoadOnStartup;
            SelectedWorktree = reg.Entry.ActiveWorktreePath;

            Commands.Clear();
            foreach (var cmd in reg.Commands)
                Commands.Add(cmd);
        }

        partial void OnIsDebugChanged(bool value)
        {
            var reg = RevitPluginManager.Get(Name);
            if (reg == null) return;
            reg.Entry.BuildConfiguration = value ? "Debug" : "Release";
        }

        partial void OnLoadOnStartupChanged(bool value)
        {
            var reg = RevitPluginManager.Get(Name);
            if (reg == null) return;
            reg.Entry.LoadOnStartup = value;
        }

        partial void OnSelectedWorktreeChanged(string? value)
        {
            var reg = RevitPluginManager.Get(Name);
            if (reg == null) return;
            reg.Entry.ActiveWorktreePath =
                string.IsNullOrEmpty(value) || value == MainCheckoutLabel ? null : value;
        }

        private const string MainCheckoutLabel = "(main)";

        public void RefreshWorktrees()
        {
            var reg = RevitPluginManager.Get(Name);
            if (reg?.Entry.ProjectFilePath == null) return;

            string? projectDir = Path.GetDirectoryName(reg.Entry.ProjectFilePath);
            if (projectDir == null) return;
            string? repoRoot = GitWorktreeService.GetRepoRoot(projectDir);
            if (repoRoot == null) return;

            var current = SelectedWorktree;
            Worktrees.Clear();
            Worktrees.Add(MainCheckoutLabel);
            foreach (var wt in GitWorktreeService.ListWorktrees(repoRoot).Where(w => !w.IsMain))
                Worktrees.Add(wt.Path);
            SelectedWorktree = current ?? MainCheckoutLabel;
        }

        [RelayCommand]
        private async Task Reload()
        {
            // Off the UI thread: the manager blocks on an ExternalEvent that
            // can only fire when the UI thread is idle.
            IsBusy = true;
            try { await Task.Run(() => RevitPluginManager.Reload(Name)); }
            finally { IsBusy = false; }
        }

        [RelayCommand]
        private async Task Load()
        {
            IsBusy = true;
            try { await Task.Run(() => RevitPluginManager.Load(Name)); }
            finally { IsBusy = false; }
        }

        [RelayCommand]
        private async Task Unload()
        {
            IsBusy = true;
            try { await Task.Run(() => RevitPluginManager.Unload(Name)); }
            finally { IsBusy = false; }
        }

        [RelayCommand]
        private async Task RunCommand(DiscoveredCommand command)
        {
            await Task.Run(() =>
                RevitPluginManager.RunCommand(Name, command.FullClassName));
        }
    }
}
