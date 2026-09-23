using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;

namespace Loam.Revit.Connector.ModelLog
{
    /// <summary>
    /// One instance per open model. Owns the on-disk log for that model: the active segment,
    /// <c>state.json</c> (hash cache + lastSeq + last checkpoint), and the writer lock. Every
    /// write path in the connector — snapshot, reconcile, live change capture — goes through
    /// this class so segment rotation, gzip, crash safety and the seq counter are handled in
    /// exactly one place. See docs/MODEL_LOG.md for the on-disk format this produces.
    ///
    /// NOT thread-safe by itself: the connector only ever calls this from Revit's own idle-time
    /// slices (see src/ModelLogCapture/IdleSliceRunner.cs), which already serializes every call
    /// onto one thread — this class assumes that and takes no lock of its own beyond the
    /// cross-process <see cref="WriterLock"/>.
    /// </summary>
    public sealed class ModelLogWriter : IDisposable
    {
        public const string SchemaVersion = "model-log/1";

        private readonly LogSegmentWriter _segment;
        private readonly WriterLock? _lock;
        private readonly string _statePath;
        private readonly ModelLogState _state;
        private bool _headerWrittenThisSegment;

        /// <summary>True when another Revit session already holds this model's writer lock —
        /// the caller must not write anything and should log that it didn't (the handoff's
        /// crash-safety rule #4: "a second Revit session with the same model open doesn't
        /// write, and logs that it didn't").</summary>
        public bool LockHeldElsewhere { get; }

        public long LastSeq => _state.LastSeq;
        public bool LastCheckpointClosed => _state.LastCheckpointClosed;
        public string LogDirectory { get; }

        public ModelLogWriter(string modelLogRoot, string modelId)
        {
            LogDirectory = Path.Combine(modelLogRoot, SanitizeForPath(modelId));
            Directory.CreateDirectory(LogDirectory);

            _lock = WriterLock.TryAcquire(Path.Combine(LogDirectory, "writer.lock"));
            LockHeldElsewhere = _lock is null;

            _statePath = Path.Combine(LogDirectory, "state.json");
            _state = StateStore.Load(_statePath);

            _segment = new LogSegmentWriter(LogDirectory, _state.CurrentSegment);
            _state.CurrentSegment = _segment.SegmentNumber;
        }

        private static string SanitizeForPath(string id)
        {
            var bad = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(id.Length);
            foreach (var c in id) sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
            return sb.ToString();
        }

        // ── Raw append (every write goes through this) ──────────────────────────

        /// <summary>Appends one record of kind <paramref name="kind"/> with the given fields,
        /// stamping <c>seq</c>/<c>ts</c>. Flushes the line, THEN persists state.json — never the
        /// other order: a crash between the two just re-writes the same state on the next
        /// reconcile, a harmless duplicate, per the handoff's crash-safety rule #2.</summary>
        public long Append(string kind, JsonObject fields)
        {
            _state.LastSeq++;
            var line = new JsonObject
            {
                ["seq"] = _state.LastSeq,
                ["ts"] = Timestamp(),
                ["k"] = kind,
            };
            foreach (var kv in fields) line[kv.Key] = kv.Value?.DeepClone();
            _segment.AppendLine(line.ToJsonString());
            SaveState();
            return _state.LastSeq;
        }

        // ── Header / segment rotation ────────────────────────────────────────────

        /// <summary>First line of every segment. Callers pass the same header fields on every
        /// rotation so a reader can open any segment on its own (the handoff's rotation rule) —
        /// model identity, producer/version, display units, coordinates, the field-role map.
        /// A no-op once already written for the current segment.</summary>
        public void WriteHeader(JsonObject headerFields)
        {
            if (_headerWrittenThisSegment) return;
            var fields = new JsonObject { ["schema"] = SchemaVersion };
            foreach (var kv in headerFields) fields[kv.Key] = kv.Value?.DeepClone();
            Append(RecordKinds.Header, fields);
            _headerWrittenThisSegment = true;
        }

        /// <summary>Call before starting a full snapshot/reconcile pass: rotates first if the
        /// active segment is already past the size threshold, then (re-)writes the header so a
        /// snapshot always begins a segment a reader can open on its own.</summary>
        public void BeginSnapshot(JsonObject headerFields)
        {
            if (_segment.ShouldRotate()) RotateAndReheader(headerFields);
            _headerWrittenThisSegment = false; // a snapshot always re-asserts the header
            WriteHeader(headerFields);
        }

        /// <summary>Call periodically during live change capture (not mid-snapshot) — rotates
        /// only if the size threshold was crossed since the last check.</summary>
        public void RotateIfNeeded(JsonObject headerFields)
        {
            if (_segment.ShouldRotate()) RotateAndReheader(headerFields);
        }

        private void RotateAndReheader(JsonObject headerFields)
        {
            _segment.Rotate();
            _state.CurrentSegment = _segment.SegmentNumber;
            _headerWrittenThisSegment = false;
            WriteHeader(headerFields);
        }

        // ── Checkpoints and gaps ──────────────────────────────────────────────────

        /// <summary>Checkpoint: end of snapshot/reconcile, after sync, or on close.
        /// <paramref name="closed"/> true only on <c>DocumentClosing</c> — its presence (or
        /// absence, checked on the NEXT open) is what tells a reader "Revit closed cleanly" from
        /// "the connector crashed mid-session".</summary>
        public void WriteCheckpoint(bool complete, string? modelVersion, int? elementCount, bool closed)
        {
            var fields = new JsonObject
            {
                ["complete"] = complete,
                ["closed"] = closed,
                ["lastSeq"] = _state.LastSeq,
            };
            if (modelVersion is not null) fields["modelVersion"] = modelVersion;
            if (elementCount is not null) fields["elementCount"] = elementCount.Value;
            Append(RecordKinds.Checkpoint, fields);

            _state.LastCheckpointClosed = closed;
            _state.LastModelVersion = modelVersion;
            SaveState();
        }

        /// <summary>Called once at startup, before the first snapshot/reconcile pass, when the
        /// previous session left no closed checkpoint — records that the connector might have
        /// missed events while it wasn't running (or crashed mid-session). The reconcile that
        /// follows closes the gap with its own checkpoint. A no-op on a brand-new log (nothing
        /// to have a gap in yet) or when the last checkpoint was already closed.</summary>
        public void WriteGapIfNeeded(string reason)
        {
            if (_state.LastCheckpointClosed) return;
            if (_state.LastSeq == 0) return;
            Append(RecordKinds.Gap, new JsonObject
            {
                ["fromSeq"] = _state.LastSeq + 1,
                ["reason"] = reason,
            });
        }

        // ── Hash-cache-backed change detection (el/type/node/grid/mat/sheet/rev/link) ──────

        /// <summary>Writes a record of the given family only if at least one of
        /// <paramref name="fullFields"/>'s top-level field-groups changed since the last write
        /// for this id (or the id has never been seen) — true when it wrote, false when nothing
        /// changed. On a first-time write (or when <paramref name="forceFullState"/> is set, for
        /// a snapshot pass), the FULL state is written; otherwise only the field-groups whose
        /// hash differs are written (new values, never a value-level diff — the handoff's rule
        /// #1), plus an <c>unset</c> array naming any field-group that disappeared entirely
        /// (e.g. every instance parameter was cleared).</summary>
        public bool WriteIfChanged(string family, string id, JsonObject fullFields, bool forceFullState = false)
        {
            var newHashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in fullFields)
                if (kv.Value is not null)
                    newHashes[kv.Key] = RecordHash.Of(kv.Value);

            var prevHashes = _state.Cache.Get(family, id);

            if (prevHashes is null || forceFullState)
            {
                Append(family, WithId(fullFields, id));
                _state.Cache.Set(family, id, newHashes);
                return true;
            }

            var changed = new JsonObject();
            JsonArray? unset = null;
            foreach (var key in newHashes.Keys)
                if (!prevHashes.TryGetValue(key, out var oldHash) || oldHash != newHashes[key])
                    changed[key] = fullFields[key]!.DeepClone();
            foreach (var key in prevHashes.Keys)
                if (!newHashes.ContainsKey(key))
                {
                    unset ??= new JsonArray();
                    unset.Add(key);
                }

            if (changed.Count == 0 && unset is null) return false;

            if (unset is not null) changed["unset"] = unset;
            Append(family, WithId(changed, id));
            _state.Cache.Set(family, id, newHashes);
            return true;
        }

        /// <summary>Definitions (pdef/cat) are written once, the first time an id is seen, and
        /// never re-checked afterwards — returns true (and marks it seen) only the first
        /// time.</summary>
        public bool WriteIfUnseen(string family, string id, JsonObject fields)
        {
            var seen = family == RecordKinds.Pdef ? _state.Cache.PdefSeen : _state.Cache.CatSeen;
            if (!seen.Add(id)) return false;
            Append(family, WithId(fields, id));
            return true;
        }

        private static JsonObject WithId(JsonObject fields, string id)
        {
            if (fields["id"] is not null) return fields;
            var withId = new JsonObject { ["id"] = id };
            foreach (var kv in fields) withId[kv.Key] = kv.Value?.DeepClone();
            return withId;
        }

        /// <summary>Element ids known from a previous session/segment but absent from the
        /// current walk — the reconcile's deletion candidates. Compares against the
        /// <c>el</c> family only (a deleted element's type record, if any, is left for the
        /// reconcile to independently decide is still referenced or not).</summary>
        public IReadOnlyList<string> KnownElementIdsNotIn(ISet<string> currentIds)
        {
            var stale = new List<string>();
            foreach (var id in _state.Cache.KnownIds(RecordKinds.El))
                if (!currentIds.Contains(id)) stale.Add(id);
            return stale;
        }

        public void WriteDelete(string uniqueId, long elementId)
        {
            Append(RecordKinds.Del, new JsonObject { ["id"] = uniqueId, ["eid"] = elementId });
            _state.Cache.Remove(RecordKinds.El, uniqueId);
        }

        private void SaveState() => StateStore.Save(_statePath, _state);

        private static string Timestamp() =>
            DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        public void Dispose()
        {
            _segment.Dispose();
            _lock?.Dispose();
        }
    }
}
