using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Text;
using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Diagnostics;
using Cntryl.Cassie.Protocol;
using Microsoft.Extensions.Logging;

namespace Cntryl.Cassie.Connection;

/// <summary>Represents an ADO.NET connection to Cassie.</summary>
public sealed partial class CassieConnection : DbConnection
{
    private const int DefaultFetchSize = 128;
    private readonly Lock _abortGate = new();
    private readonly OperationGate _gate = new();
    private int? _defaultCommandTimeout;
    private string _connectionString = string.Empty;
    private bool _ownsPoolSlot;
    private CassieConnectionPool? _pool;
    private string? _poolKey;
    private byte[] _portalPageFrames = ExtendedQueryMessages.ExecutePageFrames(DefaultFetchSize);
    private PgSession? _session;
    private ConnectionState _state;
    private bool _synchronous;

    /// <inheritdoc />
    [AllowNull]
    public override string ConnectionString
    {
        get => _connectionString;
        set
        {
            if (_state != ConnectionState.Closed)
            {
                throw new InvalidOperationException(
                    "The connection string cannot be changed while the connection is open.");
            }

            var connectionString = value ?? string.Empty;

            if (_connectionString != connectionString)
            {
                _connectionString = connectionString;
                _defaultCommandTimeout = null;
            }
        }
    }

    /// <inheritdoc />
    public override string Database
    {
        get
        {
            CassieConnectionStringBuilder settings = [with(_connectionString)];
            return string.IsNullOrWhiteSpace(settings.Database) ? settings.Username : settings.Database;
        }
    }

    /// <inheritdoc />
    public override string DataSource => new CassieConnectionStringBuilder(_connectionString).Host;

    /// <inheritdoc />
    public override string ServerVersion => _state == ConnectionState.Open && _session is not null
        ? _session.ServerVersion
        : throw new InvalidOperationException("The connection is not open.");

    /// <inheritdoc />
    public override ConnectionState State => _state;

    /// <summary>Gets the provider factory, so <c>DbProviderFactories.GetFactory(connection)</c> resolves Cassie.</summary>
    protected override DbProviderFactory DbProviderFactory => CassieProviderFactory.Instance;

    /// <inheritdoc />
    public override int ConnectionTimeout => new CassieConnectionStringBuilder(_connectionString).Timeout;

    /// <summary>Gets or sets the logger factory used for sanitized provider lifecycle and failure logs.</summary>
    /// <remarks>
    ///     The factory is owned by the caller. Log messages exclude SQL, parameter values, credentials, and server error
    ///     details.
    /// </remarks>
    public ILoggerFactory? LoggerFactory { get; set; }

    internal Guid SessionIdentity { get; private set; }

    internal ILogger? Logger => LoggerFactory?.CreateLogger("Cntryl.Cassie.CassieConnection");

    /// <summary>Gets the session DateStyle, or null when the server reported a value this provider cannot parse.</summary>
    internal CassieDateStyle? DateStyle => _session is null ? CassieDateStyle.Default : _session.DateStyle.Current;

    private void SetState(ConnectionState state)
    {
        var originalState = _state;

        if (originalState == state)
        {
            return;
        }


        _state = state;
        OnStateChange(new StateChangeEventArgs(originalState, state));
    }

    /// <summary>
    ///     Switches the connection to blocking I/O for a synchronous call. Returns false when an enclosing
    ///     synchronous call already did, so only the outermost call switches back.
    /// </summary>
    internal bool EnterSynchronous()
    {
        if (_synchronous)
        {
            return false;
        }

        _synchronous = true;
        _session?.SynchronousIO = true;
        return true;
    }

    internal void ExitSynchronous()
    {
        _synchronous = false;
        _session?.SynchronousIO = false;
    }

    /// <summary>Runs an operation on the calling thread using blocking I/O.</summary>
    internal void RunSynchronously(Func<Task> operation)
    {
        var entered = EnterSynchronous();

        try
        {
            SynchronousCall.Wait(operation());
        }
        finally
        {
            if (entered)
            {
                ExitSynchronous();
            }
        }
    }

    /// <summary>Runs an operation on the calling thread using blocking I/O.</summary>
    internal T RunSynchronously<T>(Func<Task<T>> operation)
    {
        var entered = EnterSynchronous();

        try
        {
            return SynchronousCall.Wait(operation());
        }
        finally
        {
            if (entered)
            {
                ExitSynchronous();
            }
        }
    }

    /// <summary>Returns the live session, or throws when the connection is not open.</summary>
    private PgSession RequireSession(string message)
    {
        return _state == ConnectionState.Open && _session is not null
            ? _session
            : throw new InvalidOperationException(message);
    }

    /// <inheritdoc />
    public override void ChangeDatabase(string databaseName)
    {
        RunSynchronously(() => ChangeDatabaseAsync(databaseName, CancellationToken.None));
    }

    /// <inheritdoc />
    public override Task ChangeDatabaseAsync(string databaseName, CancellationToken cancellationToken = default)
    {
        return ChangeDatabaseCoreAsync(databaseName, cancellationToken);
    }

    private async Task ChangeDatabaseCoreAsync(string databaseName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(databaseName);

        if (databaseName.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Database name cannot contain a NUL character.", nameof(databaseName));
        }


        if (_state != ConnectionState.Open)
        {
            throw new InvalidOperationException("The connection must be open to change databases.");
        }

        if (_gate.IsActive)
        {
            throw new InvalidOperationException("The database cannot be changed while a reader is active.");
        }

        if (ActiveTransaction is not null)
        {
            throw new InvalidOperationException("The database cannot be changed while a transaction is active.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        CassieConnectionStringBuilder settings = [with(_connectionString)];
        var currentDatabase = string.IsNullOrWhiteSpace(settings.Database) ? settings.Username : settings.Database;

        if (string.Equals(currentDatabase, databaseName, StringComparison.Ordinal))
        {
            return;
        }


        var previousConnectionString = _connectionString;
        settings.Database = databaseName;
        await CloseAsync().ConfigureAwait(false);
        ConnectionString = settings.ConnectionString;

        try
        {
            await OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ConnectionString = previousConnectionString;
            throw;
        }
    }

    /// <inheritdoc />
    public override void Open()
    {
        RunSynchronously(() => OpenAsync(CancellationToken.None));
    }

    /// <inheritdoc />
    public override Task OpenAsync(CancellationToken cancellationToken)
    {
        return CassieDiagnostics.ObserveAsync("connection.open", () => OpenCoreAsync(cancellationToken), Logger);
    }

    private async Task OpenCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_state != ConnectionState.Closed)
        {
            throw new InvalidOperationException("The connection is already open or opening.");
        }


        var settings = ValidateSettings();
        _portalPageFrames = ExtendedQueryMessages.ExecutePageFrames(settings.FetchSize);
        CassieConnectionPool? pool = null;
        string? poolKey = null;

        if (settings.Pooling)
        {
            (poolKey, pool) = CassieConnectionPools.Get(settings);
        }


        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (settings.Timeout > 0)
        {
            linked.CancelAfter(TimeSpan.FromSeconds(settings.Timeout));
        }


        var holdsPoolSlot = false;

        try
        {
            linked.Token.ThrowIfCancellationRequested();
            SetState(ConnectionState.Connecting);
            PgSession? session = null;

            if (pool is not null)
            {
                (session, poolKey, pool) = await RentFromPoolAsync(settings, poolKey!, pool, _synchronous, linked.Token)
                    .ConfigureAwait(false);
                holdsPoolSlot = true;
            }


            session ??= await PgSessionConnector.ConnectAsync(settings, _synchronous, linked.Token)
                .ConfigureAwait(false);
            session.SynchronousIO = _synchronous;
            session.DateStyle.BeginLease();
            session.VectorOid = null;
            session.StatementsToClose = null;
            _session = session;
            _pool = pool;
            _poolKey = poolKey;
            _ownsPoolSlot = pool is not null;
            _pendingQueryCancel = null;
            SessionIdentity = Guid.NewGuid();
            SetState(ConnectionState.Open);
            CassieLogging.ConnectionOpened(Logger);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            await AbandonOpenAsync(pool, holdsPoolSlot).ConfigureAwait(false);
            throw new TimeoutException($"Opening a Cassie connection exceeded {settings.Timeout} seconds.");
        }
        catch
        {
            await AbandonOpenAsync(pool, holdsPoolSlot).ConfigureAwait(false);
            throw;
        }
    }

    private CassieConnectionStringBuilder ValidateSettings()
    {
        CassieConnectionStringBuilder settings = [with(_connectionString)];
        settings.ValidateKeywords();
        ValidateProtocolString(settings.Host, nameof(CassieConnectionStringBuilder.Host));
        ValidateProtocolString(settings.Username, nameof(CassieConnectionStringBuilder.Username));
        ValidateProtocolString(settings.Password, nameof(CassieConnectionStringBuilder.Password));
        ValidateProtocolString(settings.Database, nameof(CassieConnectionStringBuilder.Database));
        ValidateProtocolString(settings.ApplicationName, nameof(CassieConnectionStringBuilder.ApplicationName));
        _ = settings.Options;
        ValidateProtocolString(settings.SslRootCertificate, nameof(CassieConnectionStringBuilder.SslRootCertificate));

        if (string.IsNullOrWhiteSpace(settings.Username))
        {
            throw new InvalidOperationException("Username is required.");
        }


        if (string.IsNullOrWhiteSpace(settings.Database))
        {
            settings.Database = settings.Username;
        }

        return settings;
    }

    private static async Task<(PgSession? Session, string PoolKey, CassieConnectionPool Pool)> RentFromPoolAsync(
        CassieConnectionStringBuilder settings,
        string poolKey,
        CassieConnectionPool pool,
        bool synchronous,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            try
            {
                var (session, _) = await pool.RentAsync(synchronous, cancellationToken).ConfigureAwait(false);
                return (session, poolKey, pool);
            }
            catch (ObjectDisposedException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // The pool was cleared while this opener waited; join the replacement pool.
                (poolKey, pool) = CassieConnectionPools.Get(settings);
            }
        }
    }

    private async Task AbandonOpenAsync(CassieConnectionPool? pool, bool holdsPoolSlot)
    {
        await DisposeTransportAsync().ConfigureAwait(false);

        if (holdsPoolSlot)
        {
            pool?.ReleaseReservedSlot();
        }


        _pool = null;
        _poolKey = null;
        _ownsPoolSlot = false;
        SetState(ConnectionState.Closed);
    }

    /// <inheritdoc />
    public override void Close()
    {
        RunSynchronously(CloseAsync);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && _state != ConnectionState.Closed)
        {
            try
            {
                Close();
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException
                                                  or CassieException or TimeoutException)
            {
                // Disposal must not throw; tear the transport down instead.
                AbortTransport();
            }
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        try
        {
            await CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override Task CloseAsync()
    {
        return CassieDiagnostics.ObserveAsync("connection.close", CloseCoreAsync, Logger);
    }

    private async Task CloseCoreAsync()
    {
        PgSession session;
        CassieConnectionPool? pool;
        long serial;
        lock (_abortGate)
        {
            if (_gate.IsActive)
            {
                AbortTransport();
                return;
            }

            if (_session is not { } current || _state != ConnectionState.Open)
            {
                AbortTransport();
                return;
            }

            session = current;
            pool = _pool;
            _gate.Begin();
            serial = _cleanupOperation = _gate.Serial;
        }

        try
        {
            using CleanupDeadline deadline = new(session, CleanupTimeProvider);

            if (pool is null)
            {
                await session.WriteFrameAsync(FrontendMessage.Terminate, [], CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }


            if (ActiveTransaction is not null || session.DateStyle.TransactionStatus != TransactionStatus.Idle)
            {
                await ExecuteSessionCleanupQueryAsync(session, "ROLLBACK").ConfigureAwait(false);
            }

            await ExecuteSessionCleanupQueryAsync(session, "DISCARD ALL").ConfigureAwait(false);
            deadline.Complete();

            lock (_abortGate)
            {
                if (!_gate.IsActive || _gate.Serial != serial)
                {
                    return;
                }

                ActiveTransaction?.MarkCompleted();
                _gate.End();
                _session = null;
                _pool = null;
                _poolKey = null;
                _ownsPoolSlot = false;
                session.SynchronousIO = false;
                SetState(ConnectionState.Closed);
            }

            await pool.ReturnAsync(session).ConfigureAwait(false);
        }
        catch (Exception exception) when (CleanupDeadline.IsTransportFailure(exception) || exception is TimeoutException)
        {
            // A failed reset ends the physical session and frees the reserved slot below.
        }
        finally
        {
            AbortTransport(expectedOperation: serial);
            CassieLogging.ConnectionClosed(Logger);
        }
    }

    private static async Task ExecuteSessionCleanupQueryAsync(PgSession session, string sql)
    {
        await session.WriteFrameAsync(FrontendMessage.Query, Encoding.UTF8.GetBytes(sql + "\0"), CancellationToken.None)
            .ConfigureAwait(false);
        var acknowledged = false;

        while (true)
        {
            var (tag, payload) = await session.ReadFrameAsync(CancellationToken.None).ConfigureAwait(false);

            switch (tag)
            {
                case BackendMessage.CommandComplete when !acknowledged && CommandTag.FromPayload(payload).Matches(sql):
                    ObserveCommandComplete(session, new CommandTag(sql));
                    acknowledged = true;
                    break;
                case BackendMessage.ReadyForQuery when acknowledged &&
                                                       session.DateStyle.TryObserveReadyForQuery(payload) &&
                                                       session.DateStyle.TransactionStatus == TransactionStatus.Idle:
                    return;
                case BackendMessage.ParameterStatus:
                    var (name, value) = ParameterStatusMessage.Parse(payload);
                    session.DateStyle.ApplyParameterStatus(name, value);
                    break;
                case BackendMessage.NoticeResponse:
                case BackendMessage.NotificationResponse:
                    break;
                default:
                    throw new IOException($"Cassie did not confirm {sql} in an idle transaction state.");
            }
        }
    }

    /// <summary>Closes idle physical sessions held by the pool for the specified connection settings.</summary>
    /// <param name="connection">A Cassie connection containing the pool settings to clear.</param>
    public static void ClearPool(CassieConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var key = connection._poolKey ?? (connection._pool is null
            ? CassieConnectionPools.GetKey([with(connection.ConnectionString)])
            : throw new InvalidOperationException("The connection has no pool key."));
        SynchronousCall.Wait(CassieConnectionPools.ClearAsync(key));
    }

    /// <summary>Closes idle physical sessions held by every Cassie connection pool.</summary>
    public static void ClearAllPools()
    {
        SynchronousCall.Wait(CassieConnectionPools.ClearAllAsync());
    }

    internal static void ValidateSqlText(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        if (sql.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("SQL text cannot contain a NUL character.", nameof(sql));
        }
    }

    private static void ValidateProtocolString(string value, string parameterName)
    {
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException($"{parameterName} cannot contain a NUL character.", parameterName);
        }
    }

    private async Task DisposeTransportAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Creates a command associated with this connection.</summary>
    public new CassieCommand CreateCommand()
    {
        return (CassieCommand)CreateDbCommand();
    }

    /// <inheritdoc />
    protected override DbCommand CreateDbCommand()
    {
        return new CassieCommand { Connection = this, CommandTimeout = DefaultCommandTimeout() };
    }
}
