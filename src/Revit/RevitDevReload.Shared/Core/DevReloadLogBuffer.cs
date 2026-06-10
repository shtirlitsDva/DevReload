using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitDevReload.Core
{
    // One in-memory log feeding both the window's log pane and the pipe's
    // get_log command — single sink, two views.
    public static class DevReloadLogBuffer
    {
        private const int Capacity = 2000;
        private static readonly Queue<string> _lines = new();
        private static readonly object _lock = new();

        public static event Action<string>? LineAdded;

        public static void Add(string message)
        {
            string line = $"{DateTime.Now:HH:mm:ss} {message}";
            lock (_lock)
            {
                _lines.Enqueue(line);
                while (_lines.Count > Capacity)
                    _lines.Dequeue();
            }
            LineAdded?.Invoke(line);
        }

        public static IReadOnlyList<string> Snapshot(int tail = 200)
        {
            lock (_lock)
            {
                return _lines.Reverse().Take(tail).Reverse().ToList();
            }
        }
    }
}
