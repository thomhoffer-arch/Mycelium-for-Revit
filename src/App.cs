using System;
using System.Collections.Generic;
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
            application.Idling                    += OnIdling;

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
            application.Idling -= OnIdling;
            _events?.Dispose();
            _server?.Stop();
            return Result.Succeeded;
        }

        // ── Revit document events → Loam model-event POSTs ──────────────────────
        // Handlers run on Revit's UI thread; read the doc fields here (Revit API is
        // thread-affine) and hand plain strings to the fire-and-forget client.

        // REAL READINESS SIGNAL, not a blind delay (live request: "can there also be a signal for extra
        // delay if the thread is still full?"): DocumentOpened fires while Revit is STILL finishing its own
        // open sequence (view generation, sheet-list computation) — sending "opened" here told Loam to start
        // its heavy read before Revit was actually ready, racing them on the same UI thread. Idling is
        // Revit's own "I'm caught up, ready for the next command" signal (the standard add-in pattern for
        // deferring background work). Defer the send to the FIRST Idling tick after open instead of firing
        // immediately — it naturally adapts to model size (a big/worksharing model that takes longer to
        // settle in just delays the signal longer, no guessing a fixed number). Loam's own pulse-side settle
        // delay (LOAM_MODEL_OPENED_SETTLE_MS) stays on top of this as a floor for older connector builds.
        private volatile bool _pendingOpenSignal;
        private Document _pendingOpenDoc;

        private void OnDocumentOpened(object sender, DocumentOpenedEventArgs e)
        {
            _pendingOpenDoc = e.Document;
            _pendingOpenSignal = true;
        }

        private void OnIdling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            if (_pendingOpenSignal)
            {
                _pendingOpenSignal = false;
                var doc = _pendingOpenDoc;
                _pendingOpenDoc = null;
                if (doc is not null) Emit("opened", doc);
            }
            // BOTH WAYS (live request): idle is also the moment to flush any pending "changed" batch whose
            // debounce window has already elapsed — see LoamEventClient.TryFlushIfIdle for the full reasoning.
            _events?.TryFlushIfIdle();
        }

        // A Ctrl+S and a Sync to Central both fire "saved" downstream — older orchestrator
        // builds key off `kind` alone and must keep working — but they are very different
        // events for a workshared model, so `cause` rides along as an additive discriminator
        // ("save" | "sync") without ever changing `kind`.
        private void OnDocumentSaved(object sender, DocumentSavedEventArgs e)
            => Emit("saved", e.Document, "save");

        private void OnDocumentSynced(object sender, DocumentSynchronizedWithCentralEventArgs e)
            => Emit("saved", e.Document, "sync");

        private void OnDocumentClosing(object sender, DocumentClosingEventArgs e)
            => Emit("closed", e.Document);

        // Mirrors LoamEventClient.MaxChangedIds — past this, a batch lookup isn't worth it anyway, so
        // there is no point paying the per-id doc.GetElement() cost below just to have it discarded
        // downstream. Kept as its OWN constant (not a shared reference) because the two live in separate
        // assemblies (the add-in vs. the bridge) and bounding them at the SAME value is a coincidence of
        // today's tuning, not a coupling either side should rely on. Raised alongside its sibling — see
        // the DISRUPTION note there — so an everyday batch edit doesn't needlessly push Loam into a
        // full re-sync; still far below the tens-of-thousands-of-ids case the raw-count pre-check
        // above exists for.
        private const int MaxChangedIdsToResolve = 2000;

        private void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            var doc = e.GetDocument();
            if (doc is null) return;
            var facts = ModelFacts.From(doc);

            // ENERGY EFFICIENCY (live request: "Loam should always only ask for new/changed things") —
            // hand Loam the ACTUAL touched UniqueIds for THIS transaction, not just "something changed",
            // so it can resolve only those elements (pdra_get_element_by_uniqueid) instead of re-walking
            // the whole model on its next read. Added and Modified are kept SEPARATE (see B3 below) —
            // a DELETED element's ElementId no longer resolves to anything (the element is gone), so
            // there is no UniqueId left to report for it; its numeric id is sent on its own instead (see
            // deletedIds below). Best-effort: a failure enumerating ids must never block or throw out of
            // a Revit document-changed callback.
            //
            // REGRESSION FIX (live report: "even Revit file opening was slow and impossible" with this
            // connector installed + Loam pulse running): opening a document fires DocumentChanged ONCE
            // with the ENTIRE model reported as "added" — tens of thousands of ids. Resolving each via
            // doc.GetElement() is a per-element Revit API call; doing that unconditionally, before
            // LoamEventClient's own MaxChangedIds cap ever gets a say, is exactly what hung the UI thread
            // during the one case that matters most (a big/worksharing-enabled model loading in). Check
            // the RAW counts first and skip resolution entirely once they already exceed what would ever
            // be sent — Loam falls back to its own bounded full sweep, same as any other unresolvable case.
            var added = e.GetAddedElementIds();
            var modified = e.GetModifiedElementIds();
            var deleted = e.GetDeletedElementIds();

            var addedIds = new List<string>();
            var modifiedIds = new List<string>();
            if (added.Count + modified.Count <= MaxChangedIdsToResolve)
            {
                try
                {
                    foreach (var id in added)
                    { var el = doc.GetElement(id); if (el is not null) addedIds.Add(el.UniqueId); }
                    foreach (var id in modified)
                    { var el = doc.GetElement(id); if (el is not null) modifiedIds.Add(el.UniqueId); }
                }
                catch { /* enumeration failure — fall back to "something changed", no ids */ }
            }

            // B3 — DELETIONS: e.GetDeletedElementIds() still yields numeric ids even though they no
            // longer resolve to an Element/UniqueId (the element is gone) — no doc.GetElement() call
            // needed, so this isn't gated by MaxChangedIdsToResolve the way added/modified are. Loam's
            // ingest already has a consumer for this shape (deletedIds) waiting.
            List<long> deletedIds = new List<long>();
            foreach (var id in deleted) deletedIds.Add(id.Value);

            // B3 — TRANSACTION NAME(S): Revit's own name for the operation ("Move Walls", "Change
            // Type", "Delete") — literally why the change happened, computed by Revit already and,
            // until now, never sent. Best-effort: must never throw out of this callback.
            List<string> transactionNames = new List<string>();
            try
            {
                var names = e.GetTransactionNames();
                if (names is not null) foreach (var n in names) transactionNames.Add(n);
            }
            catch { /* unavailable on this Revit version/event shape — omit, don't guess */ }

            // B3 — WHO CHANGED IT: WorksharingUtils.GetWorksharingTooltipInfo is a per-ELEMENT call and
            // throws on a non-workshared document, so this reads exactly ONE representative touched
            // element (the first added, else first modified) rather than one API call per element in
            // the transaction — a single transaction's added/modified elements are, in practice, all
            // just touched by the same user in the same edit, so one read is enough without doubling
            // the per-element cost the id-resolution loop above already pays. Degrades to omitting the
            // field on a non-workshared model or any failure — never fabricated.
            string? lastChangedBy = null;
            try
            {
                if (doc.IsWorkshared)
                {
                    var sample = ElementId.InvalidElementId;
                    foreach (var id in added) { sample = id; break; }
                    if (sample == ElementId.InvalidElementId)
                        foreach (var id in modified) { sample = id; break; }

                    if (sample != ElementId.InvalidElementId)
                    {
                        var info = WorksharingUtils.GetWorksharingTooltipInfo(doc, sample);
                        if (!string.IsNullOrEmpty(info?.LastChangedBy)) lastChangedBy = info.LastChangedBy;
                    }
                }
            }
            catch { /* non-workshared, or this element carries no worksharing info — omit, don't guess */ }

            _events?.SendChanged(
                facts,
                addedIds.Count > 0 ? addedIds : null,
                modifiedIds.Count > 0 ? modifiedIds : null,
                deletedIds.Count > 0 ? deletedIds : null,
                transactionNames.Count > 0 ? transactionNames : null,
                lastChangedBy);
        }

        private void Emit(string kind, Document doc, string? cause = null)
        {
            if (doc is null) return;
            var facts = ModelFacts.From(doc);
            _events?.Send(kind, facts, cause);
        }
    }
}
