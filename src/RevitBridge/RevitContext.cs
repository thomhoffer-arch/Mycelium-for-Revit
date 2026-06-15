using System;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Loam.Revit.Connector.RevitBridge
{
    /// Marshals work onto Revit's UI thread via ExternalEvent — the Revit API is
    /// not thread-safe and tool calls arrive on HttpListener worker threads.
    public class RevitContext
    {
        private readonly UIControlledApplication _app;
        private readonly Handler _handler = new Handler();
        private readonly ExternalEvent _event;
        private UIApplication _uiApp;

        public RevitContext(UIControlledApplication app)
        {
            _app = app;
            _event = ExternalEvent.Create(_handler);
            app.ControlledApplication.ApplicationInitialized += (s, e) =>
            {
                _uiApp = new UIApplication(((Autodesk.Revit.ApplicationServices.Application)s));
            };
        }

        public T Run<T>(Func<Document, T> work, int timeoutMs = 30_000)
        {
            T result = default;
            Exception err = null;
            using var done = new ManualResetEventSlim(false);

            _handler.Enqueue(() =>
            {
                try
                {
                    var doc = _uiApp?.ActiveUIDocument?.Document;
                    if (doc == null) throw new InvalidOperationException("No active Revit document.");
                    result = work(doc);
                }
                catch (Exception ex) { err = ex; }
                finally { done.Set(); }
            });
            _event.Raise();

            if (!done.Wait(timeoutMs))
                throw new TimeoutException("Revit UI thread did not respond in time.");
            if (err != null) throw err;
            return result;
        }

        private class Handler : IExternalEventHandler
        {
            private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _q = new();
            public void Enqueue(Action a) => _q.Enqueue(a);
            public void Execute(UIApplication app)
            {
                while (_q.TryDequeue(out var a)) a();
            }
            public string GetName() => "LoamRevitConnector.Handler";
        }
    }
}
