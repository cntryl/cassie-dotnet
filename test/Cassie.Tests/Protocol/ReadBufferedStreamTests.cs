using Cntryl.Cassie.Protocol;
using System.Buffers.Binary;
using Xunit;

namespace Cntryl.Cassie.Tests.Protocol
{
    public sealed class ReadBufferedStreamTests
    {
        [Theory]
        [InlineData(8, 1)]
        [InlineData(8, 3)]
        [InlineData(16, 7)]
        [InlineData(8192, 5)]
        [InlineData(8192, 8192)]
        public async Task ShouldReturnEveryFrameGivenFramesSplitAcrossBufferRefillsWhenFramesAreRead(int bufferSize, int transportChunk)
        {
            // Arrange
            int[] payloadLengths = [0, 1, 2, 3, 4, 7, 11, 16, 40, 0, 300];
            using MemoryStream wire = new();
            foreach (int length in payloadLengths)
            {
                wire.Write(Frame((byte)('A' + (length % 26)), Payload(length)));
            }

            wire.Position = 0;
            using ChunkedStream transport = new(wire, transportChunk);
            await using ReadBufferedStream stream = new(transport, bufferSize);

            // Act
            List<(byte Tag, byte[] Payload)> frames = [];
            foreach (int _ in payloadLengths)
            {
                frames.Add(await PgFraming.ReadBackendFrameAsync(stream, CancellationToken.None));
            }

            // Assert
            Assert.Equal(payloadLengths.Select(static length => (byte)('A' + (length % 26))), frames.Select(static frame => frame.Tag));
            Assert.Equal(payloadLengths.Select(Payload), frames.Select(static frame => frame.Payload));
            Assert.Equal(0, await stream.ReadAsync(new byte[1], CancellationToken.None));
        }

        [Fact]
        public async Task ShouldCompleteSynchronouslyGivenWholeFrameAlreadyBufferedWhenFrameIsRead()
        {
            // Arrange
            using MemoryStream wire = new([.. Frame((byte)'D', [1, 2, 3]), .. Frame((byte)'C', [9])]);
            using CountingStream transport = new(wire);
            await using ReadBufferedStream stream = new(transport);
            _ = await PgFraming.ReadBackendFrameAsync(stream, CancellationToken.None);
            int readsBeforeSecondFrame = transport.Reads;

            // Act
            ValueTask<(byte Tag, byte[] Payload)> second = PgFraming.ReadBackendFrameAsync(stream, CancellationToken.None);
            bool completedSynchronously = second.IsCompletedSuccessfully;
            (byte tag, byte[] payload) = await second;

            // Assert
            Assert.True(completedSynchronously);
            Assert.Equal(readsBeforeSecondFrame, transport.Reads);
            Assert.Equal((byte)'C', tag);
            Assert.Equal([9], payload);
        }

        [Fact]
        public async Task ShouldRejectInvalidLengthGivenHeaderAlreadyBufferedWhenFrameIsRead()
        {
            // Arrange
            using MemoryStream wire = new([.. Frame((byte)'Z', [(byte)'I']), .. Convert.FromHexString("5A00000003")]);
            await using ReadBufferedStream stream = new(wire);
            _ = await PgFraming.ReadBackendFrameAsync(stream, CancellationToken.None);

            // Act
            async Task ReadAsync()
            {
                _ = await PgFraming.ReadBackendFrameAsync(stream, CancellationToken.None);
            }

            // Assert
            _ = await Assert.ThrowsAsync<IOException>(ReadAsync);
        }

        [Fact]
        public async Task ShouldRejectTruncationGivenTransportEndsInsidePayloadWhenFrameIsRead()
        {
            // Arrange
            byte[] frame = Frame((byte)'D', Payload(20));
            using MemoryStream wire = new(frame, 0, frame.Length - 4);
            await using ReadBufferedStream stream = new(wire, 8);

            // Act
            async Task ReadAsync()
            {
                _ = await PgFraming.ReadBackendFrameAsync(stream, CancellationToken.None);
            }

            // Assert
            _ = await Assert.ThrowsAsync<EndOfStreamException>(ReadAsync);
        }

        [Fact]
        public async Task ShouldPassWritesStraightThroughGivenBufferedUnreadBytesWhenStreamIsWritten()
        {
            // Arrange
            using DuplexStream transport = new([1, 2, 3, 4]);
            await using ReadBufferedStream stream = new(transport);
            byte[] first = new byte[1];
            await stream.ReadExactlyAsync(first, CancellationToken.None);

            // Act
            await stream.WriteAsync(new byte[] { 7, 8 }, CancellationToken.None);
            byte[] rest = new byte[3];
            await stream.ReadExactlyAsync(rest, CancellationToken.None);

            // Assert
            Assert.Equal([7, 8], transport.Written.ToArray());
            Assert.Equal([1], first);
            Assert.Equal([2, 3, 4], rest);
        }

        private static byte[] Payload(int length)
        {
            byte[] payload = new byte[length];
            for (int index = 0; index < length; index++)
            {
                payload[index] = (byte)(index + length);
            }

            return payload;
        }

        private static byte[] Frame(byte tag, byte[] payload)
        {
            byte[] frame = new byte[payload.Length + 5];
            frame[0] = tag;
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), payload.Length + 4);
            payload.CopyTo(frame, 5);
            return frame;
        }

        /// <summary>Returns at most a fixed number of bytes per read, like a socket delivering small segments.</summary>
        private sealed class ChunkedStream(Stream inner, int chunk) : CountingStream(inner)
        {
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                return base.ReadAsync(buffer[..Math.Min(chunk, buffer.Length)], cancellationToken);
            }
        }

        private class CountingStream(Stream inner) : Stream
        {
            public int Reads { get; private set; }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Reads++;
                return inner.ReadAsync(buffer, cancellationToken);
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                Reads++;
                return inner.Read(buffer, offset, count);
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class DuplexStream(byte[] incoming) : Stream
        {
            private readonly MemoryStream _incoming = new(incoming);
            public MemoryStream Written { get; } = new();
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return _incoming.Read(buffer, offset, count);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                Written.Write(buffer, offset, count);
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _incoming.Dispose();
                    Written.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}
