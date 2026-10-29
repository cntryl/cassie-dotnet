using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Cntryl.Cassie.Protocol
{
    /// <summary>Reads and writes whole pgwire frames on a stream.</summary>
    internal static class PgFraming
    {
        private const int MaximumFrameLength = 16 * 1024 * 1024;
        private const int PooledFrameLimit = 64 * 1024;

        public static async Task<(byte Tag, byte[] Payload)> ReadBackendFrameAsync(Stream stream, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(stream);
            byte[] header = new byte[5];
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
            if (length is < 4 or > MaximumFrameLength)
            {
                throw new IOException("Invalid pgwire frame length.");
            }

            byte[] payload = new byte[length - 4];
            await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
            return (header[0], payload);
        }

        /// <summary>Writes one frame and zeroes any pooled buffer afterwards because payloads can carry credentials.</summary>
        public static async Task WriteFrontendFrameAsync(Stream stream, byte tag, byte[] payload, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentNullException.ThrowIfNull(payload);
            int frameLength = checked(payload.Length + 5);
            byte[]? rentedFrame = frameLength <= PooledFrameLimit ? ArrayPool<byte>.Shared.Rent(frameLength) : null;
            byte[] frame = rentedFrame ?? new byte[frameLength];
            frame[0] = tag;
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), frameLength - 1);
            payload.CopyTo(frame, 5);
            try
            {
                await stream.WriteAsync(frame.AsMemory(0, frameLength), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (rentedFrame is not null)
                {
                    CryptographicOperations.ZeroMemory(rentedFrame);
                    ArrayPool<byte>.Shared.Return(rentedFrame);
                }
            }
        }

        /// <summary>Writes a pre-built batch of frames and zeroes its buffer afterwards.</summary>
        public static async Task WriteBatchAsync(Stream stream, MemoryStream batch, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentNullException.ThrowIfNull(batch);
            byte[] buffer = batch.GetBuffer();
            try
            {
                await stream.WriteAsync(buffer.AsMemory(0, (int)batch.Length), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }
    }
}
