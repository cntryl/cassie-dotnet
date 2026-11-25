using System.Data;
using System.Data.Common;
using System.Net.Sockets;
using System.Text;
using Cntryl.Cassie.Data;
using Cntryl.Cassie.Protocol;

namespace Cntryl.Cassie.Connection;

public sealed partial class CassieConnection
{
    internal CassieTransaction? ActiveTransaction { get; private set; }

    internal void ValidateCommandTransaction(DbTransaction? transaction)
    {
        if (ActiveTransaction is not null && transaction is not null &&
            !ReferenceEquals(transaction, ActiveTransaction))
        {
            throw new InvalidOperationException(
                "Commands on a connection with an active transaction must use that transaction.");
        }

        if (ActiveTransaction is null && transaction is not null)
        {
            throw new InvalidOperationException("The command transaction is no longer active on this connection.");
        }
    }

    internal void CompleteTransaction(CassieTransaction transaction)
    {
        if (ReferenceEquals(ActiveTransaction, transaction))
        {
            ActiveTransaction = null;
        }
    }

    internal async Task EndTransactionAsync(CassieTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        var session = RequireSession("The connection is not open.");
        ThrowIfReaderActive();
        var query = Encoding.UTF8.GetBytes(sql + "\0");
        CommandBudget budget = new(0, Clock);
        long serial = 0;
        CommandTag? completion = null;
        var completionCount = 0;
        var serverFailed = false;
        var rolledBack = false;

        CassieDataReader CreateReader()
        {
            serial = _gate.Serial;
            var readerSession = new ConnectionReaderSession(this, session, ValidateReady);
            return new CassieDataReader(readerSession, budget, CommandBehavior.Default,
                statementCompleted: tag =>
                {
                    completion = tag;
                    completionCount++;
                }, statementFailed: _ => serverFailed = true);
        }

        void ValidateReady(byte status)
        {
            if (status == TransactionStatus.Idle)
            {
                transaction.MarkCompleted();
            }

            // Error and cancellation handling still use the reader's recovery path.
            if (serverFailed || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (status == TransactionStatus.Idle && completionCount == 1 && completion is { } tag)
            {
                rolledBack = sql == "COMMIT" && tag.Matches("ROLLBACK");

                if (rolledBack || tag.Matches(sql))
                {
                    return;
                }
            }

            throw RejectResponse();
        }

        IOException RejectResponse()
        {
            AbortTransport(expectedOperation: serial);
            return new IOException($"Cassie did not confirm {sql} in an idle transaction state.");
        }

        var reader = await RunReaderAsync(token => session.WriteFrameAsync(FrontendMessage.Query, query, token),
            CreateReader, budget, cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            // A transaction terminator returns no rows. The reader consumes its reply through ReadyForQuery.
            if (reader.FieldCount != 0)
            {
                throw RejectResponse();
            }

            if (rolledBack)
            {
                throw new CassieException("Cassie rolled back the transaction instead of committing it.");
            }
        }
    }

    internal async Task DisposeTransactionAsync(CassieTransaction transaction)
    {
        PgSession session;
        long serial;
        lock (_abortGate)
        {
            if (!ReferenceEquals(ActiveTransaction, transaction) || _session is not { } current)
            {
                return;
            }

            if (_gate.IsActive)
            {
                AbortTransport();
                return;
            }

            session = current;
            _gate.Begin();
            serial = _cleanupOperation = _gate.Serial;
        }

        try
        {
            using CleanupDeadline deadline = new(session, Clock);
            await ExecuteSessionCleanupQueryAsync(session, "ROLLBACK").ConfigureAwait(false);
            lock (_abortGate)
            {
                if (_gate.Serial != serial || !_gate.IsActive)
                {
                    return;
                }

                transaction.MarkCompleted();
            }

            await FinishQueryAsync(session, serial, null, deadline, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (CleanupDeadline.IsTransportFailure(exception) ||
                                          exception is CassieException or TimeoutException)
        {
            // Keep a synchronized session after a rejected statement close. Any unfinished exchange is aborted below.
        }
        finally
        {
            AbortTransport(expectedOperation: serial);
        }
    }

    internal CassieTransaction BeginTransactionCore(IsolationLevel isolationLevel)
    {
        return RunSynchronously(() => BeginTransactionCoreAsync(isolationLevel, CancellationToken.None));
    }

    /// <summary>Starts a READ COMMITTED transaction.</summary>
    public new CassieTransaction BeginTransaction()
    {
        return BeginTransactionCore(IsolationLevel.Unspecified);
    }

    /// <summary>Starts a transaction with the given isolation level.</summary>
    /// <exception cref="NotSupportedException">The isolation level is not supported by Cassie.</exception>
    public new CassieTransaction BeginTransaction(IsolationLevel isolationLevel)
    {
        return BeginTransactionCore(isolationLevel);
    }

    /// <summary>Starts a READ COMMITTED transaction.</summary>
    public ValueTask<CassieTransaction> BeginTransactionAsync()
    {
        return BeginTransactionAsync(IsolationLevel.Unspecified, CancellationToken.None);
    }

    /// <summary>Starts a READ COMMITTED transaction.</summary>
    public new ValueTask<CassieTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        return BeginTransactionAsync(IsolationLevel.Unspecified, cancellationToken);
    }

    /// <summary>Starts a transaction with the given isolation level.</summary>
    /// <exception cref="NotSupportedException">The isolation level is not supported by Cassie.</exception>
    public ValueTask<CassieTransaction> BeginTransactionAsync(IsolationLevel isolationLevel)
    {
        return BeginTransactionAsync(isolationLevel, CancellationToken.None);
    }

    /// <summary>Starts a transaction with the given isolation level.</summary>
    /// <exception cref="NotSupportedException">The isolation level is not supported by Cassie.</exception>
    public new async ValueTask<CassieTransaction> BeginTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken
    )
    {
        return await BeginTransactionCoreAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        return BeginTransactionCore(isolationLevel);
    }

    /// <inheritdoc />
    protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken = default
    )
    {
        return await BeginTransactionCoreAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CassieTransaction> BeginTransactionCoreAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken
    )
    {
        var effective = isolationLevel == IsolationLevel.Unspecified ? IsolationLevel.ReadCommitted : isolationLevel;
        var level = effective switch
        {
            IsolationLevel.ReadCommitted => "READ COMMITTED",
            _ => throw new NotSupportedException($"Cassie does not support isolation level '{isolationLevel}'.")
        };

        if (_state != ConnectionState.Open)
        {
            throw new InvalidOperationException("The connection must be open to begin a transaction.");
        }

        if (ActiveTransaction is not null)
        {
            throw new InvalidOperationException("This connection already has an active transaction.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await ExecuteControlQueryAsync($"BEGIN ISOLATION LEVEL {level}", cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RollbackCanceledTransactionStartAsync().ConfigureAwait(false);
            throw;
        }


        ActiveTransaction = new CassieTransaction(this, effective);
        return ActiveTransaction;
    }

    private async Task RollbackCanceledTransactionStartAsync()
    {
        if (_state != ConnectionState.Open)
        {
            return;
        }

        try
        {
            await ExecuteControlQueryAsync("ROLLBACK", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SocketException or CassieException
                                              or OperationCanceledException or ObjectDisposedException
                                              or InvalidOperationException or TimeoutException)
        {
            // A failed rollback leaves the server transaction state unknown, so the session is discarded and the original cancellation propagates.
            AbortTransport();
        }
    }
}
