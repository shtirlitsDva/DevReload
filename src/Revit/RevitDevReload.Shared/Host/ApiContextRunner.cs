using System;
using System.Collections.Concurrent;
using System.Threading;

using Autodesk.Revit.UI;

namespace RevitDevReload
{
    // The one bridge into Revit API context. Callers from any thread enqueue
    // work and Raise(); Revit invokes Execute on the UI thread in API context
    // when idle. Run() blocks the caller until its item completed — that is
    // what the pipe surface needs (request/response semantics).
    public sealed class ApiContextRunner : IExternalEventHandler
    {
        private sealed class WorkItem
        {
            public WorkItem(Action<UIApplication> action)
            {
                Action = action;
                Done = new ManualResetEventSlim(false);
            }

            public Action<UIApplication> Action { get; }
            public ManualResetEventSlim Done { get; }
            public Exception? Error { get; set; }
        }

        private readonly ConcurrentQueue<WorkItem> _queue = new();
        private ExternalEvent? _event;

        // Must run in API context (OnStartup) — ExternalEvent.Create throws
        // anywhere else.
        public void Attach()
        {
            _event = ExternalEvent.Create(this);
        }

        public bool IsAttached => _event != null;

        public void Run(Action<UIApplication> work, int timeoutMs = 60000)
        {
            if (_event == null)
                throw new InvalidOperationException("ApiContextRunner not attached.");

            var item = new WorkItem(work);
            _queue.Enqueue(item);
            _event.Raise();

            if (!item.Done.Wait(timeoutMs))
                throw new TimeoutException(
                    $"Timed out after {timeoutMs} ms waiting for Revit API context " +
                    "(is a modal dialog or long command blocking Revit?).");

            if (item.Error != null)
                throw new InvalidOperationException(item.Error.Message, item.Error);
        }

        public T Run<T>(Func<UIApplication, T> work, int timeoutMs = 60000)
        {
            T result = default!;
            Run(app => { result = work(app); }, timeoutMs);
            return result;
        }

        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out var item))
            {
                try
                {
                    item.Action(app);
                }
                catch (Exception ex)
                {
                    item.Error = ex;
                }
                finally
                {
                    item.Done.Set();
                }
            }
        }

        public string GetName() => "RevitDevReload.ApiContextRunner";
    }
}
