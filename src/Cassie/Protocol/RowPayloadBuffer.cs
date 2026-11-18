using System.Buffers;

namespace Cntryl.Cassie.Protocol;

/// <summary>Owns the reusable DataRow storage for one reader.</summary>
internal sealed class RowPayloadBuffer
{
    private const int MaximumReusableBytes = 64 * 1024;
    private byte[]? _rented;
    private int _payloadLength;

    internal byte[] GetBuffer(int length)
    {
        if (length > MaximumReusableBytes)
        {
            return GC.AllocateUninitializedArray<byte>(length);
        }

        if (_rented is null || length > _rented.Length)
        {
            Release();
            _rented = ArrayPool<byte>.Shared.Rent(Math.Max(length, 256));
        }

        _payloadLength = length;
        return _rented;
    }

    internal ReadOnlySpan<byte> GetPayload(byte[] payload)
    {
        return ReferenceEquals(payload, _rented) ? payload.AsSpan(0, _payloadLength) : payload;
    }

    internal void Release()
    {
        if (_rented is { } rented)
        {
            _rented = null;
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }
}
