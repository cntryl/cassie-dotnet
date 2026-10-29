using Microsoft.Extensions.Logging;
using System.Data.Common;

namespace Cntryl.Cassie.Connection
{
    /// <summary>Creates independently owned Cassie connections with shared connection settings.</summary>
    /// <remarks>Disposal prevents new connections. Existing connections and the logger factory remain caller-owned.</remarks>
    public sealed class CassieDataSource : DbDataSource
    {
        private int _disposed;

        /// <summary>Creates a data source without opening a connection.</summary>
        public CassieDataSource(string connectionString)
        {
            ArgumentNullException.ThrowIfNull(connectionString);
            ConnectionString = connectionString;
        }

        /// <inheritdoc />
        public override string ConnectionString { get; }

        /// <summary>Gets the caller-owned logger factory copied to each connection created by this source.</summary>
        public ILoggerFactory? LoggerFactory { get; init; }

        /// <summary>Creates a new closed Cassie connection, owned by the caller.</summary>
        public new CassieConnection CreateConnection()
        {
            return (CassieConnection)base.CreateConnection();
        }

        /// <summary>Creates and opens a Cassie connection, owned by the caller.</summary>
        public new CassieConnection OpenConnection()
        {
            return (CassieConnection)base.OpenConnection();
        }

        /// <summary>Creates and asynchronously opens a Cassie connection, owned by the caller.</summary>
        public new async ValueTask<CassieConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            return (CassieConnection)await base.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        protected override DbConnection CreateDbConnection()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return new CassieConnection { ConnectionString = ConnectionString, LoggerFactory = LoggerFactory };
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            _ = Interlocked.Exchange(ref _disposed, 1);
            base.Dispose(disposing);
        }
    }
}
