using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
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
    /// </summary>
    public static class RecordHash
    {
        public static string Of(JsonNode? node)
        {
            var canonical = Canonicalize(node);
            var text = canonical is null ? "null" : canonical.ToJsonString();
            var bytes = Encoding.UTF8.GetBytes(text);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(bytes);
            var sb = new StringBuilder(32);
            for (var i = 0; i < 16; i++) sb.Append(hash[i].ToString("x2"));
            return sb.ToString();
        }

        public static string Of(JsonObject fields) => Of((JsonNode)fields);

        private static JsonNode? Canonicalize(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    var keys = new List<string>();
                    foreach (var kv in obj) keys.Add(kv.Key);
                    keys.Sort(StringComparer.Ordinal);
                    var sorted = new JsonObject();
                    foreach (var k in keys) sorted[k] = Canonicalize(obj[k]?.DeepClone());
                    return sorted;

                case JsonArray arr:
                    var copy = new JsonArray();
                    foreach (var item in arr) copy.Add(Canonicalize(item?.DeepClone()));
                    return copy;

                default:
                    return node?.DeepClone();
            }
        }
    }
}
