using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.UI;
using Loam.Revit.Connector.Mcp;
using Loam.Revit.Connector.ModelLogCapture;
using Loam.Revit.Connector.RevitBridge;

namespace Loam.Revit.Connector
{
    public sealed class App : IExternalApplication
    {
        private McpServer _server;
        private RevitContext _ctx;
        private LoamEventClient _events;
        private ModelLogService _modelLog;
        private ControlledApplication _ctrl;

        public Result OnStartup(UIControlledApplication application)
        {
            _ctx = new RevitContext(application);

            // SECURITY (the handoff plan's "fix first" item): listen URL/token/log-root come
            // from the add-in's own settings file, never an environment variable — README.md
            // used to document MYCELIUM_REVIT_LISTEN/_TOKEN while this file read
            // LOAM_REVIT_LISTEN/_TOKEN, so following the README silently ran the MCP server with
            // NO bearer auth. A missing settings file gets a fresh auto-generated token on first
            // run; a settings file that explicitly carries a blank token is refused outright —
            // see ConnectorSettings' own doc comment.
            var settings = ConnectorSettings.LoadOrCreate(ConnectorSettings.DefaultPath());
            if (settings.ExplicitlyNoAuth)
            {
                TaskDialog.Show(
                    "Mycelium Studio Revit Connector",
                    "The connector's settings file has no bearer token, so the MCP server was NOT " +
                    "started (it never runs unauthenticated). Delete the token line from " +
                    ConnectorSettings.DefaultPath() + " to have one generated automatically, or set " +
                    "a token there yourself.");
            }
            else
            {
                _server = new McpServer(settings.Listen, settings.Token, _ctx);
                _server.Start();
            }

            var modelLogRoot = string.IsNullOrEmpty(settings.ModelLogRoot)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Loam", "RevitConnector", "model-logs")
                : settings.ModelLogRoot!;
            _modelLog = new ModelLogService(modelLogRoot, "0.5.0");

            // Event-driven push to Loam (additive; no-op if Loam isn't running).
            _events = new LoamEventClient();
            _ctrl = application.ControlledApplication;
            _ctrl.DocumentOpened                  += OnDocumentOpened;
            _ctrl.DocumentSaved                   += OnDocumentSaved;
            _ctrl.DocumentSynchronizedWithCentral += OnDocumentSynced;
            _ctrl.DocumentReloadedLatest           += OnDocumentReloadedLatest;
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
                _ctrl.DocumentReloadedLatest           -= OnDocumentReloadedLatest;
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

            // Snapshot-or-reconcile is queued right away (not deferred to the idle tick like the
            // Loam "opened" push below) — it's ALL idle-time-sliced work internally anyway (see
            // ModelLogService/IdleSliceRunner), so it starts settling in on the very next Idling
            // tick rather than waiting for one extra tick first.
            try { _modelLog?.OnDocumentOpened(e.Document); } catch { /* best-effort — never block open */ }
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

            try { _modelLog?.OnIdling(); } catch { /* best-effort — never throw out of Idling */ }

            // CRITICAL (live report: a snapshot/reconcile processed ~200 of ~33,600 elements over
            // two minutes, then stopped almost entirely — matching a burst of mouse movement over
            // Revit's window, then near-silence once the user stopped interacting with it, not a
            // steady-but-slow trickle). By default, Revit's Idling event is throttled: it fires
            // once, then waits for further UI activity (mouse move, keystroke) before firing again
            // — it is NOT a free-running timer. An add-in that wants Idling to keep firing on its
            // own, so background work keeps draining while the user does nothing else, MUST call
            // SetRaiseWithoutDelay() on every tick it still has work left to do; omitting it is
            // exactly why a large snapshot/reconcile would stall for minutes at a time.
            //
            // NOT unconditionally, though — a follow-up live report ("connector blocking/slow for
            // several seconds after most actions") showed that requesting it on every tick races
            // to drain an entire large backlog in one uninterrupted burst, starving Revit's own
            // message pump of the redraw/input processing a user expects to see promptly.
            // ModelLogService.ShouldRequestContinuousIdling caps each burst to a short window and
            // then releases control for one natural idle interval, so backlogs still drain
            // steadily whenever the user does anything, without monopolizing the idle loop.
            try { if (_modelLog?.ShouldRequestContinuousIdling() == true) e.SetRaiseWithoutDelay(); } catch { }
        }

        // A Ctrl+S and a Sync to Central both fire "saved" downstream — older orchestrator
        // builds key off `kind` alone and must keep working — but they are very different
        // events for a workshared model, so `cause` rides along as an additive discriminator
        // ("save" | "sync") without ever changing `kind`.
        private void OnDocumentSaved(object sender, DocumentSavedEventArgs e)
            => Emit("saved", e.Document, "save");

        private void OnDocumentSynced(object sender, DocumentSynchronizedWithCentralEventArgs e)
        {
            Emit("saved", e.Document, "sync");
            // A sync picks up OTHER users' changes, which DocumentChanged on this session never
            // reported — the model-log's reconcile pass is what catches those (docs/MODEL_LOG.md's
            // "When the connector writes" table).
            try { _modelLog?.OnDocumentSyncedOrReloaded(e.Document); } catch { }
        }

        // CORRECTED by the windows-latest Build workflow's real compile check: this event args
        // class exposes `Document` (a property, same shape as DocumentSynchronizedWithCentral-
        // EventArgs above), not `GetDocument()` — the guess this comment used to flag turned out
        // wrong, exactly the class of error that workflow exists to catch.
        private void OnDocumentReloadedLatest(object sender, DocumentReloadedLatestEventArgs e)
        {
            if (e.Document is null) return;
            try { _modelLog?.OnDocumentSyncedOrReloaded(e.Document); } catch { }
        }

        private void OnDocumentClosing(object sender, DocumentClosingEventArgs e)
        {
            Emit("closed", e.Document);
            try { _modelLog?.OnDocumentClosing(e.Document); } catch { /* best-effort — never block close */ }
        }

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

            // Model-log capture: NEVER touches a Parameter or builds a record here — only the
            // raw ids/names are recorded; idle time drains them (the handoff's "never block the
            // user" rule). Reuses the SAME added/modified/deleted/transactionNames/lastChangedBy
            // this method already computed for the Loam push above, so there is exactly one
            // enumeration of e.GetAddedElementIds() etc., not two.
            try { _modelLog?.OnDocumentChanged(doc, added, modified, deletedIds, transactionNames, lastChangedBy); }
            catch { /* best-effort — must never throw out of DocumentChanged */ }
        }

        private void Emit(string kind, Document doc, string? cause = null)
        {
            if (doc is null) return;
            var facts = ModelFacts.From(doc);
            _events?.Send(kind, facts, cause);
        }
    }
}
