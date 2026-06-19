using PDRA.Services.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PDRA.Services.Ai.Tools.Queries
{
    internal static class JsonHelpers
    {
        /// <summary>Clamp an int into [min, max]. Hand-rolled because Math.Clamp
        /// is not available on .NET Framework 4.8 (Revit 2024 target).</summary>
        public static int Clamp(int value, int min, int max)
            => value < min ? min : (value > max ? max : value);

        public static JsonNode EmptyObjectSchema() =>
            new JsonObject
            {
                ["type"]                 = "object",
                ["properties"]           = new JsonObject(),
                ["additionalProperties"] = false,
            };

        public static string Serialize(JsonNode node) =>
            node.ToJsonString(PdraJson.Compact);

        /// <summary>
        /// Reads the conventional "limit" arg (positive integer, clamped to [1, max]).
        /// Returns <paramref name="def"/> when the caller omits it. Keeps query-tool
        /// output bounded so a large model doesn't dump thousands of rows — and tokens —
        /// in one result; callers report <c>total</c>/<c>truncated</c> so it can refine.
        /// </summary>
        public static int GetLimit(this JsonElement el, int def = 200, int max = 1000)
            => el.TryGetInt("limit", out var l) ? Clamp(l, 1, max) : def;

        /// <summary>Standard schema fragment for the "limit" arg (see <see cref="GetLimit"/>).</summary>
        public static JsonObject LimitSchemaProp(int def = 200, int max = 1000) =>
            new JsonObject
            {
                ["type"]        = "integer",
                ["description"] = $"Max rows to return (default {def}, max {max}). " +
                                  "Response includes total + truncated so you can narrow the query if needed.",
            };

        /// <summary>
        /// Reads the conventional "fields" arg — a list of column names to keep in
        /// each row. Returns null when absent (caller returns all columns). Lets a
        /// model fetch only what it needs (e.g. just ["id","name"]) instead of every
        /// field, cutting result tokens on large lists.
        /// </summary>
        public static System.Collections.Generic.HashSet<string>? GetFields(this JsonElement el)
        {
            if (el.ValueKind != JsonValueKind.Object ||
                !el.TryGetProperty("fields", out var p) ||
                p.ValueKind != JsonValueKind.Array)
                return null;
            var set = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var item in p.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String)
                    set.Add(item.GetString()!);
            return set.Count > 0 ? set : null;
        }

        /// <summary>Standard schema fragment for the "fields" arg (see <see cref="GetFields"/>).</summary>
        public static JsonObject FieldsSchemaProp() =>
            new JsonObject
            {
                ["type"]        = "array",
                ["items"]       = new JsonObject { ["type"] = "string" },
                ["description"] = "Optional column allow-list — return only these fields per row "
                                + "(id is always included). Omit for all fields.",
            };

        /// <summary>
        /// Projects <paramref name="row"/> down to <paramref name="fields"/> (id always kept).
        /// Returns the row unchanged when no field filter is set. Use to honour the
        /// "fields" arg uniformly across query tools.
        /// </summary>
        public static JsonObject Project(JsonObject row, System.Collections.Generic.HashSet<string>? fields)
        {
            if (fields is null || fields.Count == 0) return row;
            var outp = new JsonObject();
            if (row.TryGetPropertyValue("id", out var idv) && idv is not null)
                outp["id"] = idv.DeepClone();
            foreach (var kv in row)
            {
                if (string.Equals(kv.Key, "id", System.StringComparison.Ordinal)) continue;
                if (fields.Contains(kv.Key) && kv.Value is not null)
                    outp[kv.Key] = kv.Value.DeepClone();
            }
            return outp;
        }

        public static bool TryGetString(this JsonElement el, string name, out string value)
        {
            if (el.ValueKind == JsonValueKind.Object &&
                el.TryGetProperty(name, out var p) &&
                p.ValueKind == JsonValueKind.String)
            {
                value = p.GetString()!;
                return true;
            }
            value = string.Empty;
            return false;
        }

        public static bool TryGetLong(this JsonElement el, string name, out long value)
        {
            value = 0;
            if (el.ValueKind != JsonValueKind.Object) return false;
            if (!el.TryGetProperty(name, out var p))   return false;
            if (p.ValueKind == JsonValueKind.Number)   return p.TryGetInt64(out value);
            if (p.ValueKind == JsonValueKind.String &&
                long.TryParse(p.GetString(), out value)) return true;
            return false;
        }

        public static bool TryGetInt(this JsonElement el, string name, out int value)
        {
            value = 0;
            if (el.ValueKind != JsonValueKind.Object) return false;
            if (!el.TryGetProperty(name, out var p))   return false;
            if (p.ValueKind == JsonValueKind.Number)   return p.TryGetInt32(out value);
            if (p.ValueKind == JsonValueKind.String &&
                int.TryParse(p.GetString(), out value)) return true;
            return false;
        }

        public static bool TryGetDouble(this JsonElement el, string name, out double value)
        {
            value = 0;
            if (el.ValueKind != JsonValueKind.Object) return false;
            if (!el.TryGetProperty(name, out var p))   return false;
            if (p.ValueKind == JsonValueKind.Number)   return p.TryGetDouble(out value);
            if (p.ValueKind == JsonValueKind.String &&
                double.TryParse(p.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out value)) return true;
            return false;
        }
    }
}
