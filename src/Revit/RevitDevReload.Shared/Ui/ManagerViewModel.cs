using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DevReload.Core;

using RevitDevReload.Core;

namespace RevitDevReload.Ui
{
    public partial class ManagerViewModel : ObservableObject
    {
        private readonly Dispatcher _dispatcher;

        public ObservableCollection<PluginCardViewModel> Plugins { get; } = new();
        public ObservableCollection<string> LogLines { get; } = new();

        public bool HasPlugins => Plugins.Count > 0;

        [ObservableProperty]
        private bool _isLogOpen;

        public string Title =>
            $"DevReload — Revit {RevitContext.RevitVersionYear}" +
            (RevitPluginManager.SupportsTrueUnload ? "" : "  (legacy loader: no true unload)");

        public ManagerViewModel()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;

            foreach (var line in DevReloadLogBuffer.Snapshot())
                LogLines.Add(line);

            RevitPluginManager.PluginsChanged += OnPluginsChanged;
            DevReloadLogBuffer.LineAdded += OnLogLine;

            RebuildCards();
        }

        public void Detach()
        {
            RevitPluginManager.PluginsChanged -= OnPluginsChanged;
            DevReloadLogBuffer.LineAdded -= OnLogLine;
        }

        private void OnPluginsChanged()
        {
            _dispatcher.BeginInvoke(new Action(RebuildCards));
        }

        private void OnLogLine(string line)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                LogLines.Add(line);
                while (LogLines.Count > 500) LogLines.RemoveAt(0);
            }));
        }

        private void RebuildCards()
        {
            var regs = RevitPluginManager.All;

            // Update existing cards in place, add new, drop removed — keeps
            // combo dropdown state stable across refreshes.
            foreach (var reg in regs)
            {
                var card = Plugins.FirstOrDefault(p => p.Name == reg.Entry.Name);
                if (card == null)
                {
                    card = new PluginCardViewModel(reg);
                    card.RefreshWorktrees();
                    Plugins.Add(card);
                }
                else
                {
                    card.Refresh(reg);
                }
            }
            foreach (var stale in Plugins
                         .Where(p => regs.All(r => r.Entry.Name != p.Name)).ToList())
                Plugins.Remove(stale);

            OnPropertyChanged(nameof(HasPlugins));
        }

        [RelayCommand]
        private async Task AddPlugin()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select plugin project (.csproj)",
                Filter = "C# project (*.csproj)|*.csproj",
            };
            if (dialog.ShowDialog() != true) return;

            string csproj = dialog.FileName;
            string name = Path.GetFileNameWithoutExtension(csproj);

            await Task.Run(() =>
            {
                string? platform = BuildService.IsSdkStyle(csproj) ? "x64" : null;
                string? dllPath = BuildService.QueryMsBuildProperty(
                    csproj, "TargetPath", "Debug", platform);
                RevitPluginManager.Register(new RevitPluginEntry
                {
                    Name = name,
                    ProjectFilePath = csproj,
                    DllPath = dllPath,
                    BuildConfiguration = "Debug",
                });
            });
        }

        [RelayCommand]
        private void RemovePlugin(string name)
        {
            Task.Run(() => RevitPluginManager.Unregister(name));
        }

        [RelayCommand]
        private void ToggleLog() => IsLogOpen = !IsLogOpen;

        [RelayCommand]
        private void ClearLog() => LogLines.Clear();
    }
}
