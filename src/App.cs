using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.UI;
using Loam.Revit.Connector.Mcp;
using Loam.Revit.Connector.RevitBridge;

namespace Loam.Revit.Connector
{
    public sealed class App : IExternalApplication
    {
        private McpServer _server;
        private RevitContext _ctx;
        private LoamEventClient _events;
        private ControlledApplication _ctrl;

        public Result OnStartup(UIControlledApplication application)
        {
            _ctx = new RevitContext(application);

            var listen = Environment.GetEnvironmentVariable("LOAM_REVIT_LISTEN")
                         ?? "http://127.0.0.1:47100/mcp";
            var token  = Environment.GetEnvironmentVariable("LOAM_REVIT_TOKEN");

            _server = new McpServer(listen, token, _ctx);
            _server.Start();

            // Event-driven push to Loam (additive; no-op if Loam isn't running).
            _events = new LoamEventClient();
            _ctrl = application.ControlledApplication;
            _ctrl.DocumentOpened                  += OnDocumentOpened;
            _ctrl.DocumentSaved                   += OnDocumentSaved;
            _ctrl.DocumentSynchronizedWithCentral += OnDocumentSynced;
            _ctrl.DocumentChanged                 += OnDocumentChanged;
            _ctrl.DocumentClosing                 += OnDocumentClosing;

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            if (_ctrl is not null)
            {
                _ctrl.DocumentOpened                  -= OnDocumentOpened;
                _ctrl.DocumentSaved                   -= OnDocumentSaved;
                _ctrl.DocumentSynchronizedWithCentral -= OnDocumentSynced;
                _ctrl.DocumentChanged                 -= OnDocumentChanged;
                _ctrl.DocumentClosing                 -= OnDocumentClosing;
            }
            _events?.Dispose();
            _server?.Stop();
            return Result.Succeeded;
        }

        // ── Revit document events → Loam model-event POSTs ──────────────────────
        // Handlers run on Revit's UI thread; read the doc fields here (Revit API is
        // thread-affine) and hand plain strings to the fire-and-forget client.

        private void OnDocumentOpened(object sender, DocumentOpenedEventArgs e)
            => Emit("opened", e.Document);

        private void OnDocumentSaved(object sender, DocumentSavedEventArgs e)
            => Emit("saved", e.Document);

        private void OnDocumentSynced(object sender, DocumentSynchronizedWithCentralEventArgs e)
            => Emit("saved", e.Document);

        private void OnDocumentClosing(object sender, DocumentClosingEventArgs e)
            => Emit("closed", e.Document);

        private void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            var doc = e.GetDocument();
            if (doc is null) return;
            var (model, project, revision) = Describe(doc);
            _events?.SendChanged(model, project, revision);
        }

        private void Emit(string kind, Document doc)
        {
            if (doc is null) return;
            var (model, project, revision) = Describe(doc);
            _events?.Send(kind, model, project, revision);
        }

        private static (string model, string project, string revision) Describe(Document doc)
        {
            string model = null, project = null, revision = null;
            try { model = doc.Title; } catch { }

            try
            {
                var pi = doc.ProjectInformation;
                var number = pi?.Number ?? "";
                var name   = pi?.Name   ?? "";
                project = $"{number} {name}".Trim();
                if (project.Length == 0) project = null;
            }
            catch { }

            try
            {
                var v = Document.GetDocumentVersion(doc);
                revision = v?.VersionGUID.ToString();
            }
            catch { }

            return (model, project, revision);
        }
    }
}
