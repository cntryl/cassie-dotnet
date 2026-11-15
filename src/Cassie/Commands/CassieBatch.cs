using Cntryl.Cassie.Connection;
using Cntryl.Cassie.Data;
using Cntryl.Cassie.Diagnostics;
using System.Data;
using System.Data.Common;

namespace Cntryl.Cassie.Commands
{
    /// <summary>Sends several commands to Cassie in one network round trip.</summary>
    /// <remarks>
    /// <para>
    /// Every command is sent through the extended query protocol in a single write: Parse, Bind, Describe, and
    /// Execute for each command, followed by one Sync. Commands run in order and rows are not paged, so each command
    /// streams its full result without further round trips.
    /// </para>
    /// <para>
    /// Parameters of every command are validated and encoded before any byte is sent. An invalid parameter fails the
    /// call without touching the connection or its transaction.
    /// </para>
    /// <para>
    /// Because there is one Sync, the server skips the commands after the first one that fails. That error surfaces as
    /// a <see cref="CassieException"/> when the reader reaches the failing command (or from the Execute method), and the
    /// connection is ready for reuse afterwards. Outside an explicit transaction the server treats the whole batch as one
    /// implicit transaction and may undo commands that completed before the failure; use a transaction when that matters.
    /// </para>
    /// <para>A batch may be reused, but only one execution can be active on a connection at a time.</para>
    /// </remarks>
    public sealed class CassieBatch : DbBatch
    {
        private int _timeout = 30;
        private readonly CassieDataSource? _dataSource;
        private bool _disposed;

        /// <summary>Creates a batch with no connection.</summary>
        public CassieBatch()
        {
        }

        /// <summary>Creates a batch on a connection and optional transaction.</summary>
        /// <param name="connection">The connection that runs the batch.</param>
        /// <param name="transaction">The transaction the batch runs in, or <see langword="null"/>.</param>
        public CassieBatch(CassieConnection? connection, CassieTransaction? transaction = null)
        {
            DbConnection = connection;
            DbTransaction = transaction;
        }

        internal CassieBatch(CassieDataSource dataSource)
        {
            _dataSource = dataSource;
        }

        /// <summary>Gets the commands of this batch in execution order.</summary>
        public new CassieBatchCommandCollection BatchCommands { get; } = [];

        /// <inheritdoc />
        public override int Timeout
        {
            get => _timeout;
            set => _timeout = CommandTimeouts.Validate(value);
        }

        /// <inheritdoc />
        protected override DbBatchCommandCollection DbBatchCommands => BatchCommands;

        /// <inheritdoc />
        protected override DbConnection? DbConnection { get; set; }

        /// <inheritdoc />
        protected override DbTransaction? DbTransaction { get; set; }

        /// <summary>Creates a command that can be added to <see cref="BatchCommands"/>.</summary>
        /// <returns>A new, empty command.</returns>
        public new CassieBatchCommand CreateBatchCommand()
        {
            return new CassieBatchCommand();
        }

        /// <inheritdoc />
        protected override DbBatchCommand CreateDbBatchCommand()
        {
            return new CassieBatchCommand();
        }

        /// <inheritdoc />
        public override void Cancel()
        {
            if (DbConnection is CassieConnection connection)
            {
                connection.RequestCancel();
            }
        }

        /// <inheritdoc />
        public override int ExecuteNonQuery()
        {
            using CassieDataReader reader = ExecuteReaderSynchronously(CommandBehavior.Default);
            do
            {
                while (reader.Read()) { }
            }
            while (reader.NextResult());

            return reader.RecordsAffected;
        }

        /// <inheritdoc />
        public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken = default)
        {
            CassieDataReader reader = await ExecuteBatchReaderAsync(null, CommandBehavior.Default, cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                do
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { }
                }
                while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

                return reader.RecordsAffected;
            }
        }

        /// <summary>Executes the batch and returns the first column of the first row of the first result set that has columns.</summary>
        /// <remarks>
        /// Leading commands that return no columns, such as <c>INSERT</c>, are skipped. The rest of the batch is
        /// still executed and read to completion.
        /// </remarks>
        /// <returns>The value, <see cref="DBNull"/> when it is NULL, or <see langword="null"/> when there is no such row.</returns>
        public override object? ExecuteScalar()
        {
            using CassieDataReader reader = ExecuteReaderSynchronously(CommandBehavior.SingleRow);
            do
            {
                if (reader.FieldCount > 0)
                {
                    return reader.Read() ? reader.GetValue(0) : null;
                }
            }
            while (reader.NextResult());

            return null;
        }

        /// <summary>Executes the batch and returns the first column of the first row of the first result set that has columns.</summary>
        /// <param name="cancellationToken">A token that cancels the batch and recovers the connection.</param>
        /// <returns>The value, <see cref="DBNull"/> when it is NULL, or <see langword="null"/> when there is no such row.</returns>
        public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken = default)
        {
            CassieDataReader reader = await ExecuteBatchReaderAsync(null, CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                do
                {
                    if (reader.FieldCount > 0)
                    {
                        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? reader.GetValue(0) : null;
                    }
                }
                while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

                return null;
            }
        }

        /// <summary>Prepares each command of the batch as a server statement on the batch's connection.</summary>
        /// <remarks>
        /// The batch is validated first: connection state, transaction, and that every command binds and encodes its
        /// parameters. Each command is then prepared on the server, and later executions bind to those statements
        /// instead of parsing the SQL again. A command is prepared again automatically when its SQL, its parameter
        /// types, or the connection's session changes. Disposing the batch closes the statements. A batch without a
        /// connection (created from a data source) is only validated, because each execution uses a new connection.
        /// </remarks>
        public override void Prepare()
        {
            (CassieBatchCommand[] commands, CassiePreparedCommand[] prepared) = ValidateForPrepare();
            if (DbConnection is CassieConnection connection)
            {
                for (int index = 0; index < commands.Length; index++)
                {
                    commands[index].PreparedStatement.Request();
                    _ = commands[index].PreparedStatement.EnsurePrepared(connection, prepared[index], Timeout);
                }
            }
        }

        /// <summary>Prepares each command of the batch as a server statement on the batch's connection. See <see cref="Prepare"/>.</summary>
        /// <param name="cancellationToken">A token that cancels validation or preparation.</param>
        /// <returns>A task that completes when every command is prepared.</returns>
        public override async Task PrepareAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (CassieBatchCommand[] commands, CassiePreparedCommand[] prepared) = ValidateForPrepare();
            if (DbConnection is CassieConnection connection)
            {
                for (int index = 0; index < commands.Length; index++)
                {
                    commands[index].PreparedStatement.Request();
                    _ = await commands[index].PreparedStatement.EnsurePreparedAsync(connection, prepared[index], Timeout, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public override void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                foreach (CassieBatchCommand command in BatchCommands.Items)
                {
                    command.PreparedStatement.Release();
                }
            }

            base.Dispose();
        }

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                foreach (CassieBatchCommand command in BatchCommands.Items)
                {
                    await command.PreparedStatement.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }

            await base.DisposeAsync().ConfigureAwait(false);
        }

        /// <inheritdoc />
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            return ExecuteReaderSynchronously(behavior);
        }

        /// <inheritdoc />
        protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        {
            return await ExecuteBatchReaderAsync(null, behavior, cancellationToken).ConfigureAwait(false);
        }

        private CassieDataReader ExecuteReaderSynchronously(CommandBehavior behavior)
        {
            if (DbConnection is CassieConnection connection)
            {
                return connection.RunSynchronously(() => ExecuteBatchReaderAsync(null, behavior, CancellationToken.None));
            }

            if (_dataSource is null)
            {
                return SynchronousCall.Wait(ExecuteBatchReaderAsync(null, behavior, CancellationToken.None));
            }

            // Connectionless: open the connection with blocking I/O, then run the whole call on this thread.
            CassieConnection owned = _dataSource.OpenConnection();
            return owned.RunSynchronously(() => ExecuteBatchReaderAsync(owned, behavior, CancellationToken.None));
        }

        private (CassieBatchCommand[] Commands, CassiePreparedCommand[] Prepared) ValidateForPrepare()
        {
            CassieBatchCommand[] commands = SnapshotCommands();
            if (DbConnection is CassieConnection connection)
            {
                RequireOpen(connection);
            }
            else if (_dataSource is null)
            {
                throw new InvalidOperationException("The batch requires an open Cassie connection.");
            }

            CassiePreparedCommand[] preparedCommands = PrepareAll(commands);
            foreach (CassiePreparedCommand prepared in preparedCommands)
            {
                _ = ParameterizedQueryBatch.EncodeParameters(prepared);
            }

            return (commands, preparedCommands);
        }

        private CassieBatchCommand[] SnapshotCommands()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CassieBatchCommand[] commands = [.. BatchCommands.Items];
            if (commands.Length == 0)
            {
                throw new InvalidOperationException("A batch must contain at least one command.");
            }

            foreach (CassieBatchCommand command in commands)
            {
                if (command.CommandType is not (CommandType.Text or CommandType.StoredProcedure))
                {
                    throw new NotSupportedException($"Cassie does not support command type '{command.CommandType}'.");
                }
            }

            return commands;
        }

        private static CassiePreparedCommand[] PrepareAll(CassieBatchCommand[] commands)
        {
            return [.. commands.Select(static command => command.PrepareParameters())];
        }

        private void RequireOpen(CassieConnection connection)
        {
            if (connection.State != ConnectionState.Open)
            {
                throw new InvalidOperationException("The connection must be open to run a batch.");
            }

            connection.ValidateCommandTransaction(DbTransaction);
        }

        /// <summary>Uses the assigned connection, or opens one from the data source that the returned reader closes.</summary>
        private async Task<(CassieConnection Connection, bool Owned)> AcquireConnectionAsync(CassieConnection? preOpened, CancellationToken cancellationToken)
        {
            if (preOpened is not null)
            {
                return (preOpened, true);
            }

            if (DbConnection is CassieConnection assigned)
            {
                RequireOpen(assigned);
                return (assigned, false);
            }

            return _dataSource is null || DbConnection is not null
                ? throw new InvalidOperationException("The batch requires an open Cassie connection.")
                : DbTransaction is not null
                    ? throw new InvalidOperationException("A batch without a connection cannot run in a transaction.")
                    : (await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false), true);
        }

        private async Task<CassieDataReader> ExecuteBatchReaderAsync(CassieConnection? preOpened, CommandBehavior behavior, CancellationToken cancellationToken)
        {
            // Every attempt starts from zero, including one rejected before anything is sent.
            foreach (CassieBatchCommand command in BatchCommands.Items)
            {
                command.RecordsAffectedValue = 0;
            }

            if ((behavior & CommandBehavior.SchemaOnly) != 0)
            {
                throw new NotSupportedException("A batch cannot return schema only; describe each command separately.");
            }

            CassieDiagnostics.OperationScope? operationScope = CassieDiagnostics.StartOperation("batch");
            CassieConnection? connection = preOpened;
            bool owned = preOpened is not null;
            try
            {
                CassieBatchCommand[] commands = SnapshotCommands();

                // Local validation comes first, so a bad parameter never opens a connection or touches a transaction.
                CassiePreparedCommand[] prepared = PrepareAll(commands);
                EncodedParameters[] encoded = [.. prepared.Select(static command => ParameterizedQueryBatch.EncodeParameters(command))];
                (connection, owned) = await AcquireConnectionAsync(preOpened, cancellationToken).ConfigureAwait(false);

                // Prepared statements belong to the caller's session; an owned connection is new for each execution.
                string?[] statementNames = new string?[commands.Length];
                if (!owned)
                {
                    for (int index = 0; index < commands.Length; index++)
                    {
                        if (commands[index].PreparedStatement.IsRequested)
                        {
                            statementNames[index] = await commands[index].PreparedStatement.EnsurePreparedAsync(connection, prepared[index], Timeout, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                int completed = 0;
                CassieDataReader reader = await connection.ExecuteBatchAsync(
                    prepared,
                    encoded,
                    statementNames,
                    Timeout,
                    owned ? behavior | CommandBehavior.CloseConnection : behavior,
                    operationScope,
                    tag =>
                    {
                        if (completed < commands.Length)
                        {
                            commands[completed].RecordsAffectedValue = tag is { CountsRecords: true } counted ? counted.RowCount : -1;
                        }

                        completed++;
                    },
                    cancellationToken).ConfigureAwait(false);

                // The reader now owns the scope and finishes it when the response completes.
                operationScope = null;
                return reader;
            }
            catch (Exception exception)
            {
                operationScope?.Fail(exception, connection?.Logger);
                if (owned && connection is not null)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }
            finally
            {
                operationScope?.Dispose();
            }
        }
    }
}
