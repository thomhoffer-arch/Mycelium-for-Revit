using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Loam.Revit.Connector.ModelLog
{
    /// <summary>
    /// The append-only delta journal that sits between two full rewrites of state.json:
    /// <c>state.&lt;gen&gt;.jsonl</c>, next to it, one compact JSON op per line, replayed in
    /// order on top of the base after <see cref="StateStore.Load"/> reads it. Only the journal
    /// whose generation matches the base's <see cref="ModelLogState.JournalGeneration"/> is ever
    /// replayed; a journal from an older generation is a leftover from a compaction interrupted
    /// after the new base was written but before the old journal was deleted, and is harmless to
    /// ignore (and safe to delete once a newer base is loaded).
    ///
    /// Ops (short keys to keep lines small — this file is written once per idle tick):
    ///   hs  hash-cache set:    {op:"hs", f:family, id, h:{fieldGroup:hash, ...}}
    ///   hr  hash-cache remove: {op:"hr", f:family, id}
    ///   pd  pdef seen:         {op:"pd", id}
    ///   cs  cat seen:          {op:"cs", id}
    ///   m   scalar fields:     {op:"m", seq, seg, closed, mv?, pv?}
    ///     (LastSeq is carried for completeness but is never trusted over the log tail — see
    ///     ModelLogWriter's recovery logic in its constructor)
    ///
    /// Replay stops at the first line that fails to parse or has no recognized "op" — a torn last
    /// write (the same "ignore a torn last line" rule <see cref="LogSegmentWriter"/> uses for the
    /// log itself) — and everything after it in the file is ignored too.
    /// </summary>
    public static class StateJournal
    {
        public static string PathFor(string statePath, long generation)
        {
            var dir = Path.GetDirectoryName(statePath);
            var name = $"state.{generation}.jsonl";
            return dir is null || dir.Length == 0 ? name : Path.Combine(dir, name);
        }

        public static string HashSetOp(string family, string id, IReadOnlyDictionary<string, string> hashes)
        {
            var h = new JsonObject();
            foreach (var kv in hashes) h[kv.Key] = kv.Value;
            return new JsonObject { ["op"] = "hs", ["f"] = family, ["id"] = id, ["h"] = h }.ToJsonString();
        }

        public static string HashRemoveOp(string family, string id) =>
            new JsonObject { ["op"] = "hr", ["f"] = family, ["id"] = id }.ToJsonString();

        public static string PdefSeenOp(string id) => new JsonObject { ["op"] = "pd", ["id"] = id }.ToJsonString();

        public static string CatSeenOp(string id) => new JsonObject { ["op"] = "cs", ["id"] = id }.ToJsonString();

        public static string MetaOp(ModelLogState state)
        {
            var obj = new JsonObject
            {
                ["op"] = "m",
                ["seq"] = state.LastSeq,
                ["seg"] = state.CurrentSegment,
                ["closed"] = state.LastCheckpointClosed,
            };
            if (state.LastModelVersion is not null) obj["mv"] = state.LastModelVersion;
            if (state.LastProducerVersion is not null) obj["pv"] = state.LastProducerVersion;
            return obj.ToJsonString();
        }

        /// <summary>Appends <paramref name="lines"/> to the current generation's journal and
        /// flushes — append + flush, no fsync, the same durability tradeoff <see
        /// cref="LogSegmentWriter.AppendLine"/> makes for the log itself. A no-op when
        /// <paramref name="lines"/> is empty (never creates an empty journal file).</summary>
        public static void Append(string statePath, long generation, IReadOnlyList<string> lines)
        {
            if (lines.Count == 0) return;
            var path = PathFor(statePath, generation);
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            foreach (var line in lines) { writer.Write(line); writer.Write('\n'); }
            writer.Flush();
        }

        /// <summary>Replays the journal matching <paramref name="state"/>'s current
        /// JournalGeneration onto it in place. A missing journal (nothing appended yet since the
        /// last compaction, or a brand-new log) is a no-op.</summary>
        public static void Replay(string statePath, ModelLogState state)
        {
            var path = PathFor(statePath, state.JournalGeneration);
            if (!File.Exists(path)) return;

            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch (JsonException) { break; } // torn last line — stop, ignore the rest

                using (doc)
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("op", out var opEl)) break;
                    switch (opEl.GetString())
                    {
                        case "hs":
                        {
                            var family = root.GetProperty("f").GetString()!;
                            var id = root.GetProperty("id").GetString()!;
                            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
                            foreach (var prop in root.GetProperty("h").EnumerateObject())
                                hashes[prop.Name] = prop.Value.GetString()!;
                            state.Cache.Set(family, id, hashes);
                            break;
                        }
                        case "hr":
                            state.Cache.Remove(root.GetProperty("f").GetString()!, root.GetProperty("id").GetString()!);
                            break;
                        case "pd":
                            state.Cache.PdefSeen.Add(root.GetProperty("id").GetString()!);
                            break;
                        case "cs":
                            state.Cache.CatSeen.Add(root.GetProperty("id").GetString()!);
                            break;
                        case "m":
                            state.LastSeq = root.GetProperty("seq").GetInt64();
                            state.CurrentSegment = root.GetProperty("seg").GetInt64();
                            state.LastCheckpointClosed = root.GetProperty("closed").GetBoolean();
                            state.LastModelVersion = root.TryGetProperty("mv", out var mv) ? mv.GetString() : null;
                            state.LastProducerVersion = root.TryGetProperty("pv", out var pv) ? pv.GetString() : null;
                            break;
                        default:
                            break; // unknown op — ignore just this line, keep replaying
                    }
                }
            }
        }

        public static long SizeBytes(string statePath, long generation)
        {
            var path = PathFor(statePath, generation);
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }

        /// <summary>Deletes one generation's journal file, if present — best-effort, since it's
        /// already superseded by a newly-written base by the time this is called.</summary>
        public static void DeleteGeneration(string statePath, long generation)
        {
            try
            {
                var path = PathFor(statePath, generation);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { /* best-effort cleanup */ }
        }

        /// <summary>Deletes every <c>state.*.jsonl</c> next to <paramref name="statePath"/> whose
        /// generation isn't <paramref name="currentGeneration"/> — leftovers from a compaction
        /// that wrote the new base but crashed before deleting the old journal. Never called by a
        /// writer that doesn't own the lock (see <see cref="ModelLogWriter.LockHeldElsewhere"/>).</summary>
        public static void DeleteStaleGenerations(string statePath, long currentGeneration)
        {
            var dir = Path.GetDirectoryName(statePath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            foreach (var f in Directory.EnumerateFiles(dir, "state.*.jsonl"))
            {
                var parts = Path.GetFileName(f).Split('.');
                if (parts.Length == 3 && parts[0] == "state" && parts[2] == "jsonl" &&
                    long.TryParse(parts[1], out var gen) && gen != currentGeneration)
                {
                    try { File.Delete(f); } catch { /* best-effort cleanup */ }
                }
            }
        }
    }
}
