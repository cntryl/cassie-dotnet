using System.Buffers.Binary;
using System.Data;
using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace Cntryl.Cassie
{
    public sealed partial class CassieConnection
    {
        internal async Task<int> ExecuteCopyToAsync(string sql, Stream destination, int commandTimeout, CancellationToken cancellationToken)
        {
            ValidateSqlText(sql);
            if (_state != ConnectionState.Open || _stream is null)
            {
                throw new InvalidOperationException("The connection is not open.");
            }

            if (ReaderActive)
            {
                throw new InvalidOperationException("A reader is already active on this connection.");
            }

            ReaderActive = true;
            bool writingFrame = false;
            using CancellationTokenSource operationTimeout = new();
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operationTimeout.Token);
            if (commandTimeout > 0)
            {
                operationTimeout.CancelAfter(TimeSpan.FromSeconds(commandTimeout));
            }

            try
            {
                byte[] query = Encoding.UTF8.GetBytes(sql + "\0");
                writingFrame = true;
                await WriteFrontendFrameAsync((byte)'Q', query, linked.Token).ConfigureAwait(false);
                writingFrame = false;

                while (true)
                {
                    (byte tag, byte[] payload) = await ReadBackendFrameAsync(_stream, linked.Token).ConfigureAwait(false);
                    if (tag == (byte)'H')
                    {
                        ValidateCopyOutResponse(payload);
                        break;
                    }

                    if (tag == (byte)'S')
                    {
                        HandleParameterStatusMessage(payload);
                    }

                    if (tag == (byte)'G')
                    {
                        await SendCopyFailAsync("Use CassieCommand.CopyFromAsync to provide COPY input.").ConfigureAwait(false);
                        try
                        {
                            _ = await DrainCopyCompletionAsync(CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (CassieException)
                        {
                            // CopyFail is expected to produce an ErrorResponse before ReadyForQuery.
                        }

                        throw new InvalidOperationException("The command started COPY FROM STDIN; use CassieCommand.CopyFromAsync.");
                    }

                    if (tag == (byte)'E')
                    {
                        CassieException error = ParseServerError(payload);
                        _ = await DrainCopyCompletionAsync(CancellationToken.None).ConfigureAwait(false);
                        throw error;
                    }

                    if (tag == (byte)'Z')
                    {
                        HandleReadyForQuery(payload);
                        await ReaderClosedAsync().ConfigureAwait(false);
                        throw new InvalidOperationException("The command did not start a COPY TO STDOUT operation.");
                    }
                }

                while (true)
                {
                    (byte tag, byte[] payload) = await ReadBackendFrameAsync(_stream, linked.Token).ConfigureAwait(false);
                    if (tag == (byte)'d')
                    {
                        await destination.WriteAsync(payload, linked.Token).ConfigureAwait(false);
                    }
                    else if (tag == (byte)'S')
                    {
                        HandleParameterStatusMessage(payload);
                    }
                    else if (tag == (byte)'c')
                    {
                        return await DrainCopyCompletionAsync(linked.Token).ConfigureAwait(false);
                    }
                    else if (tag == (byte)'E')
                    {
                        CassieException error = ParseServerError(payload);
                        _ = await DrainCopyCompletionAsync(CancellationToken.None).ConfigureAwait(false);
                        throw error;
                    }
                }
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                if (writingFrame)
                {
                    AbortTransport();
                }
                else if (ReaderActive)
                {
                    try
                    {
                        if (await CancelCurrentQueryAsync().ConfigureAwait(false))
                        {
                            _ = await DrainCopyCompletionAsync(CancellationToken.None).ConfigureAwait(false);
                        }
                        else
                        {
                            AbortTransport();
                        }
                    }
                    catch (CassieException)
                    {
                        if (ReaderActive)
                        {
                            AbortTransport();
                        }
                    }
                    catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
                    {
                        AbortTransport();
                    }
                }

                if (operationTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"The COPY command exceeded {commandTimeout} seconds.");
                }

                throw;
            }
            finally
            {
                if (ReaderActive)
                {
                    AbortTransport();
                }
            }
        }

        internal async Task<int> ExecuteCopyFromAsync(string sql, Stream source, int commandTimeout, CancellationToken cancellationToken)
        {
            ValidateSqlText(sql);
            if (_state != ConnectionState.Open || _stream is null)
            {
                throw new InvalidOperationException("The connection is not open.");
            }

            if (ReaderActive)
            {
                throw new InvalidOperationException("A reader is already active on this connection.");
            }

            ReaderActive = true;
            bool requestMayHaveStarted = false;
            bool copyMode = false;
            bool writingFrame = false;
            using CancellationTokenSource operationTimeout = new();
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operationTimeout.Token);
            if (commandTimeout > 0)
            {
                operationTimeout.CancelAfter(TimeSpan.FromSeconds(commandTimeout));
            }

            try
            {
                byte[] query = Encoding.UTF8.GetBytes(sql + "\0");
                requestMayHaveStarted = true;
                writingFrame = true;
                await WriteFrontendFrameAsync((byte)'Q', query, linked.Token).ConfigureAwait(false);
                writingFrame = false;

                while (true)
                {
                    (byte tag, byte[] payload) = await ReadBackendFrameAsync(_stream, linked.Token).ConfigureAwait(false);
                    if (tag == (byte)'G')
                    {
                        ValidateCopyInResponse(payload);
                        copyMode = true;
                        break;
                    }

                    if (tag == (byte)'S')
                    {
                        HandleParameterStatusMessage(payload);
                    }

                    if (tag == (byte)'E')
                    {
                        CassieException error = ParseServerError(payload);
                        _ = await DrainCopyCompletionAsync(CancellationToken.None).ConfigureAwait(false);
                        throw error;
                    }

                    if (tag == (byte)'Z')
                    {
                        HandleReadyForQuery(payload);
                        await ReaderClosedAsync().ConfigureAwait(false);
                        throw new InvalidOperationException("The command did not start a COPY FROM STDIN operation.");
                    }
                }

                byte[] buffer = new byte[64 * 1024];
                while (true)
                {
                    int bytesRead = await source.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    byte[] payload = buffer.AsSpan(0, bytesRead).ToArray();
                    writingFrame = true;
                    await WriteFrontendFrameAsync((byte)'d', payload, linked.Token).ConfigureAwait(false);
                    writingFrame = false;
                }

                writingFrame = true;
                await WriteFrontendFrameAsync((byte)'c', [], linked.Token).ConfigureAwait(false);
                writingFrame = false;
                copyMode = false;
                return await DrainCopyCompletionAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                if (writingFrame)
                {
                    AbortTransport();
                }
                else if (copyMode)
                {
                    try
                    {
                        await SendCopyFailAsync("COPY input was cancelled.").ConfigureAwait(false);
                        copyMode = false;
                        _ = await DrainCopyCompletionAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (CassieException)
                    {
                        if (ReaderActive)
                        {
                            AbortTransport();
                        }
                    }
                    catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
                    {
                        AbortTransport();
                    }
                }
                else if (requestMayHaveStarted && ReaderActive)
                {
                    try
                    {
                        if (await CancelCurrentQueryAsync().ConfigureAwait(false))
                        {
                            _ = await DrainCopyCompletionAsync(CancellationToken.None).ConfigureAwait(false);
                        }
                        else
                        {
                            AbortTransport();
                        }
                    }
                    catch (CassieException)
                    {
                        if (ReaderActive)
                        {
                            AbortTransport();
                        }
                    }
                    catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
                    {
                        AbortTransport();
                    }
                }

                if (operationTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"The COPY command exceeded {commandTimeout} seconds.");
                }

                throw;
            }
            catch
            {
                if (ReaderActive)
                {
                    if (copyMode && !writingFrame)
                    {
                        try
                        {
                            await SendCopyFailAsync("COPY input stream failed.").ConfigureAwait(false);
                            copyMode = false;
                            _ = await DrainCopyCompletionAsync(CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (CassieException)
                        {
                            if (ReaderActive)
                            {
                                AbortTransport();
                            }
                        }
                        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
                        {
                            AbortTransport();
                        }
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
                if (ReaderActive)
                {
                    AbortTransport();
                }
            }
        }

        private async Task<int> DrainCopyCompletionAsync(CancellationToken cancellationToken)
        {
            CassieException? serverError = null;
            int recordsAffected = 0;
            while (true)
            {
                (byte tag, byte[] payload) = await ReadBackendFrameAsync(_stream!, cancellationToken).ConfigureAwait(false);
                if (tag == (byte)'C')
                {
                    string[] parts = Encoding.UTF8.GetString(payload).TrimEnd('\0').Split(' ');
                    if (parts.Length > 1 && int.TryParse(parts[^1], NumberStyles.None, CultureInfo.InvariantCulture, out int count))
                    {
                        recordsAffected = count;
                    }
                }
                else if (tag == (byte)'E')
                {
                    serverError = ParseServerError(payload);
                }
                else if (tag == (byte)'S')
                {
                    HandleParameterStatusMessage(payload);
                }
                else if (tag == (byte)'Z')
                {
                    HandleReadyForQuery(payload);
                    await ReaderClosedAsync().ConfigureAwait(false);
                    return serverError is null ? recordsAffected : throw serverError;
                }
            }
        }

        private async Task SendCopyFailAsync(string message)
        {
            using MemoryStream payload = new();
            WriteCString(payload, message);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            await WriteFrontendFrameAsync((byte)'f', payload.ToArray(), timeout.Token).ConfigureAwait(false);
        }

        internal async Task RejectCopyFromAsync()
        {
            try
            {
                await SendCopyFailAsync("Use CassieCommand.CopyFromAsync to provide COPY input.").ConfigureAwait(false);
                try
                {
                    _ = await DrainCopyCompletionAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (CassieException)
                {
                    // CopyFail is expected to produce an ErrorResponse before ReadyForQuery.
                }
            }
            catch
            {
                AbortTransport();
                throw;
            }

            throw new NotSupportedException("COPY FROM STDIN requires CassieCommand.CopyFromAsync.");
        }

        private static void ValidateCopyInResponse(byte[] payload)
        {
            if (payload.Length < 3 || payload[0] is not 0 and not 1)
            {
                throw new IOException("Cassie returned an invalid COPY format.");
            }

            short columnCount = BinaryPrimitives.ReadInt16BigEndian(payload.AsSpan(1));
            if (columnCount < 0 || payload.Length != 3 + (columnCount * sizeof(short)))
            {
                throw new IOException("Invalid CopyInResponse column-format list from Cassie.");
            }

            for (int index = 0; index < columnCount; index++)
            {
                short formatCode = BinaryPrimitives.ReadInt16BigEndian(payload.AsSpan(3 + (index * sizeof(short))));
                if (formatCode is not 0 and not 1)
                {
                    throw new IOException("Cassie returned an invalid COPY column format code.");
                }
            }
        }

        private static void ValidateCopyOutResponse(byte[] payload)
        {
            if (payload.Length < 3 || payload[0] is not 0 and not 1)
            {
                throw new IOException("Cassie returned an invalid COPY output format.");
            }

            short columnCount = BinaryPrimitives.ReadInt16BigEndian(payload.AsSpan(1));
            if (columnCount < 0 || payload.Length != 3 + (columnCount * sizeof(short)))
            {
                throw new IOException("Invalid CopyOutResponse column-format list from Cassie.");
            }

            for (int index = 0; index < columnCount; index++)
            {
                short formatCode = BinaryPrimitives.ReadInt16BigEndian(payload.AsSpan(3 + (index * sizeof(short))));
                if (formatCode is not 0 and not 1)
                {
                    throw new IOException("Cassie returned an invalid COPY output column format code.");
                }
            }
        }

    }
}
