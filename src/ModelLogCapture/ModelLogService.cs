using Autodesk.Revit.DB;
using Loam.Revit.Connector.ModelLog;
using Loam.Revit.Connector.RevitBridge;
using PDRA.Services.Ai.Tools.Queries;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Loam.Revit.Connector.ModelLogCapture
{
    /// <summary>
    /// Owns one <see cref="ModelLogWriter"/> per open <see cref="Document"/> and drives it from
    /// Revit's own document events + idle time, per docs/MODEL_LOG.md's "When the connector
    /// writes" table:
    ///
    /// <list type="bullet">
    /// <item>Model opened, no log yet → full snapshot (definitions, then every element/sheet/
    /// revision/link), then a checkpoint.</item>
    /// <item>Model opened, log exists, same producer version → gap (if the previous session
    /// didn't close cleanly), then reconcile (write only what differs from the hash cache,
    /// delete what's gone), then a checkpoint.</item>
    /// <item>Model opened, log exists, producer version changed → a NEW LOG GENERATION: a fresh
    /// segment (header, session, project, every definition re-emitted, full state of everything,
    /// deletion detection), then a checkpoint — see <see cref="SnapshotJob"/>'s
    /// <c>isUpgrade</c>.</item>
    /// <item>User edits (<c>DocumentChanged</c>) → NEVER processed inline (the handoff's "never
    /// block the user" rule) — only the raw ids/transaction names/editor are recorded; idle time
    /// drains the queue into a <c>chg</c> record plus one record per touched id. Held back
    /// entirely while a snapshot/reconcile for that document is outstanding, since the walk
    /// covers every element anyway — see <see cref="MarkWalkElementSnapshot"/>.</item>
    /// <item>Sync/reload-latest → reconcile (catches other users' changes even if
    /// <c>DocumentChanged</c> didn't report them).</item>
    /// <item>Closing → a final checkpoint with <c>closed: true</c>.</item>
    /// </list>
    ///
    /// All actual Revit-touching work runs as <see cref="IdleSliceRunner"/> jobs on the
    /// <c>Idling</c> event (already the UI thread — no <see cref="RevitContext"/> marshalling
    /// needed here, unlike the on-demand MCP tools).
    /// </summary>
    public sealed class ModelLogService
    {
        private readonly string _modelLogRoot;
        private readonly string _producerVersion;
        private readonly IdleSliceRunner _idle;
        private readonly Dictionary<Document, ModelLogWriter> _writers = new();
        private readonly Dictionary<Document, PendingChange> _pending = new();
        private readonly HashSet<Document> _changeJobQueued = new();
        private readonly Dictionary<Document, IndexCache> _indexCache = new();
        // Ids pending for a document at the instant its currently-running walk (snapshot/
        // reconcile) started — see MarkWalkElementSnapshot. An id changed AGAIN after that
        // instant is removed from here by OnDocumentChanged, so it isn't wrongly treated as
        // covered by a walk that read the model before that later edit happened.
        private readonly Dictionary<Document, HashSet<ElementId>> _walkCoveredIds = new();

        /// <summary>Node/grid/tagged-sheet indexes are whole-document walks — expensive to
        /// rebuild, but stable between edits. Built once by the last snapshot/reconcile pass and
        /// reused by every change-capture batch after that (mutated in place as levels/spaces/
        /// grids change); rebuilding one of these on every ordinary edit — the bug this cache
        /// fixes — meant even a single parameter change on one wall re-walked every sheet, tag,
        /// room, space, level and grid in the document before touching the wall itself.</summary>
        private sealed class IndexCache
        {
            public Dictionary<ElementId, string> NodeIndex = new();
            public List<(string Name, Line Line)> GridLines = new();
            public Dictionary<ElementId, List<string>> TaggedSheets = new();
        }

        public ModelLogService(string modelLogRoot, string producerVersion, Action<string, double, int>? onJobFinished = null)
        {
            _modelLogRoot = modelLogRoot;
            _producerVersion = producerVersion;
            _idle = new IdleSliceRunner(onJobFinished ?? ((_, __, ___) => { }), onJobFailed: (_, __) => { });
        }

        // ── Sync/reload/save/close safety ──────────────────────────────────────
        // CRASH FIX (live report: Revit closed with an AccessViolationException whose stack ran
        // App.OnIdling → ModelLogService.OnIdling → IdleSliceRunner.RunSlice → ReconcileJob →
        // WalkModel → FilteredElementIterator.MoveNext, during a Synchronize with Central's
        // "Save to Central" step). Two things were wrong:
        //  1. WalkModel kept live FilteredElementCollector iterators open across idle ticks (a
        //     `foreach` over the collector with a `yield` inside it), so a paused walk held a
        //     native cursor into the document while Revit rebuilt it.
        //  2. Idling DOES fire while a sync is in progress, so the walk was resumed mid-sync.
        // Fixed by: pausing ALL model-log work between a sync/reload/save's pre-event and its
        // post-event (_busy); bumping a per-document generation on sync/reload/close so an
        // in-flight walk abandons itself (a fresh reconcile is queued by the post-event); and
        // walking element-id lists taken up front, looking each element up fresh.
        // A nesting count, not a flag: if Revit raises DocumentSaving/Saved inside a Save to
        // Central, the inner Saved must not end the pause while the sync is still running.
        private readonly Dictionary<Document, int> _busy = new();
        private readonly Dictionary<Document, int> _generation = new();

        private int Generation(Document doc) => _generation.TryGetValue(doc, out var g) ? g : 0;
        private void BumpGeneration(Document doc) => _generation[doc] = Generation(doc) + 1;

        private static bool IsDocumentValid(Document doc)
        {
            try { return doc.IsValidObject; }
            catch { return false; }
        }

        /// <summary>Call from a sync/reload/save PRE-event (DocumentSynchronizingWithCentral,
        /// DocumentReloadingLatest, DocumentSaving): no model-log work runs for any document
        /// until the matching post-event calls <see cref="EndDocumentBusy"/>. A sync/reload also
        /// invalidates any walk already in flight for this document.</summary>
        public void BeginDocumentBusy(Document doc, bool invalidatesWalks)
        {
            _busy[doc] = (_busy.TryGetValue(doc, out var n) ? n : 0) + 1;
            if (invalidatesWalks) BumpGeneration(doc);
        }

        public void EndDocumentBusy(Document doc)
        {
            if (!_busy.TryGetValue(doc, out var n)) return;
            if (n <= 1) _busy.Remove(doc);
            else _busy[doc] = n - 1;
        }

        /// <summary>True while any document has a queued idle-slice job (a snapshot/reconcile
        /// still walking the model, or a change-capture batch) OR unflushed <c>DocumentChanged</c>
        /// data waiting for its own job to be queued on the next tick.</summary>
        public bool HasPendingWork =>
            _idle.HasWork ||
            _pending.Values.Any(p => p.Added.Count > 0 || p.Modified.Count > 0 || p.Deleted.Count > 0);

        private readonly System.Diagnostics.Stopwatch _continuousBurst = new();
        private static readonly TimeSpan MaxContinuousBurst = TimeSpan.FromMilliseconds(250);

        /// <summary>Call once per Idling tick (App.cs's <c>OnIdling</c>) to decide whether to ask
        /// Revit to keep firing <c>Idling</c> back-to-back (<c>IdlingEventArgs.SetRaiseWithoutDelay()</c>)
        /// — NOT the same as unconditionally requesting it whenever <see cref="HasPendingWork"/>
        /// is true. A live report ("connector blocking/slow for several seconds after most
        /// actions") showed why: once continuous firing actually started draining a large backlog
        /// (a 33,600-element reconcile, or a big edit batch after a regen touched many hosted/
        /// joined elements), it raced to drain the ENTIRE thing in one uninterrupted burst,
        /// starving Revit's own message pump of the redraw/input messages a user expects to see
        /// promptly — the previous bug (Idling barely firing at all) had at least left long
        /// natural gaps between slices; this fix's first cut removed the gaps entirely instead of
        /// just shortening them enough to still make progress.
        ///
        /// Capped to a short continuous burst (<see cref="MaxContinuousBurst"/>) instead: once a
        /// burst has been running continuously for that long, this returns false for one tick,
        /// letting <c>Idling</c> revert to Revit's own throttled cadence (which still fires
        /// again the moment the user does anything — mouse move, keystroke — nearly continuous
        /// during active editing) before starting a fresh burst. Forward progress on a large
        /// backlog stays steady as long as the user is doing ANYTHING; the idle loop just no
        /// longer monopolizes it for one long uninterrupted stretch.</summary>
        public bool ShouldRequestContinuousIdling()
        {
            if (!HasPendingWork || _busy.Count > 0)
            {
                _continuousBurst.Reset();
                return false;
            }
            if (!_continuousBurst.IsRunning) _continuousBurst.Restart();
            if (_continuousBurst.Elapsed < MaxContinuousBurst) return true;

            _continuousBurst.Reset(); // release control for one natural idle interval
            return false;
        }

        private sealed class PendingChange
        {
            public readonly HashSet<ElementId> Added = new();
            public readonly HashSet<ElementId> Modified = new();
            // Numeric ElementIds only — a deleted element's UniqueId is not resolvable any more
            // (Element.UniqueId requires a live Element; doc.GetElement(id) is already null by
            // the time DocumentChanged reports it). Kept for the chg record's own `deleted`
            // count; the actual `del` records are written lazily by the next reconcile (on open
            // or sync), which compares the hash cache's known UniqueIds against a fresh walk and
            // needs no numeric id at all — see ModelLogWriter.KnownElementIdsNotIn.
            public readonly List<long> Deleted = new();
            public readonly List<string> TransactionNames = new();
            public string? LastChangedBy;

            public IEnumerable<ElementId> AddedOrModified => Added.Concat(Modified);
        }

        /// <summary>Best-effort, stable per-document folder name for the log — the cross-user
        /// identity anchor when known (cloud project+model, else central path), else this local
        /// copy's own path/title as a last resort (still useful for a single-user standalone
        /// model; just not cross-user-stable).</summary>
        private static string ModelId(Document doc, ModelFacts facts)
        {
            if (!string.IsNullOrEmpty(facts.ModelInstanceId)) return facts.ModelInstanceId!;
            try { if (!string.IsNullOrEmpty(doc.PathName)) return doc.PathName; } catch { }
            return facts.Model ?? "unknown-model";
        }

        public void OnDocumentOpened(Document doc)
        {
            var facts = ModelFacts.From(doc);
            var writer = new ModelLogWriter(_modelLogRoot, ModelId(doc, facts));
            if (writer.LockHeldElsewhere)
            {
                // Another Revit session already writes this model's log — per the handoff's
                // crash-safety rule #4, this session must not write at all. Dispose our (unused)
                // writer handle; there is nothing more to do for this document.
                writer.Dispose();
                return;
            }
            _writers[doc] = writer;

            var isFreshLog = writer.LastSeq == 0;

            // An existing log whose last session recorded a DIFFERENT producer version means an
            // upgrade happened since the last open — per the developer feedback (github summary):
            // "when an upgrade changes what gets logged, clean up the log ... send delete records
            // for elements that are now filtered out, plus updates adding the new fields." A
            // reconcile alone can never fix a definition bug: `pdef`/`cat` records are written
            // once per id and never re-checked (ModelLogWriter.WriteIfUnseen), and the header is
            // only written at a segment start — so a stale/wrong pdef or an old header would live
            // on forever. Instead this starts a NEW LOG GENERATION: a fresh segment with its own
            // header, session and project records, every definition re-emitted (the pdef/cat seen
            // sets are cleared — see ModelLogWriter.BeginNewGeneration) and the full state of
            // everything, same as a first-time snapshot, plus deletion detection so elements the
            // new version no longer logs get `del` records.
            var versionChanged = !isFreshLog && writer.LastProducerVersion is not null
                && writer.LastProducerVersion != _producerVersion;

            if (isFreshLog)
            {
                // A brand-new log's first line must be its header, so the session record is
                // written by SnapshotJob right after BeginSnapshot rather than here.
                _snapshotOwed.Add(doc);
                _walkOutstanding.Add(doc);
                _idle.Enqueue("snapshot", SnapshotJob(doc, writer, Generation(doc)));
            }
            else if (versionChanged)
            {
                // Tagged in _upgradeOwed too so a sync/reload that interrupts this pass is
                // resumed as the SAME kind of pass (see OnDocumentSyncedOrReloaded), not
                // downgraded to an ordinary first-time snapshot.
                _snapshotOwed.Add(doc);
                _upgradeOwed.Add(doc);
                _walkOutstanding.Add(doc);
                _idle.Enqueue("snapshot-upgrade", SnapshotJob(doc, writer, Generation(doc), isUpgrade: true));
            }
            else
            {
                writer.RecordSession(_producerVersion, SafeRevitVersion(doc));
                writer.WriteGapIfNeeded("no closed checkpoint from the previous session");
                _walkOutstanding.Add(doc);
                _idle.Enqueue("reconcile-on-open", ReconcileJob(doc, writer, Generation(doc)));
            }
        }

        /// <summary>Call from the sync/reload POST-event. Ends the busy pause, abandons any walk
        /// that was in flight before the sync (its element-id list predates other users' changes)
        /// and queues a fresh reconcile that sees the post-sync model.</summary>
        public void OnDocumentSyncedOrReloaded(Document doc)
        {
            EndDocumentBusy(doc);
            BumpGeneration(doc);
            if (!_writers.TryGetValue(doc, out var writer)) return;
            _walkOutstanding.Add(doc);
            // A first snapshot or an upgrade pass the sync interrupted is re-run as the SAME kind
            // of pass (header, project, session, full state — isUpgrade carried over via
            // _upgradeOwed), not downgraded to a reconcile or a plain snapshot that would skip
            // BeginNewGeneration/deletion detection.
            if (_snapshotOwed.Contains(doc))
                _idle.Enqueue("snapshot-after-sync", SnapshotJob(doc, writer, Generation(doc), isUpgrade: _upgradeOwed.Contains(doc)));
            else
                _idle.Enqueue("reconcile-on-sync", ReconcileJob(doc, writer, Generation(doc)));
        }

        public void OnDocumentClosing(Document doc)
        {
            // Any job still queued for this document abandons itself on its next step instead of
            // touching a closed document or the disposed writer below.
            BumpGeneration(doc);
            _busy.Remove(doc);
            if (!_writers.TryGetValue(doc, out var writer)) return;
            var version = SafeVersionGuid(doc);
            // Only "complete" if the last snapshot/reconcile actually finished — closing mid-walk
            // must not claim the log holds the whole model.
            writer.WriteCheckpoint(complete: !_walkOutstanding.Contains(doc), modelVersion: version, elementCount: null, closed: true);
            writer.Dispose();
            _writers.Remove(doc);
            _pending.Remove(doc);
            _changeJobQueued.Remove(doc);
            _indexCache.Remove(doc);
            _upgradeOwed.Remove(doc);
            _snapshotOwed.Remove(doc);
            _walkOutstanding.Remove(doc);
            _walkCoveredIds.Remove(doc);
            // _generation keeps its (bumped) entry: removing it would reset this document to
            // generation 0 and make a job queued at generation 0 look current again.
        }

        // An upgrade (new-generation) snapshot pass that a sync interrupts must not be silently
        // downgraded to an ordinary snapshot by the replacement snapshot-after-sync — owed until
        // an upgrade pass actually completes.
        private readonly HashSet<Document> _upgradeOwed = new();
        private readonly HashSet<Document> _snapshotOwed = new();
        // A snapshot/reconcile is queued or running and hasn't reached its checkpoint yet.
        private readonly HashSet<Document> _walkOutstanding = new();

        /// <summary>Called from <c>DocumentChanged</c> — records ONLY the raw ids/names, never
        /// touches a Parameter or builds a record here (that all happens later, during idle
        /// time). Safe to call even when this document has no writer (nothing happens).</summary>
        public void OnDocumentChanged(
            Document doc,
            IEnumerable<ElementId> added,
            IEnumerable<ElementId> modified,
            IEnumerable<long> deletedElementIds,
            IReadOnlyList<string> transactionNames,
            string? lastChangedBy)
        {
            if (!_writers.ContainsKey(doc)) return;

            if (!_pending.TryGetValue(doc, out var p)) _pending[doc] = p = new PendingChange();
            foreach (var id in added) p.Added.Add(id);
            foreach (var id in modified) p.Modified.Add(id);
            p.Deleted.AddRange(deletedElementIds);
            foreach (var tn in transactionNames)
                if (!p.TransactionNames.Contains(tn)) p.TransactionNames.Add(tn);
            if (!string.IsNullOrEmpty(lastChangedBy)) p.LastChangedBy = lastChangedBy;

            // An edit landing AFTER an in-progress walk already took its covered-ids snapshot
            // (MarkWalkElementSnapshot) un-covers this id: the walk read the model before this
            // edit happened, so it must still get its own change-capture pass once the walk
            // finishes, even though it was already in `p` (a HashSet.Add of an id already
            // present is a no-op, so without this the edit would otherwise go unrecorded).
            if (_walkCoveredIds.TryGetValue(doc, out var covered))
            {
                foreach (var id in added) covered.Remove(id);
                foreach (var id in modified) covered.Remove(id);
            }
        }

        /// <summary>Call on every Idling tick: runs one slice of whatever job is at the front of
        /// the queue, and enqueues a change-capture job for any document with pending
        /// <c>DocumentChanged</c> data that doesn't already have one queued.</summary>
        public void OnIdling()
        {
            // Idling fires even while Save to Central / Reload Latest / Save is running — never
            // touch any document then (see the crash note above _busy).
            if (_busy.Count > 0) return;

            foreach (var kv in _pending)
            {
                var doc = kv.Key;
                if (kv.Value.Added.Count == 0 && kv.Value.Modified.Count == 0 && kv.Value.Deleted.Count == 0) continue;
                if (_changeJobQueued.Contains(doc)) continue;
                // A snapshot/reconcile for this document is already queued or running — it will
                // walk (and, for a modified element, re-read) every element anyway, so a
                // change-capture job here would just duplicate that work. Keep accumulating
                // instead: nothing is lost — see MarkWalkElementSnapshot/ClearWalkCoveredPending.
                if (_walkOutstanding.Contains(doc)) continue;
                if (!_writers.TryGetValue(doc, out var writer)) continue;

                var change = kv.Value;
                _pending[doc] = new PendingChange(); // fresh accumulator for what happens next
                _changeJobQueued.Add(doc);
                var gen = Generation(doc);
                _idle.Enqueue("change-capture", ChangeCaptureJob(doc, writer, change,
                    () => Generation(doc) != gen || !IsDocumentValid(doc),
                    () => _changeJobQueued.Remove(doc)));
            }

            _idle.RunSlice();
            // Cheap: appends whatever's buffered to the small delta journal and flushes — never
            // rewrites the (possibly tens-of-MB) state.json base except at a checkpoint, session
            // record, segment rotation or Dispose, and only then if the journal itself has grown
            // large enough to be worth folding back in (see ModelLogWriter.MaybeCompact).
            foreach (var w in _writers.Values)
            {
                try { w.FlushState(); } catch { /* retried on the next tick */ }
            }
        }

        // ── Jobs ─────────────────────────────────────────────────────────────────

        /// <param name="gen">The document's generation when the job was QUEUED (not when it
        /// first runs) — a job queued before a sync/reload/close is stale even if it hadn't
        /// started yet.</param>
        /// <param name="isUpgrade">True only for the producer-version-change pass queued by
        /// <see cref="OnDocumentOpened"/>: begins a NEW LOG GENERATION (<see
        /// cref="ModelLogWriter.BeginNewGeneration"/>, rotating first if the active segment has
        /// content) instead of a plain <see cref="ModelLogWriter.BeginSnapshot"/>, and also runs
        /// deletion detection (a first-time snapshot has nothing to compare against yet; an
        /// upgrade's existing log does, so elements the new version no longer logs get
        /// `del`).</param>
        private IEnumerator<bool> SnapshotJob(Document doc, ModelLogWriter writer, int gen, bool isUpgrade = false)
        {
            bool IsStale() => Generation(doc) != gen || !IsDocumentValid(doc);
            if (IsStale()) yield break;
            var facts = ModelFacts.From(doc);
            var header = RecordBuilder.BuildHeader(doc, facts, _producerVersion, ModelId(doc, facts));
            if (isUpgrade) writer.BeginNewGeneration(header); else writer.BeginSnapshot(header);
            writer.RecordSession(_producerVersion, SafeRevitVersion(doc));
            writer.Append(RecordKinds.Project, RecordBuilder.BuildProject(doc));
            yield return true;

            foreach (var step in WalkModel(doc, writer, forceFullState: true, detectDeletions: isUpgrade, IsStale)) yield return step;
            // Interrupted by a sync/reload/close: never checkpoint a partial walk as complete.
            // After a sync/reload the post-event has already queued this snapshot again.
            if (IsStale()) yield break;
            _snapshotOwed.Remove(doc);
            _upgradeOwed.Remove(doc);
            _walkOutstanding.Remove(doc);
            ClearWalkCoveredPending(doc);
            RefreshIndexCache(doc);

            var version = SafeVersionGuid(doc);
            var count = CountElements(doc);
            writer.WriteCheckpoint(complete: true, modelVersion: version, elementCount: count, closed: false);
        }

        /// <summary>An ordinary reconcile — same producer version as the last session. Only what
        /// differs from the hash cache is written; deletion detection
        /// (<see cref="ModelLogWriter.KnownElementIdsNotIn"/>) always runs, since that's the
        /// whole point of a reconcile (catching elements deleted while the connector wasn't
        /// watching). A producer-version change is handled entirely by <see cref="SnapshotJob"/>'s
        /// <c>isUpgrade</c> path instead — see <see cref="OnDocumentOpened"/>.</summary>
        private IEnumerator<bool> ReconcileJob(Document doc, ModelLogWriter writer, int gen)
        {
            bool IsStale() => Generation(doc) != gen || !IsDocumentValid(doc);
            if (IsStale()) yield break;
            var facts = ModelFacts.From(doc);
            var header = RecordBuilder.BuildHeader(doc, facts, _producerVersion, ModelId(doc, facts));
            writer.RotateIfNeeded(header);

            foreach (var step in WalkModel(doc, writer, forceFullState: false, detectDeletions: true, IsStale)) yield return step;
            if (IsStale()) yield break; // see SnapshotJob
            _walkOutstanding.Remove(doc);
            ClearWalkCoveredPending(doc);
            RefreshIndexCache(doc);

            var version = SafeVersionGuid(doc);
            var count = CountElements(doc);
            writer.WriteCheckpoint(complete: true, modelVersion: version, elementCount: count, closed: false);
        }

        /// <summary>Called as the very FIRST step of <see cref="WalkModel"/>, before any section
        /// (levels/rooms/grids are walked before the element section, so the snapshot can't wait
        /// for that). Records which of this document's currently-pending ids are covered:
        /// everything the walk goes on to write is read fresh from the model, i.e. AFTER every
        /// edit pending at this instant already happened, so those ids need no change-capture job
        /// to redo the same work once the walk finishes (see <see cref="ClearWalkCoveredPending"/>).
        /// An id changed again after this instant is removed from the covered set by
        /// <see cref="OnDocumentChanged"/>, so a walk that already read its old state doesn't
        /// wrongly swallow the later edit.</summary>
        private void MarkWalkElementSnapshot(Document doc)
        {
            if (_pending.TryGetValue(doc, out var p) && (p.Added.Count > 0 || p.Modified.Count > 0))
                _walkCoveredIds[doc] = new HashSet<ElementId>(p.Added.Concat(p.Modified));
            else
                _walkCoveredIds.Remove(doc);
        }

        /// <summary>Call once a snapshot/reconcile actually finishes (not on an abandoned/stale
        /// walk — an interrupted walk never wrote anything for the ids it would have covered, so
        /// nothing here may be discarded). Drops exactly the ids still recorded as covered (an id
        /// changed again after the snapshot was already removed from that set by
        /// <see cref="OnDocumentChanged"/>, so it survives here); anything else left in
        /// <c>_pending</c> gets a change-capture job as usual once <c>_walkOutstanding</c> no
        /// longer contains this document — the hash compare makes that a no-op wherever the walk
        /// already wrote the latest state.</summary>
        private void ClearWalkCoveredPending(Document doc)
        {
            // Dictionary.Remove(key, out value) is .NET Core-only — net48 (Revit 2024) has no
            // overload taking `out`, so TryGetValue then Remove instead.
            if (_walkCoveredIds.TryGetValue(doc, out var covered))
            {
                _walkCoveredIds.Remove(doc);
                if (_pending.TryGetValue(doc, out var p))
                {
                    p.Added.ExceptWith(covered);
                    p.Modified.ExceptWith(covered);
                }
            }
        }

        /// <summary>Rebuilds the whole-document node/grid/tagged-sheet indexes ONCE, right after
        /// a snapshot or reconcile finishes walking the same data anyway — the one place this
        /// cost is expected and paid rarely (model open, sync, reload), never per edit.</summary>
        private void RefreshIndexCache(Document doc)
        {
            _indexCache[doc] = new IndexCache
            {
                NodeIndex = BuildNodeIndex(doc),
                GridLines = BuildGridLines(doc),
                TaggedSheets = RecordBuilder.BuildTaggedSheetIndex(doc),
            };
        }

        private IEnumerator<bool> ChangeCaptureJob(
            Document doc, ModelLogWriter writer, PendingChange change, Func<bool> isStale, Action onDone)
        {
            // onDone in a finally: an abandoned or throwing job must still clear the "queued" flag,
            // or no later edit to this document would ever get a change-capture job again.
            try
            {
            // Stale (closed, or a sync superseded it): the reconcile the sync's post-event
            // queued covers these ids anyway.
            if (isStale()) yield break;
            // Held until this batch writes a real record (see ModelLogWriter.BeginChange): most
            // edit batches that only touch views/annotation now write nothing at all.
            writer.BeginChange(RecordBuilder.BuildChangeHeader(
                change.TransactionNames, change.LastChangedBy,
                added: change.Added.Count, modified: change.Modified.Count, deleted: change.Deleted.Count),
                deleted: change.Deleted.Count);
            yield return true;

            // Reuse the whole-document indexes the last snapshot/reconcile built, instead of
            // re-walking every sheet/tag/room/space/level/grid in the document for THIS one
            // batch of changes — the fix for a real stall: an ordinary one-element edit used to
            // pay that whole-document cost every time. Only rebuilt here if no snapshot/
            // reconcile has run yet in this session (shouldn't normally happen — OnDocumentOpened
            // always queues one first) or if this batch touched a tag/sheet (rare; see below).
            if (!_indexCache.TryGetValue(doc, out var cache))
                _indexCache[doc] = cache = new IndexCache
                {
                    NodeIndex = BuildNodeIndex(doc),
                    GridLines = BuildGridLines(doc),
                    TaggedSheets = RecordBuilder.BuildTaggedSheetIndex(doc),
                };
            yield return true;

            if (TouchesTagOrSheet(doc, change.AddedOrModified))
            {
                cache.TaggedSheets = RecordBuilder.BuildTaggedSheetIndex(doc);
                yield return true;
            }

            var nodeIdByLevelOrSpace = cache.NodeIndex;
            var gridLines = cache.GridLines;
            var defaultPhase = ElementContextReader.DefaultPhase(doc, null);
            var taggedSheets = cache.TaggedSheets;
            var typeIds = new HashSet<ElementId>();
            void OnParamDef(Parameter p, bool isType)
            {
                var id = RecordBuilder.ParamDefId(p);
                if (id is null) return;
                writer.WriteIfUnseen(RecordKinds.Pdef, id, RecordBuilder.BuildParamDef(p, isType));
            }

            foreach (var elId in change.AddedOrModified)
            {
                if (isStale()) yield break;
                var el = doc.GetElement(elId);
                if (el is null) { yield return true; continue; }

                // Levels/spaces/grids are `node`/`grid` records, not `el` — an edit to one of
                // these must update ITS OWN record live, not wait for the next reconcile.
                if (el is Level lvl)
                {
                    var nid = RecordBuilder.NodeId(lvl.Id);
                    nodeIdByLevelOrSpace[lvl.Id] = nid;
                    writer.WriteIfChanged(RecordKinds.Node, nid, RecordBuilder.BuildLevelNode(lvl));
                    yield return true;
                    continue;
                }
                if (el is SpatialElement space)
                {
                    ElementId? levelId = null;
                    try { levelId = space.LevelId; } catch { }
                    var nid = RecordBuilder.NodeId(space.Id);
                    nodeIdByLevelOrSpace[space.Id] = nid;
                    writer.WriteIfChanged(RecordKinds.Node, nid, RecordBuilder.BuildSpaceNode(space, levelId));
                    yield return true;
                    continue;
                }
                if (el is Grid grid)
                {
                    writer.WriteIfChanged(RecordKinds.Grid, grid.UniqueId, RecordBuilder.BuildGrid(grid));
                    if (grid.Curve is Line gridLine) gridLines.Add((grid.Name, gridLine));
                    yield return true;
                    continue;
                }

                if (!RecordBuilder.IsLoggableModelElement(el)) { yield return true; continue; }

                var typeElId = el.GetTypeId();
                if (typeElId != ElementId.InvalidElementId && typeIds.Add(typeElId) &&
                    doc.GetElement(typeElId) is ElementType type)
                {
                    writer.WriteIfChanged(RecordKinds.Type, RecordBuilder.TypeId(typeElId),
                        RecordBuilder.BuildType(type, OnParamDef));
                }

                var fields = RecordBuilder.BuildElementFields(el, nodeIdByLevelOrSpace, gridLines, defaultPhase, OnParamDef, taggedSheets);
                writer.WriteIfChanged(RecordKinds.El, el.UniqueId, fields);
                yield return true;
            }

            // Deletions themselves are NOT written here — a deleted element's UniqueId can't be
            // resolved any more (see PendingChange.Deleted's own comment). The chg record above
            // already carries the deleted COUNT; the next reconcile (on open or sync) writes the
            // actual `del` records by comparing the hash cache's known ids against a fresh walk.
            }
            finally
            {
                writer.EndChange();
                onDone();
            }
        }

        /// <summary>The full-model walk shared by snapshot and reconcile: definitions,
        /// spatial tree, grids, materials, types, elements, sheets, revisions, links.
        /// <paramref name="forceFullState"/> and <paramref name="detectDeletions"/> are
        /// independent: a snapshot writes full state but skips deletion detection (nothing to
        /// compare against yet, and every element is new by definition); an ordinary reconcile
        /// does the opposite (only diffs changed since the hash cache, but always looks for
        /// stale elements); a reconcile forced full-state by a producer-version change does
        /// BOTH — the handoff's "self-healing" property (catches edits made while the connector
        /// wasn't running, or by another user, regardless of whether DocumentChanged ever
        /// reported them) plus backfilling whatever the new version adds.</summary>
        private IEnumerable<bool> WalkModel(
            Document doc, ModelLogWriter writer, bool forceFullState, bool detectDeletions, Func<bool> isStale)
        {
            // Snapshot taken before ANY section below (levels/rooms/grids are walked before the
            // element section) — see MarkWalkElementSnapshot's own doc comment for why this must
            // be the walk's very first step.
            MarkWalkElementSnapshot(doc);

            // Every section below takes its list of ids (or, for categories, objects) in ONE step
            // with no `yield` inside, then walks that plain list, looking each element up fresh and
            // skipping any that no longer exist. A `foreach` over a live FilteredElementCollector
            // with a `yield` inside it kept Revit's native element iterator open across idle ticks
            // — the exact FilteredElementIterator.MoveNext the reported crash died in.
            var categories = new List<Category>();
            foreach (Category cat in doc.Settings.Categories) categories.Add(cat);
            foreach (var cat in categories)
            {
                if (isStale()) yield break;
                writer.WriteIfUnseen(RecordKinds.Cat, RecordBuilder.CategoryId(cat), RecordBuilder.BuildCategory(cat));
                yield return true;
            }

            var nodeIdByLevelOrSpace = new Dictionary<ElementId, string>();
            foreach (var id in Ids(new FilteredElementCollector(doc).OfClass(typeof(Level))))
            {
                if (isStale()) yield break;
                if (doc.GetElement(id) is Level lvl)
                {
                    var nid = RecordBuilder.NodeId(lvl.Id);
                    nodeIdByLevelOrSpace[lvl.Id] = nid;
                    writer.WriteIfChanged(RecordKinds.Node, nid, RecordBuilder.BuildLevelNode(lvl), forceFullState);
                }
                yield return true;
            }

            var spaceIds = Ids(new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType());
            spaceIds.AddRange(Ids(new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_MEPSpaces).WhereElementIsNotElementType()));
            foreach (var id in spaceIds)
            {
                if (isStale()) yield break;
                var r = doc.GetElement(id);
                if (r is not null)
                {
                    ElementId? levelId = null;
                    try { levelId = r.LevelId; } catch { }
                    var nid = RecordBuilder.NodeId(r.Id);
                    nodeIdByLevelOrSpace[r.Id] = nid;
                    writer.WriteIfChanged(RecordKinds.Node, nid, RecordBuilder.BuildSpaceNode(r, levelId), forceFullState);
                }
                yield return true;
            }

            var gridLines = new List<(string, Line)>();
            foreach (var id in Ids(new FilteredElementCollector(doc).OfClass(typeof(Grid))))
            {
                if (isStale()) yield break;
                if (doc.GetElement(id) is Grid g)
                {
                    writer.WriteIfChanged(RecordKinds.Grid, g.UniqueId, RecordBuilder.BuildGrid(g), forceFullState);
                    if (g.Curve is Line line) gridLines.Add((g.Name, line));
                }
                yield return true;
            }

            foreach (var id in Ids(new FilteredElementCollector(doc).OfClass(typeof(Material))))
            {
                if (isStale()) yield break;
                if (doc.GetElement(id) is Material mat)
                    writer.WriteIfChanged(RecordKinds.Mat, RecordBuilder.MaterialId(mat.Id), RecordBuilder.BuildMaterial(mat), forceFullState);
                yield return true;
            }

            var defaultPhase = ElementContextReader.DefaultPhase(doc, null);
            var taggedSheets = RecordBuilder.BuildTaggedSheetIndex(doc);
            var typeIds = new HashSet<ElementId>();
            void OnParamDef(Parameter p, bool isType)
            {
                var id = RecordBuilder.ParamDefId(p);
                if (id is null) return;
                writer.WriteIfUnseen(RecordKinds.Pdef, id, RecordBuilder.BuildParamDef(p, isType));
            }

            // Model elements only (docs/MODEL_LOG.md's fix #1: a raw whole-document walk logs
            // area boundaries, sketches, sun path, dimensions, views, … as noise alongside the
            // actual building elements) — rooms/spaces/areas were already logged above, as
            // `node` records, not here.
            var currentIds = new HashSet<string>();
            foreach (var id in Ids(new FilteredElementCollector(doc).WhereElementIsNotElementType()))
            {
                if (isStale()) yield break;
                var el = doc.GetElement(id);
                if (el is null || !RecordBuilder.IsLoggableModelElement(el)) { yield return true; continue; }
                currentIds.Add(el.UniqueId);

                var typeElId = el.GetTypeId();
                if (typeElId != ElementId.InvalidElementId && typeIds.Add(typeElId) &&
                    doc.GetElement(typeElId) is ElementType type)
                {
                    writer.WriteIfChanged(RecordKinds.Type, RecordBuilder.TypeId(typeElId),
                        RecordBuilder.BuildType(type, OnParamDef), forceFullState);
                }

                var fields = RecordBuilder.BuildElementFields(el, nodeIdByLevelOrSpace, gridLines, defaultPhase, OnParamDef, taggedSheets);
                writer.WriteIfChanged(RecordKinds.El, el.UniqueId, fields, forceFullState);
                yield return true;
            }

            if (detectDeletions)
            {
                foreach (var staleUid in writer.KnownElementIdsNotIn(currentIds))
                {
                    if (isStale()) yield break;
                    // The numeric ElementId no longer resolves (the element is gone) and wasn't
                    // tracked separately from its UniqueId — WriteDelete omits it rather than
                    // fabricate one.
                    writer.WriteDelete(staleUid);
                    yield return true;
                }
            }

            foreach (var id in Ids(new FilteredElementCollector(doc).OfClass(typeof(ViewSheet))))
            {
                if (isStale()) yield break;
                if (doc.GetElement(id) is ViewSheet sheet)
                    writer.WriteIfChanged(RecordKinds.Sheet, sheet.UniqueId, RecordBuilder.BuildSheet(sheet), forceFullState);
                yield return true;
            }
            foreach (var id in Ids(new FilteredElementCollector(doc).OfClass(typeof(Revision))))
            {
                if (isStale()) yield break;
                if (doc.GetElement(id) is Revision rev)
                    writer.WriteIfChanged(RecordKinds.Rev, rev.UniqueId, RecordBuilder.BuildRevision(rev), forceFullState);
                yield return true;
            }
            foreach (var id in Ids(new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance))))
            {
                if (isStale()) yield break;
                if (doc.GetElement(id) is RevitLinkInstance link)
                {
                    ModelFacts? linkedFacts = null;
                    try { var linkDoc = link.GetLinkDocument(); if (linkDoc is not null) linkedFacts = ModelFacts.From(linkDoc); } catch { }
                    writer.WriteIfChanged(RecordKinds.Link, link.UniqueId, RecordBuilder.BuildLink(link, linkedFacts), forceFullState);
                }
                yield return true;
            }
        }

        /// <summary>Materializes a collector's matching ids into a plain list in one call, so no
        /// native iterator outlives the current idle slice.</summary>
        private static List<ElementId> Ids(FilteredElementCollector collector) =>
            new List<ElementId>(collector.ToElementIds());

        /// <summary>Cheap, per-changed-id check (never a document walk) for whether this batch
        /// needs the tagged-sheet index rebuilt — true only when a tag or sheet itself was
        /// touched, which is rare compared to ordinary model edits.</summary>
        private static bool TouchesTagOrSheet(Document doc, IEnumerable<ElementId> ids)
        {
            foreach (var id in ids)
            {
                var el = doc.GetElement(id);
                if (el is IndependentTag or ViewSheet) return true;
            }
            return false;
        }

        private static Dictionary<ElementId, string> BuildNodeIndex(Document doc)
        {
            var index = new Dictionary<ElementId, string>();
            foreach (var lvl in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
                index[lvl.Id] = RecordBuilder.NodeId(lvl.Id);
            var spaces = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                .Concat(new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_MEPSpaces).WhereElementIsNotElementType());
            foreach (var r in spaces) index[r.Id] = RecordBuilder.NodeId(r.Id);
            return index;
        }

        private static List<(string Name, Line Line)> BuildGridLines(Document doc)
        {
            var lines = new List<(string, Line)>();
            foreach (var g in new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>())
                if (g.Curve is Line line) lines.Add((g.Name, line));
            return lines;
        }

        private static string? SafeVersionGuid(Document doc)
        {
            try { return Document.GetDocumentVersion(doc)?.VersionGUID.ToString(); }
            catch { return null; }
        }

        private static string? SafeRevitVersion(Document doc)
        {
            try { return doc.Application.VersionNumber; }
            catch { return null; }
        }

        private static int CountElements(Document doc)
        {
            try { return new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount(); }
            catch { return 0; }
        }
    }
}
