using Cntryl.Cassie.Protocol;
using Xunit;

namespace Cntryl.Cassie.Tests.Protocol;

public sealed class PgPayloadTests
{
    [Fact]
    public void ShouldAdvanceOffsetGivenSignedIntegersAndUtf8StringWhenPayloadIsRead()
    {
        // Arrange
        var bytes = Convert.FromHexString("AAFFFE80000000C3A900BB");
        var offset = 1;

        // Act
        var small = PgPayload.ReadInt16(bytes, ref offset);
        var large = PgPayload.ReadInt32(bytes, ref offset);
        var text = PgPayload.ReadCString(bytes, ref offset);

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
        var bytes = Convert.FromHexString(hex);
        var offset = 0;

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
        var offset = 0;

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
