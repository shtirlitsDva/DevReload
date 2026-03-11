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
            if (e.PropertyName is nameof(PluginItemViewModel.LoadOnStartup)
                                  or nameof(PluginItemViewModel.IsReleaseBuild))
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

            string selection = IntersectUtilities.StringGridFormCaller.Call(
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

            if (_config.Plugins.Any(p =>
                    p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return;

            var entry = new PluginEntry
            {
                Name = name,
                CommandPrefix = string.IsNullOrWhiteSpace(NewPluginPrefix)
                    ? null : NewPluginPrefix.Trim().ToUpperInvariant(),
                DllPath = _selectedProject.DebugDllPath,
                VsProject = _selectedProject.Name,
                LoadOnStartup = NewPluginLoadOnStartup,
            };

            _config.Plugins.Add(entry);
            SaveConfig();

            // Register with PluginManager + create LOAD/DEV/UNLOAD commands
            DevReloaderCommands.RegisterFromConfig(entry);

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

            // Unregister from PluginManager (tears down + removes commands)
            PluginManager.Unregister(name);

            // Remove from config + save
            _config.Plugins.RemoveAll(e =>
                e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            SaveConfig();

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

            string pluginDir = !string.IsNullOrEmpty(entry.DllPath)
                ? Path.GetDirectoryName(entry.DllPath)!
                : "";

            if (string.IsNullOrEmpty(pluginDir) || !Directory.Exists(pluginDir))
            {
                System.Windows.MessageBox.Show(
                    $"Plugin directory not found:\n{pluginDir}",
                    "Shared Assemblies",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            var vm = new SharedAssembliesViewModel(pluginDir, entry.SharedAssemblies, entry.MixedModeAssemblies);
            var win = new SharedAssembliesWindow { DataContext = vm };

            if (win.ShowDialog() == true && vm.Saved)
            {
                string[] selected = vm.GetSelectedNames();
                string[] mixedMode = vm.GetMixedModeNames();
                entry.SharedAssemblies = selected.ToList();
                entry.MixedModeAssemblies = mixedMode.ToList();
                SaveConfig();
                PluginManager.UpdateSharedAssemblies(name, selected);
                PluginManager.UpdateMixedModeAssemblies(name, mixedMode);
            }
        }

        [RelayCommand]
        private void PushToProduction(string name)
        {
            var entry = _config.Plugins.FirstOrDefault(
                p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return;

            if (entry.SharedAssemblies.Count == 0)
            {
                System.Windows.MessageBox.Show(
                    "No shared assemblies configured for this plugin.\n" +
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
            string selection = IntersectUtilities.StringGridFormCaller.Call(
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

            // Write SharedAssemblies.Config.json to the target app's directory
            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            };
            var configObj = new { sharedAssemblies = entry.SharedAssemblies, mixedModeAssemblies = entry.MixedModeAssemblies };
            string json = JsonSerializer.Serialize(configObj, jsonOptions);
            string configPath = Path.Combine(target.DllDir, "SharedAssemblies.Config.json");
            File.WriteAllText(configPath, json);

            // Remember which production app this plugin targets
            entry.ProductionTarget = selection;
            SaveConfig();

            System.Windows.MessageBox.Show(
                $"SharedAssemblies.Config.json written to:\n{target.DllDir}\n\n" +
                $"Target app: {selection}\n" +
                $"Assemblies: {string.Join(", ", entry.SharedAssemblies)}",
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

        public void RefreshState()
        {
            IsLoaded = PluginManager.IsRegistered(Name) && PluginManager.IsLoaded(Name);
            Status = IsLoaded ? "Loaded" : "Unloaded";
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
