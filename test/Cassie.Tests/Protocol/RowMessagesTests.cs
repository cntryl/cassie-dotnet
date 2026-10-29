using Cntryl.Cassie.Protocol;
using Xunit;

namespace Cntryl.Cassie.Tests.Protocol
{
    public sealed class RowMessagesTests
    {
        [Fact]
        public void ShouldPreserveColumnMetadataGivenRowDescriptionWhenPayloadIsParsed()
        {
            // Arrange
            byte[] payload = Convert.FromHexString("00017600FFFFFFFFFFFF000000170004FFFFFFFF0001");

            // Act
            FieldDescription[] fields = RowMessages.ParseRowDescription(payload);

            // Assert
            FieldDescription field = Assert.Single(fields);
            Assert.Equal(new FieldDescription("v", uint.MaxValue, -1, 23, 4, -1, 1), field);
        }

        [Theory]
        [InlineData("")]
        [InlineData("FFFF")]
        [InlineData("000178")]
        [InlineData("0001780000000001")]
        [InlineData("000100000000000000000000170004FFFFFFFF0002")]
        [InlineData("000000")]
        public void ShouldRejectMalformedDescriptionGivenInvalidMetadataWhenPayloadIsParsed(string hex)
        {
            // Arrange
            byte[] payload = Convert.FromHexString(hex);

            // Act
            void Parse()
            {
                _ = RowMessages.ParseRowDescription(payload);
            }

            // Assert
            _ = Assert.Throws<IOException>(Parse);
        }

        [Fact]
        public void ShouldDistinguishTextBinaryAndNullGivenMixedFormatsWhenDataRowIsParsed()
        {
            // Arrange
            byte[] payload = Convert.FromHexString("000300000002C3A9000000020100FFFFFFFF");

            // Act
            object?[] values = RowMessages.ParseDataRow(payload, [0, 1, 0]);

            // Assert
            Assert.Equal("é", values[0]);
            Assert.Equal(new byte[] { 1, 0 }, Assert.IsType<byte[]>(values[1]));
            Assert.Null(values[2]);
        }

        [Theory]
        [InlineData("0000")]
        [InlineData("FFFF")]
        [InlineData("0001FFFFFFFE")]
        [InlineData("00010000000201")]
        [InlineData("00010000000001")]
        [InlineData("00017FFFFFFF")]
        public void ShouldRejectMalformedRowGivenInvalidCountOrLengthWhenDataRowIsParsed(string hex)
        {
            // Arrange
            byte[] payload = Convert.FromHexString(hex);

            // Act
            void Parse()
            {
                _ = RowMessages.ParseDataRow(payload, [0]);
            }

            // Assert
            _ = Assert.Throws<IOException>(Parse);
        }

        [Fact]
        public void ShouldRejectUnknownFormatGivenNonNullFieldWhenDataRowIsParsed()
        {
            // Arrange
            byte[] payload = Convert.FromHexString("000100000000");

            // Act
            void Parse()
            {
                _ = RowMessages.ParseDataRow(payload, [2]);
            }

            // Assert
            _ = Assert.Throws<IOException>(Parse);
        }
    }
}
