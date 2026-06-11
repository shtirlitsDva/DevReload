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
        private int _uiThreadId = -1;

        // Must run in API context (OnStartup) — ExternalEvent.Create throws
        // anywhere else.
        public void Attach()
        {
            _event = ExternalEvent.Create(this);
            _uiThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        public bool IsAttached => _event != null;

        public void Run(Action<UIApplication> work, int timeoutMs = 60000)
        {
            if (_event == null)
                throw new InvalidOperationException("ApiContextRunner not attached.");

            // In-context fast path: when called ON the Revit UI thread we
            // are already inside a Revit callback (Idling, OnShutdown, a
            // command) — raising an ExternalEvent and blocking would
            // deadlock, because Revit cannot return to idle while this
            // thread waits. Execute inline instead.
            if (Thread.CurrentThread.ManagedThreadId == _uiThreadId)
            {
                var uiApp = RevitContext.UiApp
                    ?? throw new InvalidOperationException(
                        "On the Revit UI thread but no UIApplication captured " +
                        "yet (expected from Idling/command/ribbon callbacks).");
                work(uiApp);
                return;
            }

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
            RevitContext.UiApp = app;
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
