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
            _modelLog = new ModelLogService(modelLogRoot, "0.6.1", retentionDays: settings.ModelLogRetentionDays);

            // Event-driven push to Loam (additive; no-op if Loam isn't running).
            _events = new LoamEventClient();
            _ctrl = application.ControlledApplication;
            _ctrl.DocumentOpened                   += OnDocumentOpened;
            _ctrl.DocumentSaving                   += OnDocumentSaving;
            _ctrl.DocumentSaved                    += OnDocumentSaved;
            _ctrl.DocumentSavingAs                 += OnDocumentSavingAs;
            _ctrl.DocumentSavedAs                  += OnDocumentSavedAs;
            _ctrl.DocumentSynchronizingWithCentral += OnDocumentSynchronizing;
            _ctrl.DocumentSynchronizedWithCentral  += OnDocumentSynced;
            _ctrl.DocumentReloadingLatest          += OnDocumentReloadingLatest;
            _ctrl.DocumentReloadedLatest           += OnDocumentReloadedLatest;
            _ctrl.DocumentChanged                  += OnDocumentChanged;
            _ctrl.DocumentClosing                  += OnDocumentClosing;
            application.Idling                     += OnIdling;

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            if (_ctrl is not null)
            {
                _ctrl.DocumentOpened                   -= OnDocumentOpened;
                _ctrl.DocumentSaving                   -= OnDocumentSaving;
                _ctrl.DocumentSaved                    -= OnDocumentSaved;
                _ctrl.DocumentSavingAs                 -= OnDocumentSavingAs;
                _ctrl.DocumentSavedAs                  -= OnDocumentSavedAs;
                _ctrl.DocumentSynchronizingWithCentral -= OnDocumentSynchronizing;
                _ctrl.DocumentSynchronizedWithCentral  -= OnDocumentSynced;
                _ctrl.DocumentReloadingLatest          -= OnDocumentReloadingLatest;
                _ctrl.DocumentReloadedLatest           -= OnDocumentReloadedLatest;
                _ctrl.DocumentChanged                  -= OnDocumentChanged;
                _ctrl.DocumentClosing                  -= OnDocumentClosing;
            }
            application.Idling -= OnIdling;
            // Safety net: a closing checkpoint for any model whose DocumentClosing this add-in
            // never saw (see ModelLogService.CloseAll) — a no-op for models already closed.
            try { _modelLog?.CloseAll(); } catch { /* best-effort — never block shutdown */ }
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

        // Point 3 of the crash fix: nothing the connector does may throw out of Idling. Note this
        // cannot catch an AccessViolationException (.NET 8 never lets managed code catch one, and
        // .NET Framework 4.8 doesn't by default) — the crash itself is prevented by never touching
        // a document mid-sync and never keeping a live element iterator between ticks (see
        // ModelLogService's _busy/_generation); this catch covers every ordinary exception.
        private void OnIdling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            try { OnIdlingCore(e); } catch { }
        }

        private void OnIdlingCore(Autodesk.Revit.UI.Events.IdlingEventArgs e)
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

            try { DrainPendingLoamChanges(); } catch { /* best-effort — never throw out of Idling */ }

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
        // Pre-events pause all model-log work until the matching post-event (Idling keeps firing
        // during Save to Central / Reload Latest / Save — the reported crash was a model walk
        // resumed mid-sync). A sync/reload also abandons any walk in flight; a plain save only
        // pauses, since it doesn't bring in other users' changes. Relies on Revit raising the
        // post-event (with a failed/cancelled Status) even when the operation doesn't succeed —
        // NEEDS LIVE-REVIT CHECK (docs/MODEL_LOG.md); if one were ever skipped, model logging
        // stays paused (safe) until that document closes, rather than risking the crash.
        private void OnDocumentSaving(object sender, DocumentSavingEventArgs e)
        {
            try { _modelLog?.BeginDocumentBusy(e.Document, isSyncOrReload: false); } catch { }
        }

        private void OnDocumentSavingAs(object sender, DocumentSavingAsEventArgs e)
        {
            try { _modelLog?.BeginDocumentBusy(e.Document, isSyncOrReload: false); } catch { }
        }

        private void OnDocumentSavedAs(object sender, DocumentSavedAsEventArgs e)
        {
            try { _modelLog?.EndDocumentBusy(e.Document); } catch { }
        }

        private void OnDocumentSynchronizing(object sender, DocumentSynchronizingWithCentralEventArgs e)
        {
            try { _modelLog?.BeginDocumentBusy(e.Document, isSyncOrReload: true); } catch { }
        }

        private void OnDocumentReloadingLatest(object sender, DocumentReloadingLatestEventArgs e)
        {
            try { _modelLog?.BeginDocumentBusy(e.Document, isSyncOrReload: true); } catch { }
        }

        private void OnDocumentSaved(object sender, DocumentSavedEventArgs e)
        {
            try { _modelLog?.EndDocumentBusy(e.Document); } catch { }
            try { Emit("saved", e.Document, "save"); } catch { }
        }

        private void OnDocumentSynced(object sender, DocumentSynchronizedWithCentralEventArgs e)
        {
            try { Emit("saved", e.Document, "sync"); } catch { }
            // A sync picks up OTHER users' changes, which DocumentChanged on this session never
            // reported — the model-log's reconcile pass is what catches those (docs/MODEL_LOG.md's
            // "When the connector writes" table). Also ends the pause the pre-event started.
            try { _modelLog?.OnDocumentSyncedOrReloaded(e.Document); } catch { }
        }

        // CORRECTED by the windows-latest Build workflow's real compile check: this event args
        // class exposes `Document` (a property, same shape as DocumentSynchronizedWithCentral-
        // EventArgs above), not `GetDocument()` — the guess this comment used to flag turned out
        // wrong, exactly the class of error that workflow exists to catch.
        private void OnDocumentReloadedLatest(object sender, DocumentReloadedLatestEventArgs e)
        {
            if (e.Document is null) return;
            try { RefreshFacts(e.Document); } catch { }
            try { _modelLog?.OnDocumentSyncedOrReloaded(e.Document); } catch { }
        }

        private void OnDocumentClosing(object sender, DocumentClosingEventArgs e)
        {
            // The model log's closing checkpoint FIRST: Emit below resolves ModelFacts (worksharing/
            // cloud path lookups that can be slow), and nothing it does may stand between a closing
            // model and its `closed: true` checkpoint.
            try { _modelLog?.OnDocumentClosing(e.Document); } catch { /* best-effort — never block close */ }
            try { Emit("closed", e.Document); } catch { }
            _factsCache.Remove(e.Document);
            _pendingLoamChanges.Remove(e.Document);
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

        // ENERGY EFFICIENCY / RESPONSIVENESS (live requests: "Loam should always only ask for
        // new/changed things", plus a later report that resolving up to 2,000 ids and calling
        // WorksharingUtils synchronously inside every DocumentChanged — part of Revit's own
        // transaction-commit pipeline — was itself a cost, even bounded, paid on EVERY edit while
        // LoamEventClient only ever POSTs once per 45s debounce window). DocumentChanged below now
        // records only the raw ElementIds/names (cheap, no Revit API beyond the event args
        // themselves); UniqueId resolution and the one WorksharingUtils lookup per document happen
        // in DrainPendingLoamChanges instead, called from OnIdlingCore — same UI thread, but once
        // per idle tick rather than once per edit, and skippable while busy (see IsBusy below).
        private sealed class PendingLoamChange
        {
            public readonly HashSet<ElementId> Added = new();
            public readonly HashSet<ElementId> Modified = new();
            public readonly List<long> DeletedIds = new();
            public readonly List<string> TransactionNames = new();
        }
        private readonly Dictionary<Document, PendingLoamChange> _pendingLoamChanges = new();

        private void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            var doc = e.GetDocument();
            if (doc is null) return;

            // THIS transaction's own raw ids/names only (never the accumulator below) — fed to
            // the model log exactly as before, unaffected by this change (it does its own
            // delta-accumulation into a List that isn't deduplicated, so handing it our
            // EVER-GROWING batch on every call, instead of just this transaction's delta, would
            // double-count deletions across repeated calls before the next drain).
            var added = e.GetAddedElementIds();
            var modified = e.GetModifiedElementIds();
            var deleted = e.GetDeletedElementIds();
            List<long> deletedIds = new List<long>();
            foreach (var id in deleted) deletedIds.Add(id.Value);
            List<string> transactionNames = new List<string>();
            try
            {
                var names = e.GetTransactionNames();
                if (names is not null) foreach (var n in names) transactionNames.Add(n);
            }
            catch { /* unavailable on this Revit version/event shape — omit, don't guess */ }

            // Model-log capture: NEVER touches a Parameter or builds a record here — only the raw
            // ids/names are recorded (its own idle time drains them separately — the handoff's
            // "never block the user" rule). LastChangedBy isn't known yet (resolved later, in
            // DrainPendingLoamChanges) — SetPendingLastChangedBy fills it in once it is, onto this
            // SAME batch, as long as it hasn't already been drained into a `chg` record.
            try { _modelLog?.OnDocumentChanged(doc, added, modified, deletedIds, transactionNames, lastChangedBy: null); }
            catch { /* best-effort — must never throw out of DocumentChanged */ }

            // Our OWN accumulator, separately: batches across possibly many transactions until
            // the next idle-time drain resolves it into one Loam "changed" push (see
            // DrainPendingLoamChanges) — a HashSet for added/modified (idempotent, so re-adding an
            // id already in this batch is harmless) but this is the only place these ids are
            // added, never re-derived from a growing snapshot.
            if (!_pendingLoamChanges.TryGetValue(doc, out var p)) _pendingLoamChanges[doc] = p = new PendingLoamChange();
            foreach (var id in added) p.Added.Add(id);
            foreach (var id in modified) p.Modified.Add(id);
            p.DeletedIds.AddRange(deletedIds);
            foreach (var n in transactionNames)
                if (!string.IsNullOrEmpty(n) && !p.TransactionNames.Contains(n)) p.TransactionNames.Add(n);
        }

        /// <summary>Called once per Idling tick: resolves each document's raw DocumentChanged ids
        /// accumulated since the last drain into a Loam "changed" push — UniqueId lookups
        /// (<c>doc.GetElement</c>) and the one <c>WorksharingUtils.GetWorksharingTooltipInfo</c>
        /// call per document, both real Revit API calls that used to run synchronously inside
        /// DocumentChanged on every single edit. Skipped ENTIRELY while <see
        /// cref="ModelLogService.IsBusy"/> — calling <c>doc.GetElement</c> mid-sync is exactly the
        /// crash <c>ModelLogService</c>'s own busy pause exists to prevent, and this idle-time
        /// resolution touches the Revit API the same way, so it must honor the same pause. Left
        /// for the next idle tick once the pause ends — nothing is lost, only delayed.</summary>
        private void DrainPendingLoamChanges()
        {
            if (_pendingLoamChanges.Count == 0) return;
            if (_modelLog?.IsBusy == true) return;

            // Snapshotted first: assigning _pendingLoamChanges[doc] = new PendingLoamChange() for
            // a KEY ALREADY IN the dictionary, while enumerating that SAME dictionary, bumps its
            // version on .NET Framework 4.8 (net48 — Revit 2024) the same as an Add/Remove would,
            // so the next MoveNext throws InvalidOperationException — caught by OnIdling's own
            // try/catch and silently swallowed, meaning the drain (and the Loam push) never
            // completes again. A plain list of keys, iterated separately, sidesteps that.
            foreach (var doc in new List<Document>(_pendingLoamChanges.Keys))
            {
                var p = _pendingLoamChanges[doc];
                if (p.Added.Count == 0 && p.Modified.Count == 0 && p.DeletedIds.Count == 0 && p.TransactionNames.Count == 0)
                    continue;

                bool valid;
                try { valid = doc.IsValidObject; } catch { valid = false; }
                if (!valid) { _pendingLoamChanges[doc] = new PendingLoamChange(); continue; } // closed/invalidated — drop it

                _pendingLoamChanges[doc] = new PendingLoamChange(); // fresh accumulator for what happens next
                var facts = CachedFacts(doc);

                // Check the RAW counts first and skip resolution entirely once they already exceed
                // what would ever be sent — Loam falls back to its own bounded full sweep, same as
                // any other unresolvable case (see MaxChangedIdsToResolve's own doc comment).
                var addedIds = new List<string>();
                var modifiedIds = new List<string>();
                if (p.Added.Count + p.Modified.Count <= MaxChangedIdsToResolve)
                {
                    try
                    {
                        foreach (var id in p.Added)
                        { var el = doc.GetElement(id); if (el is not null) addedIds.Add(el.UniqueId); }
                        foreach (var id in p.Modified)
                        { var el = doc.GetElement(id); if (el is not null) modifiedIds.Add(el.UniqueId); }
                    }
                    catch { /* enumeration failure — fall back to "something changed", no ids */ }
                }

                // WHO CHANGED IT: WorksharingUtils.GetWorksharingTooltipInfo is a per-ELEMENT call
                // and throws on a non-workshared document, so this reads exactly ONE representative
                // touched element (the first added, else first modified) rather than one API call
                // per element in the batch. Degrades to omitting the field on a non-workshared
                // model or any failure — never fabricated.
                string? lastChangedBy = null;
                try
                {
                    if (doc.IsWorkshared)
                    {
                        var sample = ElementId.InvalidElementId;
                        foreach (var id in p.Added) { sample = id; break; }
                        if (sample == ElementId.InvalidElementId)
                            foreach (var id in p.Modified) { sample = id; break; }

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
                    p.DeletedIds.Count > 0 ? p.DeletedIds : null,
                    p.TransactionNames.Count > 0 ? p.TransactionNames : null,
                    lastChangedBy);

                // Fills in the model log's own `chg.by` field for the SAME batch, now that
                // lastChangedBy is finally known — a no-op if that batch was already drained into
                // its own `chg` record by the time this runs.
                try { _modelLog?.SetPendingLastChangedBy(doc, lastChangedBy); } catch { }
            }
        }

        private void Emit(string kind, Document doc, string? cause = null)
        {
            if (doc is null) return;
            var facts = RefreshFacts(doc);
            _events?.Send(kind, facts, cause);
        }

        // ── ModelFacts cache ────────────────────────────────────────────────────
        // REGRESSION FIX (live report: "blocking/slow for several seconds after most actions",
        // and "syncing is also very slow"): OnDocumentChanged used to call ModelFacts.From(doc)
        // — fresh, unconditionally, before any batching/debounce logic — on EVERY single
        // DocumentChanged event, i.e. every user edit. ModelFacts.From calls
        // doc.GetWorksharingCentralModelPath()/ModelPathUtils.ConvertModelPathToUserVisiblePath()
        // (and, for a cloud model, doc.GetCloudModelPath()) — Revit API calls well documented as
        // slow, sometimes requiring a round trip to the worksharing/cloud service, especially on
        // BIM 360/ACC-hosted models. None of these facts (central path, cloud project/model GUID,
        // worksharing state) actually change between edits, so recomputing them synchronously on
        // every transaction (which blocks Revit's UI thread, since DocumentChanged runs
        // synchronously as part of the transaction-commit pipeline) was pure waste — and squarely
        // matches both symptoms: sync is exactly where worksharing/cloud path resolution is most
        // likely to hit a slow path, and "most actions" matches every ordinary edit paying this
        // cost. Cached per document instead, refreshed only at the infrequent milestone events
        // (open/save/sync/reload — see Emit and OnDocumentSynced/OnDocumentReloadedLatest) where
        // recomputing costs nothing noticeable; DocumentChanged now just reads the cached value.
        private readonly Dictionary<Document, ModelFacts> _factsCache = new();

        private ModelFacts RefreshFacts(Document doc)
        {
            var facts = ModelFacts.From(doc);
            _factsCache[doc] = facts;
            return facts;
        }

        private ModelFacts CachedFacts(Document doc) =>
            _factsCache.TryGetValue(doc, out var cached) ? cached : RefreshFacts(doc);
    }
}
