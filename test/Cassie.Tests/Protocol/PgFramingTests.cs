using Cntryl.Cassie.Protocol;
using Xunit;

namespace Cntryl.Cassie.Tests.Protocol
{
    public sealed class PgFramingTests
    {
        [Fact]
        public async Task ShouldReadConsecutiveFramesGivenEmptyAndNonemptyPayloadsWhenFramesAreRead()
        {
            // Arrange
            using MemoryStream stream = new(Convert.FromHexString("5A00000005494300000004"));

            // Act
            (byte firstTag, byte[] firstPayload) = await PgFraming.ReadBackendFrameAsync(stream, CancellationToken.None);
            (byte secondTag, byte[] secondPayload) = await PgFraming.ReadBackendFrameAsync(stream, CancellationToken.None);

            // Assert
            Assert.Equal((byte)'Z', firstTag);
            Assert.Equal([(byte)'I'], firstPayload);
            Assert.Equal((byte)'C', secondTag);
            Assert.Empty(secondPayload);
            Assert.Equal(stream.Length, stream.Position);
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
        public async Task ShouldRejectTruncationGivenIncompleteHeaderOrPayloadWhenFrameIsRead(string frame, int omittedBytes)
        {
            // Arrange
            byte[] bytes = Convert.FromHexString(frame);
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
            byte[] buffer = batch.GetBuffer();

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
            using MemoryStream stream = new([], writable: false);
            using MemoryStream batch = new();
            batch.Write([1, 2, 3]);
            byte[] buffer = batch.GetBuffer();

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
}
