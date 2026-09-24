using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using Loam.Revit.Connector.ModelLog;
using Xunit;

namespace ModelLog.Tests
{
    /// <summary>
    /// RecordHash was rewritten to hash straight off a <see cref="Utf8JsonWriter"/> instead of
    /// deep-cloning + sorting a whole new <see cref="JsonNode"/> tree then calling
    /// <c>ToJsonString()</c> (a measured cost on a real snapshot). Every existing hash cache on
    /// disk depends on the two producing byte-identical output, so this file keeps the OLD
    /// implementation (verbatim, pre-optimization) ONLY for that comparison — nothing else in
    /// the codebase may reference it.
    /// </summary>
    public class RecordHashCompatTests
    {
        private static string OldOf(JsonNode? node)
        {
            var canonical = OldCanonicalize(node);
            var text = canonical is null ? "null" : canonical.ToJsonString();
            var bytes = Encoding.UTF8.GetBytes(text);
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(bytes);
            var sb = new StringBuilder(32);
            for (var i = 0; i < 16; i++) sb.Append(hash[i].ToString("x2"));
            return sb.ToString();
        }

        private static JsonNode? OldCanonicalize(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    var keys = new List<string>();
                    foreach (var kv in obj) keys.Add(kv.Key);
                    keys.Sort(System.StringComparer.Ordinal);
                    var sorted = new JsonObject();
                    foreach (var k in keys) sorted[k] = OldCanonicalize(obj[k]?.DeepClone());
                    return sorted;

                case JsonArray arr:
                    var copy = new JsonArray();
                    foreach (var item in arr) copy.Add(OldCanonicalize(item?.DeepClone()));
                    return copy;

                default:
                    return node?.DeepClone();
            }
        }

        public static IEnumerable<object?[]> Cases()
        {
            yield return new object?[] { null };
            yield return new object?[] { JsonValue.Create(42) };
            yield return new object?[] { JsonValue.Create(true) };
            yield return new object?[] { JsonValue.Create("plain string") };
            yield return new object?[] { JsonValue.Create("unicode: café, 日本語, emoji 🎉") };
            yield return new object?[] { JsonValue.Create("html-sensitive: <a href=\"x\">&'\"") };
            yield return new object?[] { JsonValue.Create(20.99700000000001) };
            yield return new object?[] { JsonValue.Create(-0.0) };
            yield return new object?[] { JsonValue.Create(long.MaxValue) };
            yield return new object?[] { new JsonObject() };
            yield return new object?[] { new JsonArray() };
            yield return new object?[]
            {
                new JsonObject
                {
                    ["eid"] = 303793,
                    ["cat"] = "c:Walls",
                    ["h"] = new JsonObject { ["mark"] = "W-12", ["typeMark"] = "EW-02" },
                    ["loc"] = new JsonObject { ["storey"] = "n:311", ["space"] = "n:4402" },
                    ["q"] = new JsonObject { ["length"] = 20.997, ["height"] = 10.006, ["area"] = 210.1 },
                    ["bb"] = new JsonArray { new JsonArray { 1.0, 2.0, 3.0 }, new JsonArray { 4.0, 5.0, 6.0 } },
                    ["mats"] = new JsonArray { new JsonArray { "m:77", 210.1, 10.5 } },
                    ["nested_null"] = null,
                    ["p"] = new JsonObject { ["builtin:FIRE_RATING"] = "60", ["shared:9f2e"] = "EW-02" },
                },
            };
            yield return new object?[]
            {
                new JsonObject { ["z"] = 1, ["a"] = new JsonObject { ["nested_z"] = 1, ["nested_a"] = 2 }, ["m"] = 3 },
            };
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void NewImplementation_MatchesOld_ByteForByte(JsonNode? node)
        {
            Assert.Equal(OldOf(node), RecordHash.Of(node));
        }
    }
}
