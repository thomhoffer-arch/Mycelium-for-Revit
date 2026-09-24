using Loam.Revit.Connector.ModelLog;
using Xunit;

namespace ModelLog.Tests
{
    public class UniqueIdElementIdTests
    {
        [Fact]
        public void ParsesOrdinaryTail()
        {
            // 0x0001e240 = 123456
            Assert.True(UniqueIdElementId.TryGetElementIdTail(
                "22222222-3333-4444-5555-666666666666-0001e240", out var id));
            Assert.Equal(123456, id);
        }

        [Fact]
        public void Parses64BitTail()
        {
            // Revit 2024+ widened ElementId to 64 bits — a full 16-hex-digit tail must still work.
            Assert.True(UniqueIdElementId.TryGetElementIdTail(
                "22222222-3333-4444-5555-666666666666-1122334455667788", out var id));
            Assert.Equal(0x1122334455667788L, id);
        }

        [Fact]
        public void UppercaseHexAccepted()
        {
            Assert.True(UniqueIdElementId.TryGetElementIdTail(
                "22222222-3333-4444-5555-666666666666-0001E240", out var id));
            Assert.Equal(123456, id);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("no-dashes-at-all-but-still-not-a-uniqueid")]
        [InlineData("22222222-3333-4444-5555-666666666666-")]        // empty tail
        [InlineData("22222222-3333-4444-5555-666666666666-ZZZZ")]    // non-hex tail
        [InlineData("22222222-3333-4444-5555-666666666666-11223344556677889")] // 17 hex chars, too long
        public void MalformedInput_Skipped(string? uniqueId)
        {
            Assert.False(UniqueIdElementId.TryGetElementIdTail(uniqueId, out var id));
            Assert.Equal(0, id);
        }
    }
}
