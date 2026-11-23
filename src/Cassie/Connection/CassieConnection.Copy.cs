using System.Buffers;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Cntryl.Cassie.Protocol;

namespace Cntryl.Cassie.Connection;

public sealed partial class CassieConnection
{
    private const int CopyBufferSize = 64 * 1024;
    private static readonly TimeSpan CopyRecoveryTimeout = TimeSpan.FromSeconds(5);

    // Each connection supplies its clock so recovery tests can advance the fixed deadline without sleeping.
    internal TimeProvider CopyRecoveryTimeProvider { get; set; } = TimeProvider.System;

    internal async Task<int> ExecuteCopyToAsync(
        string sql,
        Stream destination,
        int commandTimeout,
        CancellationToken cancellationToken
    )
    {
        ValidateSqlText(sql);
        var session = RequireSession("The connection is not open.");
        ThrowIfReaderActive();
        _gate.Begin();
        using CommandDeadline deadline = new(commandTimeout, cancellationToken);
        var writingFrame = false;

        try
        {
            writingFrame = true;
            await session.WriteFrameAsync(FrontendMessage.Query, Encoding.UTF8.GetBytes(sql + "\0"), deadline.Token)
                .ConfigureAwait(false);
            writingFrame = false;
            await AwaitCopyStartAsync(session, BackendMessage.CopyOutResponse, "output", "COPY TO STDOUT",
                deadline.Token).ConfigureAwait(false);
            return await StreamCopyOutAsync(session, destination, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            var failure = TimeoutOrOriginal(deadline, commandTimeout, cancellationToken);

            if (writingFrame)
            {
                AbortTransport();
            }
            else if (_gate.IsActive)
            {
                await RecoverCopyAsync(CancelAndDrainCopyAsync).ConfigureAwait(false);
            }


            throw failure;
        }
        finally
        {
            AbortIfStillActive();
        }
    }

    internal async Task<int> ExecuteCopyFromAsync(
        string sql,
        Stream source,
        int commandTimeout,
        CancellationToken cancellationToken
    )
    {
        ValidateSqlText(sql);
        var session = RequireSession("The connection is not open.");
        ThrowIfReaderActive();
        _gate.Begin();
        using CommandDeadline deadline = new(commandTimeout, cancellationToken);
        var copyMode = false;
        var writingFrame = false;
        byte[]? buffer = null;

        try
        {
            writingFrame = true;
            await session.WriteFrameAsync(FrontendMessage.Query, Encoding.UTF8.GetBytes(sql + "\0"), deadline.Token)
                .ConfigureAwait(false);
            writingFrame = false;
            await AwaitCopyStartAsync(session, BackendMessage.CopyInResponse, "input", "COPY FROM STDIN",
                deadline.Token).ConfigureAwait(false);
            copyMode = true;
            buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize + PgFraming.HeaderLength);
            var payload = buffer.AsMemory(PgFraming.HeaderLength, CopyBufferSize);

            while (true)
            {
                var bytesRead = await source.ReadAsync(payload, deadline.Token).ConfigureAwait(false);

                if (bytesRead == 0)
                {
                    break;
                }


                if ((uint)bytesRead > (uint)payload.Length)
                {
                    throw new IOException("The COPY input stream returned an invalid byte count.");
                }

                writingFrame = true;
                PgFraming.WriteHeader(buffer, FrontendMessage.CopyData, bytesRead);
                await session.WriteRawAsync(buffer.AsMemory(0, PgFraming.HeaderLength + bytesRead), deadline.Token)
                    .ConfigureAwait(false);
                writingFrame = false;
            }


            writingFrame = true;
            await session.WriteFrameAsync(FrontendMessage.CopyDone, [], deadline.Token).ConfigureAwait(false);
            writingFrame = false;
            copyMode = false;
            return await DrainCopyCompletionAsync(session, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            var failure = TimeoutOrOriginal(deadline, commandTimeout, cancellationToken);

            if (writingFrame)
            {
                AbortTransport();
            }
            else if (copyMode)
            {
                await RecoverCopyAsync(token => FailCopyInputAsync("COPY input was cancelled.", token))
                    .ConfigureAwait(false);
            }
            else if (_gate.IsActive)
            {
                await RecoverCopyAsync(CancelAndDrainCopyAsync).ConfigureAwait(false);
            }


            throw failure;
        }
        catch
        {
            if (_gate.IsActive)
            {
                if (copyMode && !writingFrame)
                {
                    await RecoverCopyAsync(token => FailCopyInputAsync("COPY input stream failed.", token))
                        .ConfigureAwait(false);
                }
                else
                {
                    AbortTransport();
                }
            }

            throw;
        }
        finally
        {
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }

            AbortIfStillActive();
        }
    }

    internal async Task RejectCopyFromAsync()
    {
        await RecoverCopyAsync(token =>
            FailCopyInputAsync("Use CassieCommand.CopyFromAsync to provide COPY input.", token)).ConfigureAwait(false);
        throw new NotSupportedException("COPY FROM STDIN requires CassieCommand.CopyFromAsync.");
    }

    /// <summary>A copy that did not reach ReadyForQuery leaves the stream in an unknown state, so the session is discarded.</summary>
    private void AbortIfStillActive()
    {
        if (_gate.IsActive)
        {
            AbortTransport();
        }
    }

    private static Exception TimeoutOrOriginal(
        CommandDeadline deadline,
        int commandTimeout,
        CancellationToken cancellationToken
    )
    {
        return deadline.TimedOut
            ? new TimeoutException($"The COPY command exceeded {commandTimeout} seconds.")
            : new OperationCanceledException(cancellationToken);
    }

    /// <summary>Runs a recovery exchange; if it fails the session is discarded because the stream position is unknown.</summary>
    private async Task RecoverCopyAsync(Func<CancellationToken, Task> recovery)
    {
        using CancellationTokenSource timeout = new(CopyRecoveryTimeout, CopyRecoveryTimeProvider);
        // Close the borrowed socket on expiry, including an unresponsive prepared-statement cleanup.
        // The recovery task is still awaited so none of its callbacks can act on a later operation.
        using var abortOnDeadline =
            timeout.Token.UnsafeRegister(static state => ((PgSession?)state)?.Client.Dispose(), _session);

        try
        {
            await recovery(timeout.Token).ConfigureAwait(false);
            await abortOnDeadline.DisposeAsync().ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
        }
        catch (CassieException)
        {
            AbortIfStillActive();
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException
                                              or ObjectDisposedException or InvalidOperationException)
        {
            AbortTransport();
        }
    }

    private async Task CancelAndDrainCopyAsync(CancellationToken cancellationToken)
    {
        var session = _session!;

        if (session.CanCancel && await QueryCanceler.SendAsync([with(_connectionString)], session.BackendProcessId,
                session.BackendSecretKey, cancellationToken).ConfigureAwait(false))
        {
            _ = await DrainCopyCompletionAsync(session, cancellationToken, true).ConfigureAwait(false);
        }
        else
        {
            AbortTransport();
        }
    }

    /// <summary>Sends CopyFail and drains the server's response within one shared recovery deadline.</summary>
    private async Task FailCopyInputAsync(string message, CancellationToken cancellationToken)
    {
        var session = _session!;
        using MemoryStream payload = new();
        PgWriter.WriteCString(payload, message);
        await session.WriteFrameAsync(FrontendMessage.CopyFail, payload.ToArray(), cancellationToken)
            .ConfigureAwait(false);

        try
        {
            _ = await DrainCopyCompletionAsync(session, cancellationToken, true).ConfigureAwait(false);
        }
        catch (CassieException)
        {
            // CopyFail is expected to produce an ErrorResponse before ReadyForQuery.
        }
    }

    private async Task RecoverCopyServerErrorAsync(PgSession session)
    {
        await RecoverCopyAsync(async token =>
        {
            _ = await DrainCopyCompletionAsync(session, token, true).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>
    ///     Reads until the server confirms the expected COPY direction, translating any other outcome into the matching
    ///     failure.
    /// </summary>
    private async Task AwaitCopyStartAsync(
        PgSession session,
        byte expectedTag,
        string direction,
        string operation,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            var (tag, payload) = await session.ReadFrameAsync(cancellationToken).ConfigureAwait(false);

            if (tag == expectedTag)
            {
                ValidateCopyResponse(payload, direction);
                return;
            }


            switch (tag)
            {
                case BackendMessage.ParameterStatus:
                    HandleParameterStatusMessage(payload);
                    break;
                case BackendMessage.CopyInResponse:
                    await RecoverCopyAsync(token =>
                            FailCopyInputAsync("Use CassieCommand.CopyFromAsync to provide COPY input.", token))
                        .ConfigureAwait(false);
                    throw new InvalidOperationException(
                        "The command started COPY FROM STDIN; use CassieCommand.CopyFromAsync.");
                case BackendMessage.ErrorResponse:
                    var error = ServerErrorParser.Parse(payload);
                    await RecoverCopyServerErrorAsync(session).ConfigureAwait(false);
                    throw error;
                case BackendMessage.ReadyForQuery:
                    HandleReadyForQuery(payload);
                    await ReaderClosedAsync().ConfigureAwait(false);
                    throw new InvalidOperationException($"The command did not start a {operation} operation.");
            }
        }
    }

    private async Task<int> StreamCopyOutAsync(
        PgSession session,
        Stream destination,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            var (tag, payload) = await session.ReadFrameAsync(cancellationToken).ConfigureAwait(false);

            switch (tag)
            {
                case BackendMessage.CopyData:
                    await destination.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                    break;
                case BackendMessage.ParameterStatus:
                    HandleParameterStatusMessage(payload);
                    break;
                case BackendMessage.CopyDone:
                    return await DrainCopyCompletionAsync(session, cancellationToken).ConfigureAwait(false);
                case BackendMessage.ErrorResponse:
                    var error = ServerErrorParser.Parse(payload);
                    await RecoverCopyServerErrorAsync(session).ConfigureAwait(false);
                    throw error;
            }
        }
    }

    private async Task<int> DrainCopyCompletionAsync(
        PgSession session,
        CancellationToken cancellationToken,
        bool recovering = false
    )
    {
        CassieException? serverError = null;
        var recordsAffected = 0;

        while (true)
        {
            var (tag, payload) = await session.ReadFrameAsync(cancellationToken).ConfigureAwait(false);

            switch (tag)
            {
                case BackendMessage.CommandComplete:
                    recordsAffected = CommandTag.FromPayload(payload).RowCount;
                    break;
                case BackendMessage.ErrorResponse:
                    serverError ??= ServerErrorParser.Parse(payload);
                    if (!recovering)
                    {
                        await RecoverCopyServerErrorAsync(session).ConfigureAwait(false);
                        throw serverError;
                    }

                    break;
                case BackendMessage.ParameterStatus:
                    HandleParameterStatusMessage(payload);
                    break;
                case BackendMessage.ReadyForQuery:
                    HandleReadyForQuery(payload);
                    await ReaderClosedAsync().ConfigureAwait(false);
                    return serverError is null ? recordsAffected : throw serverError;
            }
        }
    }

    private static void ValidateCopyResponse(byte[] payload, string direction)
    {
        if (payload.Length < 3 || payload[0] is not 0 and not 1)
        {
            throw new IOException($"Cassie returned an invalid COPY {direction} format.");
        }

        var columnCount = BinaryPrimitives.ReadInt16BigEndian(payload.AsSpan(1));

        if (columnCount < 0 || payload.Length != 3 + (columnCount * sizeof(short)))
        {
            throw new IOException($"Invalid COPY {direction} column-format list from Cassie.");
        }


        for (var index = 0; index < columnCount; index++)
        {
            var formatCode = BinaryPrimitives.ReadInt16BigEndian(payload.AsSpan(3 + (index * sizeof(short))));

            if (formatCode is not 0 and not 1)
            {
                throw new IOException($"Cassie returned an invalid COPY {direction} column format code.");
            }
        }
    }
}
