using System.IO;
using System.Text.Json;

namespace Loam.Revit.Connector.ModelLog
{
    /// <summary>
    /// Loads/saves the state.json BASE of a model's hash-cache state, and replays its matching
    /// delta journal (<see cref="StateJournal"/>) on load. state.json itself is only rewritten by
    /// a compaction (see ModelLogWriter.MaybeCompact) — most updates are cheap appends to the
    /// journal instead, per the handoff's crash-safety rule #2 ("update state only after the log
    /// line is flushed. If a crash lands in between, the next reconcile writes the same state
    /// again: a harmless duplicate, never a lost change"), which applies equally to a journal
    /// append or a base rewrite.
    /// </summary>
    public static class StateStore
    {
        // Compact: this file is the whole hash cache, rewritten on every compaction — indentation
        // roughly doubled it. Loading still accepts older indented files.
        private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

        /// <summary>Loads the base (or an empty state, for a fresh log or a corrupt/partial
        /// state.json) and replays the journal matching its JournalGeneration on top of it.</summary>
        public static ModelLogState Load(string path)
        {
            ModelLogState state;
            if (!File.Exists(path)) state = new ModelLogState();
            else
            {
                try
                {
                    var text = File.ReadAllText(path);
                    state = JsonSerializer.Deserialize<ModelLogState>(text, Options) ?? new ModelLogState();
                }
                catch
                {
                    // Corrupt/partial state.json (e.g. a crash mid-write before this atomic-
                    // rename scheme existed, or a hand edit) — start from an empty cache rather
                    // than fail to load the log at all. The next reconcile re-derives every hash
                    // from the live model, so nothing is lost except one round of "everything
                    // looks changed" writes.
                    state = new ModelLogState();
                }
            }
            StateJournal.Replay(path, state);
            return state;
        }

        /// <summary>Atomic write: serialize to a temp file in the same directory, then swap it
        /// into the real path. A crash mid-write leaves the OLD state.json intact — never a
        /// truncated or half-written one for <see cref="Load"/> to trip over next time. A leftover
        /// .tmp from an earlier interrupted save is never read (only <paramref name="path"/>
        /// itself is) and is harmlessly overwritten here (FileMode.Create truncates it).</summary>
        public static void Save(string path, ModelLogState state)
        {
            var tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                JsonSerializer.Serialize(fs, state, Options);

            if (File.Exists(path))
                // Atomic swap (NTFS and Linux both do this as a single rename) — unlike
                // delete-then-move, there is never a moment where neither file exists for a
                // concurrent reader, or for a crash, to land on.
                File.Replace(tmp, path, destinationBackupFileName: null);
            else
                File.Move(tmp, path);
        }
    }
}
