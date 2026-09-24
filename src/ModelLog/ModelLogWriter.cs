using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;

namespace Loam.Revit.Connector.ModelLog
{
    /// <summary>
    /// One instance per open model. Owns the on-disk log for that model: the active segment,
    /// state (a state.json base plus a state.&lt;gen&gt;.jsonl delta journal — hash cache + lastSeq
    /// + last checkpoint), and the writer lock. Every
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

        // state.json (the BASE) holds the whole hash cache (tens of MB on a large model), so it's
        // rewritten only by a compaction, not after every record: doing that per record made a
        // snapshot quadratic in disk writes (measured: 4,000 elements → 6.1 GB written for a 7.3
        // MB log). Every mutation instead buffers a small delta op (see StateJournal) in memory;
        // FlushState() appends the buffer to state.<gen>.jsonl (cheap: append + flush, no base
        // rewrite), and is called after every idle slice plus at once on a checkpoint, session
        // record, segment rotation and Dispose — those four also then compact (rewrite the base,
        // bump the generation, drop the old journal) if the journal has grown past
        // MinCompactionBytes relative to the base. If Revit dies in between, the base+journal on
        // disk is behind the log: LastSeq/segment are recovered from the log itself on the next
        // open, and the stale hashes just make the next reconcile re-write those records — the
        // harmless duplicates the handoff's crash-safety rule #2 already allows.
        internal static long MinCompactionBytes = 1L << 20;
        private readonly List<string> _journalBuffer = new();

        // A change batch's `chg` record, held until the batch actually writes a record (or has
        // deletions): an edit that only touched views/annotation writes nothing at all.
        private JsonObject? _pendingChange;

        /// <summary>True when another Revit session already holds this model's writer lock —
        /// the caller must not write anything and should log that it didn't (the handoff's
        /// crash-safety rule #4: "a second Revit session with the same model open doesn't
        /// write, and logs that it didn't").</summary>
        public bool LockHeldElsewhere { get; }

        public long LastSeq => _state.LastSeq;
        public bool LastCheckpointClosed => _state.LastCheckpointClosed;
        public string LogDirectory { get; }

        /// <summary>The producer (connector) version recorded by the LAST session record this
        /// log ever got, or null if this log predates the <c>session</c> record kind — the
        /// signal <see cref="ModelLogCapture.ModelLogService.OnDocumentOpened"/> compares its own
        /// version against to decide whether an upgrade changed what gets logged and a forced
        /// full reconcile (with deletion detection) is owed, per docs/MODEL_LOG.md.</summary>
        public string? LastProducerVersion => _state.LastProducerVersion;

        public ModelLogWriter(string modelLogRoot, string modelId)
        {
            LogDirectory = Path.Combine(modelLogRoot, SanitizeForPath(modelId));
            Directory.CreateDirectory(LogDirectory);

            _lock = WriterLock.TryAcquire(Path.Combine(LogDirectory, "writer.lock"));
            LockHeldElsewhere = _lock is null;

            _statePath = Path.Combine(LogDirectory, "state.json");
            _state = StateStore.Load(_statePath);
            if (!LockHeldElsewhere) StateJournal.DeleteStaleGenerations(_statePath, _state.JournalGeneration);

            // state (base+journal) may be behind the log (LastSeq is only journaled at a
            // checkpoint/session/rotation, not per record): never reopen an older segment number,
            // and never reuse a seq already on disk.
            var segment = Math.Max(_state.CurrentSegment, LogSegmentWriter.DiscoverLatestSegment(LogDirectory));
            var lastSeqOnDisk = LogSegmentWriter.LastSeqIn(Path.Combine(LogDirectory, $"{segment:D6}.jsonl"));
            if (lastSeqOnDisk > _state.LastSeq) _state.LastSeq = lastSeqOnDisk;

            _segment = new LogSegmentWriter(LogDirectory, segment);
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
        /// stamping <c>seq</c>/<c>ts</c>. Flushes the line, THEN (at the callers that buffer a
        /// journal op alongside it) persists state — never the other order: a crash between the
        /// two just re-writes the same state on the next reconcile, a harmless duplicate, per the
        /// handoff's crash-safety rule #2.</summary>
        public long Append(string kind, JsonObject fields)
        {
            if (_pendingChange is not null && kind != RecordKinds.Chg)
            {
                var chg = _pendingChange;
                _pendingChange = null;
                Append(RecordKinds.Chg, chg);
            }
            _state.LastSeq++;
            var line = new JsonObject
            {
                ["seq"] = _state.LastSeq,
                ["ts"] = Timestamp(),
                ["k"] = kind,
            };
            foreach (var kv in fields) line[kv.Key] = kv.Value?.DeepClone();
            _segment.AppendLine(line.ToJsonString());
            return _state.LastSeq;
        }

        /// <summary>Starts one live-edit batch. Its <c>chg</c> record is written just before
        /// the batch's first real record, or right away when it deleted something (deletions
        /// never write a record of their own until the next reconcile, so the chg is their only
        /// trace); a batch that ends up writing nothing leaves the log untouched.</summary>
        public void BeginChange(JsonObject chgFields, int deleted)
        {
            _pendingChange = null;
            if (deleted > 0) Append(RecordKinds.Chg, chgFields);
            else _pendingChange = chgFields;
        }

        /// <summary>Ends the batch started by <see cref="BeginChange"/>, discarding its
        /// <c>chg</c> record if nothing was written.</summary>
        public void EndChange() => _pendingChange = null;

        /// <summary>Appends whatever's buffered (hash-cache/pdef/cat/meta deltas) to the
        /// journal and flushes. Cheap — never rewrites the base. Call after each idle slice.</summary>
        public void FlushState() => FlushJournalBuffer();

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
            _journalBuffer.Add(StateJournal.MetaOp(_state));
            FlushJournalBuffer();
            MaybeCompact();
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
            _journalBuffer.Add(StateJournal.MetaOp(_state));
            FlushJournalBuffer();
            MaybeCompact();
        }

        /// <summary>Call once per <c>DocumentOpened</c>, right after deciding whether this is a
        /// fresh log/version change (before the snapshot/reconcile it may have triggered) — a
        /// reader can then tell, from the <c>session</c> records alone, exactly which producer
        /// version wrote which records, without having to diff <c>header</c> records across
        /// segments. Also updates <see cref="LastProducerVersion"/> for the NEXT open to compare
        /// against.</summary>
        public void RecordSession(string producerVersion, string? revitVersion)
        {
            var fields = new JsonObject { ["producerVersion"] = producerVersion };
            if (revitVersion is not null) fields["revitVersion"] = revitVersion;
            Append(RecordKinds.Session, fields);

            _state.LastProducerVersion = producerVersion;
            _journalBuffer.Add(StateJournal.MetaOp(_state));
            FlushJournalBuffer();
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
                _journalBuffer.Add(StateJournal.HashSetOp(family, id, newHashes));
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
            _journalBuffer.Add(StateJournal.HashSetOp(family, id, newHashes));
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
            _journalBuffer.Add(family == RecordKinds.Pdef ? StateJournal.PdefSeenOp(id) : StateJournal.CatSeenOp(id));
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

        /// <summary><paramref name="elementId"/> is omitted (never a fabricated 0) when the
        /// caller doesn't have it any more — a reconcile detects a deletion purely from the
        /// UniqueId disappearing from a fresh walk, with no numeric id available at all.</summary>
        public void WriteDelete(string uniqueId, long? elementId = null)
        {
            var fields = new JsonObject { ["id"] = uniqueId };
            if (elementId is not null) fields["eid"] = elementId.Value;
            Append(RecordKinds.Del, fields);
            _state.Cache.Remove(RecordKinds.El, uniqueId);
            _journalBuffer.Add(StateJournal.HashRemoveOp(RecordKinds.El, uniqueId));
        }

        private void FlushJournalBuffer()
        {
            if (_journalBuffer.Count == 0) return;
            if (LockHeldElsewhere) { _journalBuffer.Clear(); return; } // never write the owning session's state
            StateJournal.Append(_statePath, _state.JournalGeneration, _journalBuffer);
            _journalBuffer.Clear();
        }

        /// <summary>Rewrites the base (state.json) from the in-memory state, bumps the journal
        /// generation, and drops the old journal — but only when the journal has grown past
        /// <see cref="MinCompactionBytes"/> relative to the base (a small journal is cheaper to
        /// keep appending to than to fold into a full rewrite). The base write is atomic (see
        /// StateStore.Save); a crash between it and the old-journal delete leaves a loadable
        /// state either way — base gen N + journal N (crash before the rename), or base gen N+1
        /// with the stale journal N left behind (crash after) — the next open ignores that stale
        /// journal and deletes it opportunistically (see StateJournal.DeleteStaleGenerations).
        /// Best-effort: an I/O failure here leaves the (still valid, already-flushed) journal at
        /// its old generation and must not fail whatever called this (a checkpoint, a rotation,
        /// or Dispose) — nothing is lost, just a rewrite deferred to the next opportunity.</summary>
        private void MaybeCompact()
        {
            if (LockHeldElsewhere) return;
            var baseBytes = File.Exists(_statePath) ? new FileInfo(_statePath).Length : 0;
            var threshold = Math.Max(MinCompactionBytes, baseBytes / 4);
            var journalBytes = StateJournal.SizeBytes(_statePath, _state.JournalGeneration);
            if (journalBytes <= threshold) return;

            var oldGeneration = _state.JournalGeneration;
            _state.JournalGeneration = oldGeneration + 1;
            try
            {
                StateStore.Save(_statePath, _state);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _state.JournalGeneration = oldGeneration; // save didn't land — keep the old journal current
                return;
            }
            StateJournal.DeleteGeneration(_statePath, oldGeneration);
        }

        private static string Timestamp() =>
            DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        public void Dispose()
        {
            try
            {
                _pendingChange = null;
                FlushJournalBuffer();
                MaybeCompact();
            }
            finally
            {
                // Always released, even if flushing/compacting the state above threw — an
                // unreleased lock or an open segment handle would outlive this process for no
                // benefit (compaction is already best-effort; nothing more is recoverable here).
                _segment.Dispose();
                _lock?.Dispose();
            }
        }
    }
}
