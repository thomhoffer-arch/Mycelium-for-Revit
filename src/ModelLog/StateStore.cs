using System.IO;
using System.Text.Json;

namespace Loam.Revit.Connector.ModelLog
{
    /// <summary>
    /// Loads/saves <see cref="ModelLogState"/> to <c>state.json</c> next to the log. Loaded once
    /// at writer startup; saved after every flushed append — never before (the handoff's
    /// crash-safety rule #2: "update state.json only after the log line is flushed. If a crash
    /// lands in between, the next reconcile writes the same state again: a harmless duplicate,
    /// never a lost change").
    /// </summary>
    public static class StateStore
    {
        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        public static ModelLogState Load(string path)
        {
            if (!File.Exists(path)) return new ModelLogState();
            try
            {
                var text = File.ReadAllText(path);
                return JsonSerializer.Deserialize<ModelLogState>(text, Options) ?? new ModelLogState();
            }
            catch
            {
                // Corrupt/partial state.json (e.g. a crash mid-write before this atomic-rename
                // scheme existed, or a hand edit) — start from an empty cache rather than fail to
                // load the log at all. The next reconcile re-derives every hash from the live
                // model, so nothing is lost except one round of "everything looks changed" writes.
                return new ModelLogState();
            }
        }

        /// <summary>Atomic write: serialize to a temp file in the same directory, then move it
        /// over the real path. A crash mid-write leaves the OLD state.json intact — never a
        /// truncated or half-written one for <see cref="Load"/> to trip over next time.</summary>
        public static void Save(string path, ModelLogState state)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, Options));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
