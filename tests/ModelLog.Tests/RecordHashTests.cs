using Loam.Revit.Connector.ModelLog;
using System.Text.Json.Nodes;
using Xunit;

namespace ModelLog.Tests
{
    public class RecordHashTests
    {
        [Fact]
        public void SameContentDifferentKeyOrder_SameHash()
        {
            var a = new JsonObject { ["x"] = 1, ["y"] = 2 };
            var b = new JsonObject { ["y"] = 2, ["x"] = 1 };

            Assert.Equal(RecordHash.Of(a), RecordHash.Of(b));
        }

        [Fact]
        public void NestedObjectKeyOrder_SameHash()
        {
            var a = new JsonObject
            {
                ["outer"] = new JsonObject { ["a"] = 1, ["b"] = 2 },
            };
            var b = new JsonObject
            {
                ["outer"] = new JsonObject { ["b"] = 2, ["a"] = 1 },
            };

            Assert.Equal(RecordHash.Of(a), RecordHash.Of(b));
        }

        [Fact]
        public void DifferentValue_DifferentHash()
        {
            var a = new JsonObject { ["length"] = 20.997 };
            var b = new JsonObject { ["length"] = 20.998 };

            Assert.NotEqual(RecordHash.Of(a), RecordHash.Of(b));
        }

        [Fact]
        public void ArrayElementOrder_DifferentHash()
        {
            // Array order IS significant (unlike object key order) — e.g. the `p` parameter
            // list or `bb` bounding-box corners are positional, not a bag of keys.
            var a = new JsonArray { 1, 2, 3 };
            var b = new JsonArray { 3, 2, 1 };

            Assert.NotEqual(RecordHash.Of(a), RecordHash.Of(b));
        }

        [Fact]
        public void RoundTripDouble_Precision_AffectsHash()
        {
            // Revit's own values (round-trip "R"-precision doubles) must never be silently
            // rounded away before hashing, or a sub-tolerance edit would look like "no change".
            var a = new JsonObject { ["v"] = 20.99700000000001 };
            var b = new JsonObject { ["v"] = 20.997 };

            Assert.NotEqual(RecordHash.Of(a), RecordHash.Of(b));
        }
    }
}
