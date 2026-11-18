using Cntryl.Cassie.Protocol;
using Xunit;

namespace Cntryl.Cassie.Tests.Protocol;

public sealed class RowMessagesTests
{
    [Fact]
    public void ShouldPreserveColumnMetadataGivenRowDescriptionWhenPayloadIsParsed()
    {
        // Arrange
        var payload = Convert.FromHexString("00017600FFFFFFFFFFFF000000170004FFFFFFFF0001");

        // Act
        var fields = RowMessages.ParseRowDescription(payload);

        // Assert
        var field = Assert.Single(fields);
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
        var payload = Convert.FromHexString(hex);

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
        var payload = Convert.FromHexString("000300000002C3A9000000020100FFFFFFFF");

        // Act
        var offsets = new int[3];
        var lengths = new int[3];
        RowMessages.IndexDataRow(payload, offsets, lengths);

        // Assert
        Assert.Equal([2, 2, -1], lengths);
        Assert.Equal("é", RowMessages.DecodeField(payload, offsets[0], lengths[0], 0));
        Assert.Equal(new byte[] { 1, 0 },
            Assert.IsType<byte[]>(RowMessages.DecodeField(payload, offsets[1], lengths[1], 1)));
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
        var payload = Convert.FromHexString(hex);

        // Act
        void Parse()
        {
            RowMessages.IndexDataRow(payload, new int[1], new int[1]);
        }

        // Assert
        _ = Assert.Throws<IOException>(Parse);
    }

    [Fact]
    public void ShouldRejectUnknownFormatGivenNonNullFieldWhenDataRowIsParsed()
    {
        // Arrange
        var payload = Convert.FromHexString("000100000000");

        // Act
        void Parse()
        {
            _ = RowMessages.DecodeField(payload, 6, 0, 2);
        }

        // Assert
        _ = Assert.Throws<IOException>(Parse);
    }
}
