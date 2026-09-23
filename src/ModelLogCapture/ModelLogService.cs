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
    /// <item>Model opened, log exists → gap (if the previous session didn't close cleanly),
    /// then reconcile (write only what differs from the hash cache, delete what's gone), then a
    /// checkpoint.</item>
    /// <item>User edits (<c>DocumentChanged</c>) → NEVER processed inline (the handoff's "never
    /// block the user" rule) — only the raw ids/transaction names/editor are recorded; idle time
    /// drains the queue into a <c>chg</c> record plus one record per touched id.</item>
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

        public ModelLogService(string modelLogRoot, string producerVersion, Action<string, double, int>? onJobFinished = null)
        {
            _modelLogRoot = modelLogRoot;
            _producerVersion = producerVersion;
            _idle = new IdleSliceRunner(onJobFinished ?? ((_, __, ___) => { }));
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

            if (writer.LastSeq == 0)
                _idle.Enqueue("snapshot", SnapshotJob(doc, writer));
            else
            {
                writer.WriteGapIfNeeded("no closed checkpoint from the previous session");
                _idle.Enqueue("reconcile-on-open", ReconcileJob(doc, writer));
            }
        }

        public void OnDocumentSyncedOrReloaded(Document doc)
        {
            if (!_writers.TryGetValue(doc, out var writer)) return;
            _idle.Enqueue("reconcile-on-sync", ReconcileJob(doc, writer));
        }

        public void OnDocumentClosing(Document doc)
        {
            if (!_writers.TryGetValue(doc, out var writer)) return;
            var version = SafeVersionGuid(doc);
            writer.WriteCheckpoint(complete: true, modelVersion: version, elementCount: null, closed: true);
            writer.Dispose();
            _writers.Remove(doc);
            _pending.Remove(doc);
            _changeJobQueued.Remove(doc);
        }

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
        }

        /// <summary>Call on every Idling tick: runs one slice of whatever job is at the front of
        /// the queue, and enqueues a change-capture job for any document with pending
        /// <c>DocumentChanged</c> data that doesn't already have one queued.</summary>
        public void OnIdling()
        {
            foreach (var kv in _pending)
            {
                var doc = kv.Key;
                if (kv.Value.Added.Count == 0 && kv.Value.Modified.Count == 0 && kv.Value.Deleted.Count == 0) continue;
                if (_changeJobQueued.Contains(doc)) continue;
                if (!_writers.TryGetValue(doc, out var writer)) continue;

                var change = kv.Value;
                _pending[doc] = new PendingChange(); // fresh accumulator for what happens next
                _changeJobQueued.Add(doc);
                _idle.Enqueue("change-capture", ChangeCaptureJob(doc, writer, change, () => _changeJobQueued.Remove(doc)));
            }

            _idle.RunSlice();
        }

        // ── Jobs ─────────────────────────────────────────────────────────────────

        private IEnumerator<bool> SnapshotJob(Document doc, ModelLogWriter writer)
        {
            var facts = ModelFacts.From(doc);
            var header = RecordBuilder.BuildHeader(doc, facts, _producerVersion);
            writer.BeginSnapshot(header);
            writer.Append(RecordKinds.Project, RecordBuilder.BuildProject(doc));
            yield return true;

            foreach (var step in WalkModel(doc, writer, forceFullState: true)) yield return step;

            var version = SafeVersionGuid(doc);
            var count = CountElements(doc);
            writer.WriteCheckpoint(complete: true, modelVersion: version, elementCount: count, closed: false);
        }

        private IEnumerator<bool> ReconcileJob(Document doc, ModelLogWriter writer)
        {
            var facts = ModelFacts.From(doc);
            var header = RecordBuilder.BuildHeader(doc, facts, _producerVersion);
            writer.RotateIfNeeded(header);

            foreach (var step in WalkModel(doc, writer, forceFullState: false)) yield return step;

            var version = SafeVersionGuid(doc);
            var count = CountElements(doc);
            writer.WriteCheckpoint(complete: true, modelVersion: version, elementCount: count, closed: false);
        }

        private IEnumerator<bool> ChangeCaptureJob(
            Document doc, ModelLogWriter writer, PendingChange change, Action onDone)
        {
            writer.Append(RecordKinds.Chg, RecordBuilder.BuildChangeHeader(
                change.TransactionNames, change.LastChangedBy,
                added: change.Added.Count, modified: change.Modified.Count, deleted: change.Deleted.Count));
            yield return true;

            var nodeIdByLevelOrSpace = BuildNodeIndex(doc);
            var gridLines = BuildGridLines(doc);
            var defaultPhase = ElementContextReader.DefaultPhase(doc, null);
            var typeIds = new HashSet<ElementId>();
            void OnParamDef(Parameter p, bool isType)
            {
                var id = RecordBuilder.ParamDefId(p);
                if (id is null) return;
                writer.WriteIfUnseen(RecordKinds.Pdef, id, RecordBuilder.BuildParamDef(p, isType));
            }

            foreach (var elId in change.AddedOrModified)
            {
                var el = doc.GetElement(elId);
                if (el is null || el.Category is null) { yield return true; continue; }

                var typeElId = el.GetTypeId();
                if (typeElId != ElementId.InvalidElementId && typeIds.Add(typeElId) &&
                    doc.GetElement(typeElId) is ElementType type)
                {
                    writer.WriteIfChanged(RecordKinds.Type, RecordBuilder.TypeId(typeElId),
                        RecordBuilder.BuildType(type, OnParamDef));
                }

                var fields = RecordBuilder.BuildElementFields(el, nodeIdByLevelOrSpace, gridLines, defaultPhase, OnParamDef);
                writer.WriteIfChanged(RecordKinds.El, el.UniqueId, fields);
                yield return true;
            }

            // Deletions themselves are NOT written here — a deleted element's UniqueId can't be
            // resolved any more (see PendingChange.Deleted's own comment). The chg record above
            // already carries the deleted COUNT; the next reconcile (on open or sync) writes the
            // actual `del` records by comparing the hash cache's known ids against a fresh walk.

            onDone();
        }

        /// <summary>The full-model walk shared by snapshot and reconcile: definitions,
        /// spatial tree, grids, materials, types, elements, sheets, revisions, links. On a
        /// reconcile (<paramref name="forceFullState"/> false), also detects and deletes
        /// elements that used to be in the log but are gone from the model now — the
        /// handoff's "self-healing" property: this catches edits made while the connector
        /// wasn't running, or by another user, regardless of whether DocumentChanged ever
        /// reported them.</summary>
        private IEnumerable<bool> WalkModel(Document doc, ModelLogWriter writer, bool forceFullState)
        {
            foreach (Category cat in doc.Settings.Categories)
            {
                writer.WriteIfUnseen(RecordKinds.Cat, RecordBuilder.CategoryId(cat), RecordBuilder.BuildCategory(cat));
                yield return true;
            }

            var nodeIdByLevelOrSpace = new Dictionary<ElementId, string>();
            foreach (var lvl in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
            {
                var nid = RecordBuilder.NodeId(lvl.Id);
                nodeIdByLevelOrSpace[lvl.Id] = nid;
                writer.WriteIfChanged(RecordKinds.Node, nid, RecordBuilder.BuildLevelNode(lvl), forceFullState);
                yield return true;
            }

            var spaces = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                .Concat(new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_MEPSpaces).WhereElementIsNotElementType());
            foreach (var r in spaces)
            {
                ElementId? levelId = null;
                try { levelId = r.LevelId; } catch { }
                var nid = RecordBuilder.NodeId(r.Id);
                nodeIdByLevelOrSpace[r.Id] = nid;
                writer.WriteIfChanged(RecordKinds.Node, nid, RecordBuilder.BuildSpaceNode(r, levelId), forceFullState);
                yield return true;
            }

            var gridLines = new List<(string, Line)>();
            foreach (var g in new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>())
            {
                writer.WriteIfChanged(RecordKinds.Grid, g.UniqueId, RecordBuilder.BuildGrid(g), forceFullState);
                if (g.Curve is Line line) gridLines.Add((g.Name, line));
                yield return true;
            }

            foreach (Material mat in new FilteredElementCollector(doc).OfClass(typeof(Material)))
            {
                writer.WriteIfChanged(RecordKinds.Mat, RecordBuilder.MaterialId(mat.Id), RecordBuilder.BuildMaterial(mat), forceFullState);
                yield return true;
            }

            var defaultPhase = ElementContextReader.DefaultPhase(doc, null);
            var typeIds = new HashSet<ElementId>();
            void OnParamDef(Parameter p, bool isType)
            {
                var id = RecordBuilder.ParamDefId(p);
                if (id is null) return;
                writer.WriteIfUnseen(RecordKinds.Pdef, id, RecordBuilder.BuildParamDef(p, isType));
            }

            var currentIds = new HashSet<string>();
            foreach (var el in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                if (el.Category is null) { yield return true; continue; }
                currentIds.Add(el.UniqueId);

                var typeElId = el.GetTypeId();
                if (typeElId != ElementId.InvalidElementId && typeIds.Add(typeElId) &&
                    doc.GetElement(typeElId) is ElementType type)
                {
                    writer.WriteIfChanged(RecordKinds.Type, RecordBuilder.TypeId(typeElId),
                        RecordBuilder.BuildType(type, OnParamDef), forceFullState);
                }

                var fields = RecordBuilder.BuildElementFields(el, nodeIdByLevelOrSpace, gridLines, defaultPhase, OnParamDef);
                writer.WriteIfChanged(RecordKinds.El, el.UniqueId, fields, forceFullState);
                yield return true;
            }

            if (!forceFullState)
            {
                foreach (var staleUid in writer.KnownElementIdsNotIn(currentIds))
                {
                    // The numeric ElementId no longer resolves (the element is gone) and wasn't
                    // tracked separately from its UniqueId — WriteDelete omits it rather than
                    // fabricate one.
                    writer.WriteDelete(staleUid);
                    yield return true;
                }
            }

            foreach (ViewSheet sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)))
            {
                writer.WriteIfChanged(RecordKinds.Sheet, sheet.UniqueId, RecordBuilder.BuildSheet(sheet), forceFullState);
                yield return true;
            }
            foreach (Revision rev in new FilteredElementCollector(doc).OfClass(typeof(Revision)))
            {
                writer.WriteIfChanged(RecordKinds.Rev, rev.UniqueId, RecordBuilder.BuildRevision(rev), forceFullState);
                yield return true;
            }
            foreach (RevitLinkInstance link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)))
            {
                ModelFacts? linkedFacts = null;
                try { var linkDoc = link.GetLinkDocument(); if (linkDoc is not null) linkedFacts = ModelFacts.From(linkDoc); } catch { }
                writer.WriteIfChanged(RecordKinds.Link, link.UniqueId, RecordBuilder.BuildLink(link, linkedFacts), forceFullState);
                yield return true;
            }
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

        private static int CountElements(Document doc)
        {
            try { return new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount(); }
            catch { return 0; }
        }
    }
}
