using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Diagnostics;
using Cntryl.Cassie.Protocol;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Cntryl.Cassie.Connection
{
    /// <summary>Represents an ADO.NET connection to Cassie.</summary>
    public sealed partial class CassieConnection : DbConnection
    {
        private const int DefaultFetchSize = 128;
        private readonly Lock _abortGate = new();
        private readonly OperationGate _gate = new();
        private byte[] _portalPageFrames = ExtendedQueryMessages.ExecutePageFrames(DefaultFetchSize);
        private string _connectionString = string.Empty;
        private ConnectionState _state;
        private PgSession? _session;
        private CassieConnectionPool? _pool;
        private string? _poolKey;
        private bool _ownsPoolSlot;

        /// <inheritdoc />
        [AllowNull]
        public override string ConnectionString
        {
            get => _connectionString;
            set
            {
                if (_state != ConnectionState.Closed)
                {
                    throw new InvalidOperationException("The connection string cannot be changed while the connection is open.");
                }

                _connectionString = value ?? string.Empty;
            }
        }

        /// <inheritdoc />
        public override string Database
        {
            get
            {
                CassieConnectionStringBuilder settings = new(_connectionString);
                return string.IsNullOrWhiteSpace(settings.Database) ? settings.Username : settings.Database;
            }
        }

        /// <inheritdoc />
        public override string DataSource => new CassieConnectionStringBuilder(_connectionString).Host;

        /// <inheritdoc />
        public override string ServerVersion => _state == ConnectionState.Open && _session is not null ? _session.ServerVersion : throw new InvalidOperationException("The connection is not open.");

        /// <inheritdoc />
        public override ConnectionState State => _state;

        /// <inheritdoc />
        public override int ConnectionTimeout => new CassieConnectionStringBuilder(_connectionString).Timeout;

        /// <summary>Gets or sets the logger factory used for sanitized provider lifecycle and failure logs.</summary>
        /// <remarks>The factory is owned by the caller. Log messages exclude SQL, parameter values, credentials, and server error details.</remarks>
        public ILoggerFactory? LoggerFactory { get; set; }

        internal Guid SessionIdentity { get; private set; }

        internal ILogger? Logger => LoggerFactory?.CreateLogger("Cntryl.Cassie.CassieConnection");

        /// <summary>Gets the session DateStyle, or null when the server reported a value this provider cannot parse.</summary>
        internal CassieDateStyle? DateStyle => _session is null ? CassieDateStyle.Default : _session.DateStyle.Current;

        private void SetState(ConnectionState state)
        {
            ConnectionState originalState = _state;
            if (originalState == state)
            {
                return;
            }

            _state = state;
            OnStateChange(new StateChangeEventArgs(originalState, state));
        }

        /// <summary>Returns the live session, or throws when the connection is not open.</summary>
        private PgSession RequireSession(string message)
        {
            return _state == ConnectionState.Open && _session is not null ? _session : throw new InvalidOperationException(message);
        }

        /// <inheritdoc />
        public override void ChangeDatabase(string databaseName)
        {
            ChangeDatabaseAsync(databaseName, CancellationToken.None).GetAwaiter().GetResult();
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
            CassieConnectionStringBuilder settings = new(_connectionString);
            string currentDatabase = string.IsNullOrWhiteSpace(settings.Database) ? settings.Username : settings.Database;
            if (string.Equals(currentDatabase, databaseName, StringComparison.Ordinal))
            {
                return;
            }

            string previousConnectionString = _connectionString;
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
            OpenAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <inheritdoc />
        public override Task OpenAsync(CancellationToken cancellationToken)
        {
            return CassieDiagnostics.ObserveAsync("connection.open", () => OpenCoreAsync(cancellationToken), Logger);
        }

        private async Task OpenCoreAsync(CancellationToken cancellationToken)
        {
            if (_state != ConnectionState.Closed)
            {
                throw new InvalidOperationException("The connection is already open or opening.");
            }

            CassieConnectionStringBuilder settings = ValidateSettings();
            _portalPageFrames = ExtendedQueryMessages.ExecutePageFrames(settings.FetchSize);
            CassieConnectionPool? pool = null;
            string? poolKey = null;
            if (settings.Pooling)
            {
                (poolKey, pool) = CassieConnectionPools.Get(settings);
            }

            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (settings.Timeout > 0)
            {
                linked.CancelAfter(TimeSpan.FromSeconds(settings.Timeout));
            }

            bool holdsPoolSlot = false;
            try
            {
                SetState(ConnectionState.Connecting);
                PgSession? session = null;
                if (pool is not null)
                {
                    (session, poolKey, pool) = await RentFromPoolAsync(settings, poolKey!, pool, linked.Token).ConfigureAwait(false);
                    holdsPoolSlot = true;
                }

                session ??= await PgSessionConnector.ConnectAsync(settings, linked.Token).ConfigureAwait(false);
                session.DateStyle.BeginLease();
                _session = session;
                _pool = pool;
                _poolKey = poolKey;
                _ownsPoolSlot = pool is not null;
                SessionIdentity = Guid.NewGuid();
                SetState(ConnectionState.Open);
                CassieLogging.ConnectionOpened(Logger);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
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
            CassieConnectionStringBuilder settings = new(_connectionString);
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

        private static async Task<(PgSession? Session, string PoolKey, CassieConnectionPool Pool)> RentFromPoolAsync(CassieConnectionStringBuilder settings, string poolKey, CassieConnectionPool pool, CancellationToken cancellationToken)
        {
            while (true)
            {
                try
                {
                    (PgSession? session, _) = await pool.RentAsync(cancellationToken).ConfigureAwait(false);
                    return (session, poolKey, pool);
                }
                catch (ObjectDisposedException) when (!cancellationToken.IsCancellationRequested)
                {
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
            CloseAsync().GetAwaiter().GetResult();
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
                catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or CassieException or TimeoutException)
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
            if (_pool is not null && _session is not null && _state == ConnectionState.Open && !_gate.IsActive && await TryReturnToPoolAsync().ConfigureAwait(false))
            {
                return;
            }

            bool wasOpen = _state == ConnectionState.Open;
            ActiveTransaction?.ConnectionClosed();
            if (_session is not null && wasOpen)
            {
                try
                {
                    await _session.WriteFrameAsync(FrontendMessage.Terminate, [], CancellationToken.None).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // The peer is already gone; the transport is disposed below.
                }
            }

            CassieConnectionPool? poolToRelease = _pool;
            bool releasePoolSlot = _ownsPoolSlot;
            await DisposeTransportAsync().ConfigureAwait(false);
            _pool = null;
            _poolKey = null;
            _ownsPoolSlot = false;
            _preparedStatementsToClose.Clear();
            SetState(ConnectionState.Closed);
            if (poolToRelease is not null && releasePoolSlot)
            {
                poolToRelease.ReleaseReservedSlot();
            }

            if (wasOpen)
            {
                CassieLogging.ConnectionClosed(Logger);
            }
        }

        /// <summary>Resets the session and hands it back to its pool; returns false when the session must be discarded instead.</summary>
        private async Task<bool> TryReturnToPoolAsync()
        {
            try
            {
                if (ActiveTransaction is not null)
                {
                    await ExecuteControlQueryAsync("ROLLBACK", CancellationToken.None).ConfigureAwait(false);
                    ActiveTransaction.ConnectionClosed();
                }

                await ExecuteControlQueryAsync("DISCARD ALL", CancellationToken.None).ConfigureAwait(false);
                PgSession session = _session!;
                CassieConnectionPool pool = _pool!;
                _session = null;
                _pool = null;
                _poolKey = null;
                _ownsPoolSlot = false;
                _preparedStatementsToClose.Clear();
                SetState(ConnectionState.Closed);
                await pool.ReturnAsync(session).ConfigureAwait(false);
                CassieLogging.ConnectionClosed(Logger);
                return true;
            }
            catch (Exception exception) when (exception is IOException or SocketException or CassieException or OperationCanceledException or ObjectDisposedException)
            {
                ActiveTransaction?.ConnectionClosed();
                return false;
            }
        }

        /// <summary>Closes idle physical sessions held by the pool for the specified connection settings.</summary>
        /// <param name="connection">A Cassie connection containing the pool settings to clear.</param>
        public static void ClearPool(CassieConnection connection)
        {
            ArgumentNullException.ThrowIfNull(connection);
            string key = connection._poolKey ?? (connection._pool is null
                ? CassieConnectionPools.GetKey(new CassieConnectionStringBuilder(connection.ConnectionString))
                : throw new InvalidOperationException("The connection has no pool key."));
            CassieConnectionPools.ClearAsync(key).GetAwaiter().GetResult();
        }

        /// <summary>Closes idle physical sessions held by every Cassie connection pool.</summary>
        public static void ClearAllPools()
        {
            CassieConnectionPools.ClearAllAsync().GetAwaiter().GetResult();
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

        /// <inheritdoc />
        protected override DbCommand CreateDbCommand()
        {
            int commandTimeout = 30;
            try
            {
                commandTimeout = new CassieConnectionStringBuilder(_connectionString).CommandTimeout;
            }
            catch (ArgumentException)
            {
                // An invalid connection string is reported when the connection opens.
            }

            return new CassieCommand { Connection = this, CommandTimeout = commandTimeout };
        }
    }
}
