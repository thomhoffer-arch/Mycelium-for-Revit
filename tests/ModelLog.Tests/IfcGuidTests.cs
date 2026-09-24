using Loam.Revit.Connector.ModelLog;
using System;
using Xunit;

namespace ModelLog.Tests
{
    // Golden values computed directly from SRM's own reference implementation
    // (thomhoffer-arch/SRM, srm/ifcguid.py — itself verified there against
    // ifcopenshell.guid.compress/expand), not reproduced from memory:
    //
    //   python3 -c "from srm.ifcguid import compress_guid, revit_unique_id_to_ifc_guid; ..."
    //
    // so a mistake in this C# port shows up as a mismatch against an independently-computed
    // value, not just an internal round-trip agreeing with itself.
    public class IfcGuidTests
    {
        [Theory]
        [InlineData("12345678123412341234123456789012", "0ID5Pu4ZGID18q4ZHMU90I")]
        [InlineData("00000000000000000000000000000000", "0000000000000000000000")]
        [InlineData("ffffffffffffffffffffffffffffffff", "3$$$$$$$$$$$$$$$$$$$$$")]
        [InlineData("a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4", "2XiiFKvVQXiiFKvVQXiiFK")]
        [InlineData("0123456789abcdef0123456789abcdef", "018qLdYQlDxm4ZHMU9gytl")]
        public void Compress_MatchesSrmReference(string hex32, string expectedIfcGuid)
        {
            Assert.Equal(expectedIfcGuid, IfcGuid.Compress(hex32));
        }

        [Theory]
        [InlineData("12345678123412341234123456789012")]
        [InlineData("00000000000000000000000000000000")]
        [InlineData("ffffffffffffffffffffffffffffffff")]
        [InlineData("a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4")]
        [InlineData("0123456789abcdef0123456789abcdef")]
        public void CompressThenExpand_RoundTrips(string hex32)
        {
            var compressed = IfcGuid.Compress(hex32);
            Assert.Equal(22, compressed.Length);
            Assert.Equal(hex32, IfcGuid.Expand(compressed));
        }

        [Fact]
        public void Compress_AcceptsDashedGuidForm()
        {
            var dashed = "12345678-1234-1234-1234-123456789012";
            Assert.Equal(IfcGuid.Compress(dashed), IfcGuid.Compress(dashed.Replace("-", "")));
        }

        [Fact]
        public void Compress_RejectsWrongLength()
        {
            Assert.Throws<ArgumentException>(() => IfcGuid.Compress("abcd"));
        }

        [Fact]
        public void Expand_RejectsWrongLength()
        {
            Assert.Throws<ArgumentException>(() => IfcGuid.Expand("tooshort"));
        }

        [Theory]
        [InlineData("12345678-1234-1234-1234-123456789012-0000002a", "0ID5Pu4ZGID18q4ZHMU90u")]
        [InlineData("5f1c2e3a-9b4d-4e8f-a1b2-c3d4e5f6a7b8-00000001", "1V72uwcqrEZw6omzJbzgUv")]
        public void FromRevitUniqueId_MatchesSrmReference(string uniqueId, string expectedIfcGuid)
        {
            Assert.Equal(expectedIfcGuid, IfcGuid.FromRevitUniqueId(uniqueId));
        }

        [Fact]
        public void FromRevitUniqueId_DifferentEpisodeCounter_ChangesResult()
        {
            var a = IfcGuid.FromRevitUniqueId("12345678-1234-1234-1234-123456789012-00000000");
            var b = IfcGuid.FromRevitUniqueId("12345678-1234-1234-1234-123456789012-000000ff");
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void FromRevitUniqueId_IsDeterministic()
        {
            const string uid = "12345678-1234-1234-1234-123456789012-0000002a";
            Assert.Equal(IfcGuid.FromRevitUniqueId(uid), IfcGuid.FromRevitUniqueId(uid));
        }

        [Theory]
        [InlineData("not-a-unique-id")]
        [InlineData("12345678-1234-1234-1234-123456789012")] // missing episode counter
        [InlineData("")]
        public void FromRevitUniqueId_MalformedInput_ReturnsNullNeverThrows(string uniqueId)
        {
            Assert.Null(IfcGuid.FromRevitUniqueId(uniqueId));
        }
    }
}
