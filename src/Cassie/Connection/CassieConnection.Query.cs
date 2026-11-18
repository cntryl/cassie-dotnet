using System.Data;
using System.Security.Cryptography;
using System.Text;
using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Data;
using Cntryl.Cassie.Diagnostics;
using Cntryl.Cassie.Protocol;

namespace Cntryl.Cassie.Connection;

public sealed partial class CassieConnection
{
    internal Task<CassieDataReader> ExecuteQueryAsync(string sql, CancellationToken cancellationToken)
    {
        ValidateSqlText(sql);
        return ExecuteQueryCoreAsync(sql, default, CommandBehavior.Default, null, true, cancellationToken);
    }

    internal async Task<CassieDataReader> ExecuteQueryCoreAsync(string sql, CommandBudget budget,
        CommandBehavior behavior, CassieDiagnostics.OperationScope? operationScope, bool refreshDateStyle,
        CancellationToken cancellationToken)
    {
        var session = RequireSession("The connection is not open.");
        ThrowIfReaderActive();
        var query = Encoding.UTF8.GetBytes(sql + "\0");
        if (refreshDateStyle)
        {
            await RefreshDateStyleIfNeededAsync(budget, cancellationToken).ConfigureAwait(false);
        }

        session.DateStyle.ObserveSql(sql);
        return await RunReaderAsync(
            token => session.WriteFrameAsync(FrontendMessage.Query, query, token),
            () => new CassieDataReader(new ConnectionReaderSession(this, session), budget, behavior, operationScope),
            budget, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<CassieDataReader> ExecuteParameterizedQueryAsync(CassiePreparedCommand prepared,
        CommandBudget budget, CancellationToken cancellationToken, CommandBehavior behavior = CommandBehavior.Default,
        bool binaryResults = false, string? statementName = null,
        CassieDiagnostics.OperationScope? operationScope = null, EncodedParameters? encodedParameters = null)
    {
        ValidateSqlText(prepared.Sql);
        var session = RequireSession("The connection is not open.");
        ThrowIfReaderActive();
        var schemaOnly = (behavior & CommandBehavior.SchemaOnly) != 0;
        using MemoryStream batch = new();
        try
        {
            // Local validation must finish before a refresh or execution touches the session.
            if (schemaOnly)
            {
                ParameterizedQueryBatch.AppendSchemaRequest(batch, prepared, statementName);
            }
            else
            {
                ParameterizedQueryBatch.AppendExecuteRequest(batch, prepared, statementName, binaryResults,
                    _portalPageFrames, encodedParameters);
            }

            if (!binaryResults)
            {
                await RefreshDateStyleIfNeededAsync(budget, cancellationToken).ConfigureAwait(false);
            }

            session.DateStyle.ObserveSql(prepared.Sql);
            return await RunReaderAsync(
                token => session.WriteBatchAsync(batch, token),
                () => schemaOnly
                    ? new CassieDataReader(new ConnectionReaderSession(this, session), budget, behavior,
                        operationScope)
                    : new CassieDataReader(new ConnectionReaderSession(this, session), budget, behavior,
                        operationScope, _portalPageFrames),
                budget,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(batch.GetBuffer());
        }
    }

    internal async Task ExecuteControlQueryAsync(string sql, CancellationToken cancellationToken)
    {
        var reader = await ExecuteQueryAsync(sql, cancellationToken).ConfigureAwait(false);
        try
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // Control statements return no rows of interest; reading to the end completes the response.
            }
        }
        finally
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void ThrowIfReaderActive()
    {
        if (_gate.IsActive)
        {
            throw new InvalidOperationException("A reader is already active on this connection.");
        }
    }

    /// <summary>
    ///     Sends one request and returns a reader positioned on its first result. The gate is held from the first
    ///     byte written, so any failure after that point leaves the stream out of sync and aborts the transport.
    /// </summary>
    private async Task<CassieDataReader> RunReaderAsync(Func<CancellationToken, Task> sendRequest,
        Func<CassieDataReader> createReader, CommandBudget budget, CancellationToken cancellationToken)
    {
        using var deadline = new CommandDeadline(budget, cancellationToken);
        deadline.ThrowIfExpired();
        _gate.Begin();
        var serial = _gate.Serial;
        CassieDataReader? reader = null;
        var initialized = false;
        try
        {
            try
            {
                await sendRequest(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (deadline.IsCancellationRequested &&
                exception is OperationCanceledException or IOException or ObjectDisposedException or System.Net.Sockets.SocketException)
            {
                deadline.ThrowIfExpired();
                throw;
            }

            reader = createReader();
            await reader.InitializeAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            initialized = true;
            return reader;
        }
        catch (OperationCanceledException) when (reader is not null && cancellationToken.IsCancellationRequested)
        {
            await reader.CancelAndDrainAsync().ConfigureAwait(false);
            throw;
        }
        catch
        {
            AbortTransport(expectedOperation: serial);
            throw;
        }
        finally
        {
            if (!initialized && reader is not null)
            {
                await reader.StopCancellationAsync().ConfigureAwait(false);
            }
        }
    }

    internal CommandBudget StartCommandBudget(int commandTimeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = RequireSession("The connection is not open.");
        ThrowIfReaderActive();
        return new CommandBudget(commandTimeout);
    }
}
