using System.Data;
using System.Data.Common;
using System.Net.Sockets;
using Cntryl.Cassie.Connection;

namespace Cntryl.Cassie.Data;

/// <summary>Represents an active transaction on a Cassie connection.</summary>
public sealed class CassieTransaction : DbTransaction
{
    private readonly CassieConnection _connection;
    private bool _completed;

    internal CassieTransaction(CassieConnection connection, IsolationLevel isolationLevel)
    {
        _connection = connection;
        IsolationLevel = isolationLevel;
    }

    /// <inheritdoc />
    public override IsolationLevel IsolationLevel { get; }

    /// <summary>Gets a value indicating that savepoints are available through Save, Rollback and Release.</summary>
    public override bool SupportsSavepoints => true;

    /// <summary>Gets the connection this transaction belongs to, or null once the transaction has ended.</summary>
    public new CassieConnection? Connection => _completed ? null : _connection;

    /// <inheritdoc />
    protected override DbConnection? DbConnection => Connection;

    /// <inheritdoc />
    public override void Commit()
    {
        Complete("COMMIT");
    }

    /// <inheritdoc />
    public override void Rollback()
    {
        Complete("ROLLBACK");
    }

    /// <inheritdoc />
    public override async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        await CompleteAsync("COMMIT", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Rolls the transaction back.</summary>
    public Task RollbackAsync()
    {
        return RollbackAsync(CancellationToken.None);
    }

    /// <inheritdoc />
    public override async Task RollbackAsync(CancellationToken cancellationToken)
    {
        await CompleteAsync("ROLLBACK", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override void Save(string savepointName)
    {
        ExecuteSavepoint("SAVEPOINT", savepointName);
    }

    /// <inheritdoc />
    public override void Rollback(string savepointName)
    {
        ExecuteSavepoint("ROLLBACK TO SAVEPOINT", savepointName);
    }

    /// <inheritdoc />
    public override void Release(string savepointName)
    {
        ExecuteSavepoint("RELEASE SAVEPOINT", savepointName);
    }

    /// <inheritdoc />
    public override async Task SaveAsync(string savepointName, CancellationToken cancellationToken = default)
    {
        await ExecuteSavepointAsync("SAVEPOINT", savepointName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Rolls back to a savepoint, undoing the commands executed after it was created.</summary>
    public Task RollbackAsync(string savepointName)
    {
        return RollbackAsync(savepointName, CancellationToken.None);
    }

    /// <inheritdoc />
    public override async Task RollbackAsync(string savepointName, CancellationToken cancellationToken)
    {
        await ExecuteSavepointAsync("ROLLBACK TO SAVEPOINT", savepointName, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task ReleaseAsync(string savepointName, CancellationToken cancellationToken = default)
    {
        await ExecuteSavepointAsync("RELEASE SAVEPOINT", savepointName, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_completed && _connection.State == ConnectionState.Open)
        {
            try
            {
                Rollback();
            }
            catch (Exception exception) when (exception is CassieException or IOException or SocketException
                                                  or ObjectDisposedException or InvalidOperationException
                                                  or TimeoutException)
            {
                CompleteLocally();
            }
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (!_completed && _connection.State == ConnectionState.Open)
        {
            try
            {
                await RollbackAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is CassieException or IOException or SocketException
                                                  or ObjectDisposedException or InvalidOperationException
                                                  or TimeoutException)
            {
                CompleteLocally();
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void Complete(string sql)
    {
        _connection.RunSynchronously(() => CompleteAsync(sql, CancellationToken.None));
    }

    private async Task CompleteAsync(string sql, CancellationToken cancellationToken)
    {
        EnsureActive();
        await _connection.ExecuteControlQueryAsync(sql, cancellationToken).ConfigureAwait(false);
        CompleteLocally();
    }

    private void ExecuteSavepoint(string operation, string name)
    {
        _connection.RunSynchronously(() => ExecuteSavepointAsync(operation, name, CancellationToken.None));
    }

    private async Task ExecuteSavepointAsync(string operation, string name, CancellationToken cancellationToken)
    {
        EnsureActive();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var quotedName = "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        await _connection.ExecuteControlQueryAsync($"{operation} {quotedName}", cancellationToken)
            .ConfigureAwait(false);
    }

    private void EnsureActive()
    {
        if (_completed || !ReferenceEquals(_connection.ActiveTransaction, this))
        {
            throw new InvalidOperationException("The transaction is no longer active.");
        }
    }

    private void CompleteLocally()
    {
        _completed = true;
        _connection.CompleteTransaction(this);
    }

    internal void ConnectionClosed()
    {
        CompleteLocally();
    }
}
