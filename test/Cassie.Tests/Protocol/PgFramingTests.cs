using Cntryl.Cassie.Protocol;
using Xunit;

namespace Cntryl.Cassie.Tests.Protocol;

public sealed class PgFramingTests
{
    [Fact]
    public async Task ShouldReadConsecutiveFramesGivenEmptyAndNonemptyPayloadsWhenFramesAreRead()
    {
        // Arrange
        using MemoryStream stream = new(Convert.FromHexString("5A00000005494300000004"));

        // Act
        var (firstTag, firstPayload) = await PgFraming.ReadBackendFrameAsync(stream, CancellationToken.None);
        var (secondTag, secondPayload) = await PgFraming.ReadBackendFrameAsync(stream, CancellationToken.None);

        // Assert
        Assert.Equal((byte)'Z', firstTag);
        Assert.Equal([(byte)'I'], firstPayload);
        Assert.Equal((byte)'C', secondTag);
        Assert.Empty(secondPayload);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public async Task ShouldUseExactPayloadGivenFragmentedRowsWhenReaderStorageIsReused()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        using MemoryStream frames = new();
        PgWriter.AppendFrame(frames, BackendMessage.DataRow, [0, 1, 0, 0, 0, 3, 1, 2, 3]);
        PgWriter.AppendFrame(frames, BackendMessage.DataRow, [0, 1, 0, 0, 0, 1, 4]);
        PgWriter.AppendFrame(frames, BackendMessage.DataRow, new byte[(64 * 1024) + 1]);
        frames.Position = 0;
        using ReadBufferedStream stream = new(frames, 3);
        RowPayloadBuffer buffer = new();
        var offsets = new int[1];
        var lengths = new int[1];

        // Act
        byte[] retained;
        byte[] first;
        byte[] second;
        byte[] oversized;

        try
        {
            (_, first) = await PgFraming.ReadBackendFrameAsync(stream, buffer, token);
            RowMessages.IndexDataRow(buffer.GetPayload(first), offsets, lengths);
            retained = Assert.IsType<byte[]>(RowMessages.DecodeField(first, offsets[0], lengths[0], 1));
            (_, second) = await PgFraming.ReadBackendFrameAsync(stream, buffer, token);
            RowMessages.IndexDataRow(buffer.GetPayload(second), offsets, lengths);
            (_, oversized) = await PgFraming.ReadBackendFrameAsync(stream, buffer, token);
        }
        finally
        {
            buffer.Release();
        }

        // Assert

        Assert.Same(first, second);
        Assert.Equal(1, lengths[0]);
        Assert.Equal([1, 2, 3], retained);
        Assert.NotSame(first, oversized);
        Assert.Equal((64 * 1024) + 1, oversized.Length);
    }

    [Theory]
    [InlineData("5A00000003")]
    [InlineData("5AFFFFFFFF")]
    [InlineData("5A01000001")]
    public async Task ShouldRejectInvalidLengthGivenOutsideAllowedRangeWhenFrameIsRead(string frame)
    {
        // Arrange
        using MemoryStream stream = new(Convert.FromHexString(frame));

        // Act
        async Task ReadAsync()
        {
            _ = await PgFraming.ReadBackendFrameAsync(stream, CancellationToken.None);
        }

        // Assert
        _ = await Assert.ThrowsAsync<IOException>(ReadAsync);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("5A000000", 0)]
    [InlineData("5A0000000549", 1)]
    public async Task ShouldRejectTruncationGivenIncompleteHeaderOrPayloadWhenFrameIsRead(
        string frame,
        int omittedBytes
    )
    {
        // Arrange
        var bytes = Convert.FromHexString(frame);
        using MemoryStream stream = new(bytes, 0, bytes.Length - omittedBytes);

        // Act
        async Task ReadAsync()
        {
            _ = await PgFraming.ReadBackendFrameAsync(stream, CancellationToken.None);
        }

        // Assert
        _ = await Assert.ThrowsAsync<EndOfStreamException>(ReadAsync);
    }

    [Fact]
    public async Task ShouldWriteLengthIncludingItselfGivenFrontendPayloadWhenFrameIsWritten()
    {
        // Arrange
        using MemoryStream stream = new();

        // Act
        await PgFraming.WriteFrontendFrameAsync(stream, (byte)'Q', [(byte)'x', 0], CancellationToken.None);

        // Assert
        Assert.Equal(Convert.FromHexString("51000000067800"), stream.ToArray());
    }

    [Fact]
    public async Task ShouldEraseBatchBufferGivenSuccessfulWriteWhenBatchIsWritten()
    {
        // Arrange
        using MemoryStream stream = new();
        using MemoryStream batch = new();
        batch.Write([1, 2, 3]);
        var buffer = batch.GetBuffer();

        // Act
        await PgFraming.WriteBatchAsync(stream, batch, CancellationToken.None);

        // Assert
        Assert.Equal(new byte[] { 1, 2, 3 }, stream.ToArray());
        Assert.All(buffer, value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public async Task ShouldEraseBatchBufferGivenWriteFailureWhenBatchIsWritten()
    {
        // Arrange
        using MemoryStream stream = new([], false);
        using MemoryStream batch = new();
        batch.Write([1, 2, 3]);
        var buffer = batch.GetBuffer();

        // Act
        async Task WriteAsync()
        {
            await PgFraming.WriteBatchAsync(stream, batch, CancellationToken.None);
        }

        // Assert
        _ = await Assert.ThrowsAsync<NotSupportedException>(WriteAsync);
        Assert.All(buffer, value => Assert.Equal((byte)0, value));
    }
}
