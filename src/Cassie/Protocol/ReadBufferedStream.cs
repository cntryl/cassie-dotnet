namespace Cntryl.Cassie.Protocol;

/// <summary>
///     Buffers reads from a transport stream so many small pgwire frames cost one transport read.
///     Writes and flushes pass straight through; the wrapper owns the inner stream.
/// </summary>
internal sealed class ReadBufferedStream(Stream inner, int bufferSize = ReadBufferedStream.DefaultBufferSize) : Stream
{
    internal const int DefaultBufferSize = 8192;
    private readonly byte[] _buffer = GC.AllocateUninitializedArray<byte>(bufferSize);
    private int _count;
    private int _offset;

    /// <summary>
    ///     Gets or sets whether the asynchronous members block on the calling thread and return completed tasks.
    ///     Set for the duration of a synchronous ADO.NET call so it never waits on the thread pool.
    /// </summary>
    internal bool Synchronous { get; set; }

    /// <summary>Gets the bytes already read from the transport and not yet consumed.</summary>
    internal ReadOnlySpan<byte> Buffered => _buffer.AsSpan(_offset, _count);

    /// <inheritdoc />
    public override bool CanRead => inner.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => inner.CanWrite;

    /// <inheritdoc />
    public override bool CanTimeout => inner.CanTimeout;

    /// <inheritdoc />
    public override int ReadTimeout
    {
        get => inner.ReadTimeout;
        set => inner.ReadTimeout = value;
    }

    /// <inheritdoc />
    public override int WriteTimeout
    {
        get => inner.WriteTimeout;
        set => inner.WriteTimeout = value;
    }

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Marks buffered bytes as consumed.</summary>
    internal void Consume(int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)byteCount, (uint)_count, nameof(byteCount));
        _offset += byteCount;
        _count -= byteCount;
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        if (_count == 0)
        {
            if (buffer.Length >= _buffer.Length)
            {
                return inner.Read(buffer);
            }

            _offset = 0;
            _count = inner.Read(_buffer);
        }

        return CopyBuffered(buffer);
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return _count > 0
            ? ValueTask.FromResult(CopyBuffered(buffer.Span))
            : Synchronous
                ? ValueTask.FromResult(ReadBlocking(buffer.Span, cancellationToken))
                : buffer.Length >= _buffer.Length
                    ? inner.ReadAsync(buffer, cancellationToken)
                    : FillAndCopyAsync(buffer, cancellationToken);
    }

    private int ReadBlocking(Span<byte> destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var abort = RegisterAbort(cancellationToken);
        int read;
        try
        {
            read = Read(destination);
        }
        catch (Exception exception) when (IsAbortedByCancellation(exception, cancellationToken))
        {
            throw new OperationCanceledException("The read was canceled.", exception, cancellationToken);
        }

        // Closing a socket under a blocked read can surface as end-of-stream instead of an exception.
        return read == 0 && destination.Length > 0 && cancellationToken.IsCancellationRequested
            ? throw new OperationCanceledException("The read was canceled.", cancellationToken)
            : read;
    }

    private void WriteBlocking(ReadOnlySpan<byte> source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var abort = RegisterAbort(cancellationToken);
        try
        {
            inner.Write(source);
        }
        catch (Exception exception) when (IsAbortedByCancellation(exception, cancellationToken))
        {
            throw new OperationCanceledException("The write was canceled.", exception, cancellationToken);
        }
    }

    /// <summary>A blocking call cannot observe a token, so cancellation closes the transport underneath it.</summary>
    private CancellationTokenRegistration RegisterAbort(CancellationToken cancellationToken)
    {
        return cancellationToken.UnsafeRegister(static transport => ((Stream)transport!).Dispose(), inner);
    }

    private static bool IsAbortedByCancellation(Exception exception, CancellationToken cancellationToken)
    {
        return cancellationToken.IsCancellationRequested && exception is IOException or ObjectDisposedException;
    }

    private async ValueTask<int> FillAndCopyAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = await inner.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
        _offset = 0;
        _count = read;
        return CopyBuffered(buffer.Span);
    }

    private int CopyBuffered(Span<byte> destination)
    {
        var copied = Math.Min(destination.Length, _count);
        _buffer.AsSpan(_offset, copied).CopyTo(destination);
        _offset += copied;
        _count -= copied;
        return copied;
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!Synchronous)
        {
            return inner.WriteAsync(buffer, cancellationToken);
        }

        WriteBlocking(buffer.Span, cancellationToken);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override void Flush()
    {
        inner.Flush();
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        if (!Synchronous)
        {
            return inner.FlushAsync(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        inner.Flush();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync()
    {
        // The base implementation disposes synchronously on the calling thread.
        return Synchronous ? base.DisposeAsync() : DisposeCoreAsync();
    }

    private async ValueTask DisposeCoreAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
