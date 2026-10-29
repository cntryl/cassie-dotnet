using Cntryl.Cassie.Protocol;
using Xunit;

namespace Cntryl.Cassie.Tests.Protocol
{
    public sealed class PgPayloadTests
    {
        [Fact]
        public void ShouldAdvanceOffsetGivenSignedIntegersAndUtf8StringWhenPayloadIsRead()
        {
            // Arrange
            byte[] bytes = Convert.FromHexString("AAFFFE80000000C3A900BB");
            int offset = 1;

            // Act
            short small = PgPayload.ReadInt16(bytes, ref offset);
            int large = PgPayload.ReadInt32(bytes, ref offset);
            string text = PgPayload.ReadCString(bytes, ref offset);

            // Assert
            Assert.Equal((short)-2, small);
            Assert.Equal(int.MinValue, large);
            Assert.Equal("é", text);
            Assert.Equal(bytes.Length - 1, offset);
        }

        [Theory]
        [InlineData("", false)]
        [InlineData("01", false)]
        [InlineData("000102", true)]
        public void ShouldRejectTruncatedIntegerGivenInsufficientBytesWhenPayloadIsRead(string hex, bool wide)
        {
            // Arrange
            byte[] bytes = Convert.FromHexString(hex);
            int offset = 0;

            // Act
            void Read()
            {
                _ = wide ? PgPayload.ReadInt32(bytes, ref offset) : PgPayload.ReadInt16(bytes, ref offset);
            }

            // Assert
            _ = Assert.Throws<IOException>(Read);
            Assert.Equal(0, offset);
        }

        [Fact]
        public void ShouldRejectUnterminatedStringGivenMissingNullWhenPayloadIsRead()
        {
            // Arrange
            byte[] bytes = [(byte)'x'];
            int offset = 0;

            // Act
            void Read()
            {
                _ = PgPayload.ReadCString(bytes, ref offset);
            }

            // Assert
            _ = Assert.Throws<IOException>(Read);
            Assert.Equal(0, offset);
        }

        [Fact]
        public void ShouldWriteNetworkOrderGivenSignedIntegersAndUtf8StringWhenPayloadIsBuilt()
        {
            // Arrange
            using MemoryStream stream = new();

            // Act
            PgWriter.WriteInt16(stream, -2);
            PgWriter.WriteInt32(stream, int.MinValue);
            PgWriter.WriteCString(stream, "é");

            // Assert
            Assert.Equal(Convert.FromHexString("FFFE80000000C3A900"), stream.ToArray());
        }
    }
}
