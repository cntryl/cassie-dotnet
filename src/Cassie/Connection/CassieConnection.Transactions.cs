using Cntryl.Cassie.Data;
using System.Data;
using System.Data.Common;
using System.Net.Sockets;

namespace Cntryl.Cassie.Connection
{
    public sealed partial class CassieConnection
    {
        internal CassieTransaction? ActiveTransaction { get; private set; }

        internal void ValidateCommandTransaction(DbTransaction? transaction)
        {
            if (ActiveTransaction is not null && transaction is not null && !ReferenceEquals(transaction, ActiveTransaction))
            {
                throw new InvalidOperationException("Commands on a connection with an active transaction must use that transaction.");
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
        public new async ValueTask<CassieTransaction> BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken)
        {
            return await BeginTransactionCoreAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            return BeginTransactionCore(isolationLevel);
        }

        /// <inheritdoc />
        protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken = default)
        {
            return await BeginTransactionCoreAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
        }

        private async Task<CassieTransaction> BeginTransactionCoreAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken)
        {
            IsolationLevel effective = isolationLevel == IsolationLevel.Unspecified ? IsolationLevel.ReadCommitted : isolationLevel;
            string level = effective switch
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
            catch (Exception exception) when (exception is IOException or SocketException or CassieException or OperationCanceledException or ObjectDisposedException or InvalidOperationException or TimeoutException)
            {
                // A failed rollback leaves the server transaction state unknown, so the session is discarded and the original cancellation propagates.
                AbortTransport();
            }
        }
    }
}
