using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Data;

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using EventManager;

namespace DevReloadTest.ViewModels
{
    public partial class EventTestViewModel : ObservableObject
    {
        private readonly AcadEventManager _events;

        private readonly Dictionary<Document, HashSet<string>> _activeEvents = new();

        [ObservableProperty] private string _activeDocName = "(none)";
        [ObservableProperty] private bool _cmdEndedActive;
        [ObservableProperty] private bool _objAppendedActive;
        [ObservableProperty] private string _subscriptionSummary = "No subscriptions";

        public ObservableCollection<string> Log { get; } = new();

        public EventTestViewModel(AcadEventManager events)
        {
            _events = events;

            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc != null)
                ActiveDocName = Path.GetFileName(doc.Name);

            Application.DocumentManager.DocumentBecameCurrent += OnDocBecameCurrent;
            _events.Track(doc!, () =>
                Application.DocumentManager.DocumentBecameCurrent -= OnDocBecameCurrent);

            RefreshButtonStates();
        }

        private static void OnDocBecameCurrent(object sender, DocumentCollectionEventArgs e)
        {
            _instance?.HandleDocChanged(e.Document);
        }

        private void HandleDocChanged(Document doc)
        {
            ActiveDocName = doc != null ? Path.GetFileName(doc.Name) : "(none)";
            RefreshButtonStates();
        }

        private static EventTestViewModel? _instance;

        partial void OnActiveDocNameChanged(string value) { }

        [RelayCommand]
        private void SubscribeCommandEnded()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var active = GetActiveEvents(doc);
            if (active.Contains("CommandEnded")) return;

            doc.CommandEnded += OnCommandEnded;
            _events.Track(doc, () =>
            {
                doc.CommandEnded -= OnCommandEnded;
                GetActiveEvents(doc).Remove("CommandEnded");
            });
            active.Add("CommandEnded");

            AddLog($"Subscribed CommandEnded on {Path.GetFileName(doc.Name)}");
            RefreshButtonStates();
            RefreshSummary();
        }

        [RelayCommand]
        private void SubscribeObjectAppended()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var active = GetActiveEvents(doc);
            if (active.Contains("ObjectAppended")) return;

            doc.Database.ObjectAppended += OnObjectAppended;
            _events.Track(doc, () =>
            {
                doc.Database.ObjectAppended -= OnObjectAppended;
                GetActiveEvents(doc).Remove("ObjectAppended");
            });
            active.Add("ObjectAppended");

            AddLog($"Subscribed ObjectAppended on {Path.GetFileName(doc.Name)}");
            RefreshButtonStates();
            RefreshSummary();
        }

        [RelayCommand]
        private void ClearLog()
        {
            Log.Clear();
        }

        private static void OnCommandEnded(object sender, CommandEventArgs e)
        {
            _instance?.AddLog($"CommandEnded: {e.GlobalCommandName} ({TestVersion.Tag})");
        }

        private static void OnObjectAppended(object sender, ObjectEventArgs e)
        {
            string typeName = e.DBObject.GetType().Name;
            _instance?.AddLog($"ObjectAppended: {typeName} ({TestVersion.Tag})");
        }

        private HashSet<string> GetActiveEvents(Document doc)
        {
            if (!_activeEvents.TryGetValue(doc, out var set))
            {
                set = new HashSet<string>();
                _activeEvents[doc] = set;
            }
            return set;
        }

        private void RefreshButtonStates()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                CmdEndedActive = false;
                ObjAppendedActive = false;
                return;
            }
            var active = GetActiveEvents(doc);
            CmdEndedActive = active.Contains("CommandEnded");
            ObjAppendedActive = active.Contains("ObjectAppended");
        }

        private void RefreshSummary()
        {
            var lines = _activeEvents
                .Where(kv => kv.Value.Count > 0)
                .Select(kv => $"{Path.GetFileName(kv.Key.Name)}: {string.Join(", ", kv.Value)}")
                .ToList();

            SubscriptionSummary = lines.Count > 0
                ? string.Join("\n", lines)
                : "No subscriptions";
        }

        private void AddLog(string msg)
        {
            string entry = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            Log.Add(entry);
            while (Log.Count > 100) Log.RemoveAt(0);
        }

        public void Activate()
        {
            _instance = this;
        }
    }

    public class InvertBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b ? !b : value;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b ? !b : value;
    }
}
