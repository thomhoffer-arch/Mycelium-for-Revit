using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Loam.Revit.Connector.ModelLog
{
    /// <summary>
    /// Stable content hash for one field-group's current value — the primitive
    /// <see cref="ModelLogWriter.WriteIfChanged"/> compares per top-level field (h, loc, rel,
    /// q, mats, p, …) to decide whether THAT field-group changed since the last write, without
    /// ever diffing values (the handoff's "the connector never computes diffs" rule: a changed
    /// field-group is always written as its whole new value, never a value-level delta).
    ///
    /// Canonicalized (object keys sorted, recursively) before hashing so field construction
    /// order — which can legitimately vary run to run for a Dictionary-backed lookup — never
    /// changes the hash, only content does.
    ///
    /// Written straight to a <see cref="Utf8JsonWriter"/> over a reused buffer instead of
    /// building a whole cloned/sorted <see cref="JsonNode"/> tree first (the old approach — a
    /// measured cost on a real snapshot: this is called once per field-group per element, tens
    /// of millions of times on a large model). A leaf value is handed to the writer via its own
    /// <see cref="JsonNode.WriteTo(Utf8JsonWriter, JsonSerializerOptions?)"/> with no clone at
    /// all — cloning a value never changes how it serializes, so this produces byte-identical
    /// output to the old clone-then-<c>ToJsonString()</c> path (verified by
    /// RecordHashTests against the old implementation, kept there for comparison only).
    /// </summary>
    public static class RecordHash
    {
        // Reused across calls rather than allocated fresh each time — this class is only ever
        // driven from Revit's own single idle/UI thread (see ModelLogWriter's own "NOT
        // thread-safe by itself" note), but [ThreadStatic] costs nothing and keeps it safe if
        // that ever changes, or under parallel test execution.
        [ThreadStatic] private static MemoryStream? _buffer;

        public static string Of(JsonNode? node)
        {
            var buffer = _buffer ??= new MemoryStream(1024);
            buffer.Position = 0;
            buffer.SetLength(0);

            using (var writer = new Utf8JsonWriter(buffer))
                WriteCanonical(writer, node);

            buffer.Position = 0;
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(buffer);

            // byte.TryFormat/Span<char> string ctor aren't available on net48 (Revit 2024) — a
            // plain StringBuilder works on every target.
            var sb = new System.Text.StringBuilder(32);
            for (var i = 0; i < 16; i++) sb.Append(hash[i].ToString("x2"));
            return sb.ToString();
        }

        public static string Of(JsonObject fields) => Of((JsonNode)fields);

        private static void WriteCanonical(Utf8JsonWriter writer, JsonNode? node)
        {
            switch (node)
            {
                case null:
                    writer.WriteNullValue();
                    break;

                case JsonObject obj:
                {
                    var keys = new List<string>(obj.Count);
                    foreach (var kv in obj) keys.Add(kv.Key);
                    keys.Sort(StringComparer.Ordinal);

                    writer.WriteStartObject();
                    foreach (var k in keys)
                    {
                        writer.WritePropertyName(k);
                        WriteCanonical(writer, obj[k]);
                    }
                    writer.WriteEndObject();
                    break;
                }

                case JsonArray arr:
                    writer.WriteStartArray();
                    foreach (var item in arr) WriteCanonical(writer, item);
                    writer.WriteEndArray();
                    break;

                default: // a leaf JsonValue — write it directly, no clone (see class doc comment)
                    node.WriteTo(writer);
                    break;
            }
        }
    }
}
