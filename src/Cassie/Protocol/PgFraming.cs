using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Cntryl.Cassie.Protocol;

/// <summary>Reads and writes whole pgwire frames on a stream.</summary>
internal static class PgFraming
{
    private const int MaximumFrameLength = 16 * 1024 * 1024;
    private const int PooledFrameLimit = 64 * 1024;

    internal const int HeaderLength = 5;

    /// <summary>
    ///     Reads one backend frame. Completes synchronously, without touching the transport, when the
    ///     whole frame is already buffered.
    /// </summary>
    public static ValueTask<(byte Tag, byte[] Payload)> ReadBackendFrameAsync(Stream stream,
        CancellationToken cancellationToken)
    {
        return ReadBackendFrameAsync(stream, null, cancellationToken);
    }

    public static ValueTask<(byte Tag, byte[] Payload)> ReadBackendFrameAsync(Stream stream,
        RowPayloadBuffer? rowBuffer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream is ReadBufferedStream buffered)
        {
            var available = buffered.Buffered;
            if (available.Length >= HeaderLength)
            {
                var tag = available[0];
                var payloadLength = ReadPayloadLength(available[1..]);
                if (available.Length - HeaderLength >= payloadLength)
                {
                    var payload = GetPayloadBuffer(tag, payloadLength, rowBuffer);
                    available.Slice(HeaderLength, payloadLength).CopyTo(payload);
                    buffered.Consume(HeaderLength + payloadLength);
                    return ValueTask.FromResult((tag, payload));
                }
            }
        }

        return ReadBackendFrameCoreAsync(stream, rowBuffer, cancellationToken);
    }

    private static async ValueTask<(byte Tag, byte[] Payload)> ReadBackendFrameCoreAsync(Stream stream,
        RowPayloadBuffer? rowBuffer, CancellationToken cancellationToken)
    {
        var header = new byte[HeaderLength];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var payloadLength = ReadPayloadLength(header.AsSpan(1));
        var payload = GetPayloadBuffer(header[0], payloadLength, rowBuffer);
        await stream.ReadExactlyAsync(payload.AsMemory(0, payloadLength), cancellationToken).ConfigureAwait(false);
        return (header[0], payload);
    }

    private static byte[] GetPayloadBuffer(byte tag, int length, RowPayloadBuffer? rows)
    {
        return tag == BackendMessage.DataRow && rows is not null
            ? rows.GetBuffer(length)
            : new byte[length];
    }

    /// <summary>Parses the frame length field, which counts itself, and returns the payload length.</summary>
    private static int ReadPayloadLength(ReadOnlySpan<byte> lengthField)
    {
        var length = BinaryPrimitives.ReadInt32BigEndian(lengthField);
        return length is < 4 or > MaximumFrameLength
            ? throw new IOException("Invalid pgwire frame length.")
            : length - 4;
    }

    /// <summary>Writes one frame and zeroes any pooled buffer afterwards because payloads can carry credentials.</summary>
    public static async Task WriteFrontendFrameAsync(Stream stream, byte tag, byte[] payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(payload);
        var frameLength = checked(payload.Length + 5);
        var rentedFrame = frameLength <= PooledFrameLimit ? ArrayPool<byte>.Shared.Rent(frameLength) : null;
        var frame = rentedFrame ?? new byte[frameLength];
        WriteHeader(frame, tag, payload.Length);
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

    internal static void WriteHeader(Span<byte> frame, byte tag, int payloadLength)
    {
        frame[0] = tag;
        BinaryPrimitives.WriteInt32BigEndian(frame[1..], checked(payloadLength + sizeof(int)));
    }

    /// <summary>Writes a pre-built batch of frames and zeroes its buffer afterwards.</summary>
    public static async Task WriteBatchAsync(Stream stream, MemoryStream batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(batch);
        var buffer = batch.GetBuffer();
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
