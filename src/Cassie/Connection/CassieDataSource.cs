using System.Data.Common;
using Cntryl.Cassie.Commands;
using Microsoft.Extensions.Logging;

namespace Cntryl.Cassie.Connection;

/// <summary>Creates independently owned Cassie connections with shared connection settings.</summary>
/// <remarks>Disposal prevents new connections. Existing connections and the logger factory remain caller-owned.</remarks>
public sealed class CassieDataSource : DbDataSource
{
    private readonly Lazy<int> _defaultCommandTimeout;
    private int _disposed;

    /// <summary>Creates a data source without opening a connection.</summary>
    public CassieDataSource(string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        ConnectionString = connectionString;
        _defaultCommandTimeout = new Lazy<int>(() => CassieConnection.CommandTimeoutFrom(connectionString));
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

    /// <summary>Creates a batch that opens a pooled connection for each execution and closes it when the result is read.</summary>
    /// <remarks>A connectionless batch has no <see cref="DbBatch.Connection" /> and cannot be given a transaction.</remarks>
    protected override DbBatch CreateDbBatch()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return new CassieBatch(this) { Timeout = _defaultCommandTimeout.Value };
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
