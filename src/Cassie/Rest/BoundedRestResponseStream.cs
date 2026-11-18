namespace Cntryl.Cassie.Rest;

/// <summary>Limits bytes read from an HTTP response, before any charset transcoding.</summary>
internal sealed class BoundedRestResponseStream(Stream content, long limit) : Stream
{
    private readonly long _limit = limit;
    private long _remaining = limit;

    public override bool CanRead => content.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        return CountBytes(content.Read(buffer[..ReadLength(buffer.Length)]));
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = content.ReadAsync(buffer[..ReadLength(buffer.Length)], cancellationToken);
        return read.IsCompletedSuccessfully ? ValueTask.FromResult(CountBytes(read.Result)) : CountBytesAsync(read);
    }

    private async ValueTask<int> CountBytesAsync(ValueTask<int> read)
    {
        return CountBytes(await read.ConfigureAwait(false));
    }

    private int ReadLength(int requested)
    {
        // Probe one byte beyond the limit so both an exact-size body and EOF can be accepted.
        return (int)Math.Min(requested, _remaining + 1);
    }

    private int CountBytes(int count)
    {
        if (count > _remaining)
        {
            throw LimitExceeded(_limit);
        }

        _remaining -= count;
        return count;
    }

    internal static HttpRequestException LimitExceeded(long limit)
    {
        return new HttpRequestException($"The REST response exceeded MaxResponseContentBufferSize ({limit} bytes).");
    }

    public override void Flush()
    {
        throw new NotSupportedException();
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
