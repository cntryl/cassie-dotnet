using System.Buffers;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Cntryl.Cassie
{
    /// <summary>Represents an ADO.NET connection to Cassie.</summary>
    public sealed partial class CassieConnection : DbConnection
    {
        private const int DefaultFetchSize = 128;
        private int _fetchSize = DefaultFetchSize;
        private byte[] _portalPageFrames = BuildPortalPageFrames(DefaultFetchSize);
        private readonly Lock _abortGate = new();
        private long _sessionCreatedTimestamp;
#pragma warning disable IDE0032 // The setter must count operations, so this is not an auto property.
        private bool _readerActiveValue;
#pragma warning restore IDE0032
        private long _operationSerial;
        private string _connectionString = string.Empty;
        private ConnectionState _state;
        private TcpClient? _client;
        private Stream? _stream;
        private string _serverVersion = string.Empty;
        private byte _transactionStatus = (byte)'I';
        private bool _transactionMayHaveChangedDateStyle;
        internal CassieDateStyle? DateStyle { get; private set; } = CassieDateStyle.Default;
        private CassieDateStyle StartupDateStyle { get; set; } = CassieDateStyle.Default;
        private bool _dateStyleNeedsRefresh;
        private bool _refreshingDateStyle;
        private bool ReaderActive
        {
            get => Volatile.Read(ref _readerActiveValue);
            set
            {
                if (value)
                {
                    _ = Interlocked.Increment(ref _operationSerial);
                }

                Volatile.Write(ref _readerActiveValue, value);
            }
        }
        private int _backendProcessId;
        private int _backendSecretKey;
        private CassieConnectionPool? _pool;
        private string? _poolKey;
        private bool _ownsPoolSlot;
        private int _nextPreparedStatementId;
        private readonly List<string> _preparedStatementsToClose = [];

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
        public override string ServerVersion => _state == ConnectionState.Open ? _serverVersion : throw new InvalidOperationException("The connection is not open.");
        /// <inheritdoc />
        public override ConnectionState State => _state;
        internal Guid SessionIdentity { get; private set; }
        /// <inheritdoc />
        public override int ConnectionTimeout => new CassieConnectionStringBuilder(_connectionString).Timeout;

        /// <summary>Gets or sets the logger factory used for sanitized provider lifecycle and failure logs.</summary>
        /// <remarks>The factory is owned by the caller. Log messages exclude SQL, parameter values, credentials, and server error details.</remarks>
        public ILoggerFactory? LoggerFactory { get; set; }

        internal ILogger? Logger => LoggerFactory?.CreateLogger("Cntryl.Cassie.CassieConnection");

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

            if (ReaderActive)
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

            _fetchSize = settings.FetchSize;
            _portalPageFrames = BuildPortalPageFrames(_fetchSize);
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
            bool reservedPoolSlot = false;
            _transactionStatus = (byte)'I';
            _transactionMayHaveChangedDateStyle = false;
            try
            {
                SetState(ConnectionState.Connecting);
                if (pool is not null)
                {
                    CassiePooledSession? session;
                    while (true)
                    {
                        try
                        {
                            (session, reservedPoolSlot) = await pool.RentAsync(linked.Token).ConfigureAwait(false);
                            break;
                        }
                        catch (ObjectDisposedException) when (!linked.IsCancellationRequested)
                        {
                            // The pool was cleared while this opener waited; join the replacement pool.
                            (poolKey, pool) = CassieConnectionPools.Get(settings);
                        }
                    }

                    if (session is not null)
                    {
                        _client = session.Client;
                        _stream = session.Stream;
                        _serverVersion = session.ServerVersion;
                        DateStyle = session.DateStyle;
                        StartupDateStyle = session.StartupDateStyle;
                        _dateStyleNeedsRefresh = false;
                        _backendProcessId = session.BackendProcessId;
                        _backendSecretKey = session.BackendSecretKey;
                        _sessionCreatedTimestamp = session.CreatedTimestamp;
                        _pool = pool;
                        _poolKey = poolKey;
                        _ownsPoolSlot = true;
                        SessionIdentity = Guid.NewGuid();
                        SetState(ConnectionState.Open);
                        CassieLogging.ConnectionOpened(Logger);
                        return;
                    }
                }

                DateStyle = CassieDateStyle.Default;
                _dateStyleNeedsRefresh = false;
                StartupDateStyle = CassieDateStyle.Default;
                _client = new TcpClient { NoDelay = true };
                await _client.ConnectAsync(settings.Host, settings.Port, linked.Token).ConfigureAwait(false);
                _stream = _client.GetStream();
                if (settings.SslMode != CassieSslMode.Disable)
                {
                    await NegotiateTlsAsync(settings, linked.Token).ConfigureAwait(false);
                }

                await SendStartupAsync(settings, linked.Token).ConfigureAwait(false);
                await CompleteAuthenticationAsync(settings, linked.Token).ConfigureAwait(false);
                _sessionCreatedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                StartupDateStyle = DateStyle ?? CassieDateStyle.Default;
                _pool = pool;
                _poolKey = poolKey;
                _ownsPoolSlot = reservedPoolSlot;
                SessionIdentity = Guid.NewGuid();
                SetState(ConnectionState.Open);
                CassieLogging.ConnectionOpened(Logger);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                await DisposeTransportAsync().ConfigureAwait(false);
                if (reservedPoolSlot)
                {
                    pool?.ReleaseReservedSlot();
                }

                _pool = null;
                _poolKey = null;
                _ownsPoolSlot = false;
                SetState(ConnectionState.Closed);
                throw new TimeoutException($"Opening a Cassie connection exceeded {settings.Timeout} seconds.");
            }
            catch
            {
                await DisposeTransportAsync().ConfigureAwait(false);
                if (reservedPoolSlot)
                {
                    pool?.ReleaseReservedSlot();
                }

                _pool = null;
                _poolKey = null;
                _ownsPoolSlot = false;
                SetState(ConnectionState.Closed);
                throw;
            }
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
        [SuppressMessage("Reliability", "CA2000", Justification = "The pooled session is transferred to the connection pool, which owns its lifetime.")]
        public override Task CloseAsync()
        {
            return CassieDiagnostics.ObserveAsync("connection.close", CloseCoreAsync, Logger);
        }

        [SuppressMessage("Reliability", "CA2000", Justification = "The pooled session is transferred to the connection pool, which owns its lifetime.")]
        private async Task CloseCoreAsync()
        {
            if (_pool is not null && _state == ConnectionState.Open && !ReaderActive)
            {
                try
                {
                    if (ActiveTransaction is not null)
                    {
                        await ExecuteControlQueryAsync("ROLLBACK", CancellationToken.None).ConfigureAwait(false);
                        ActiveTransaction.ConnectionClosed();
                    }

                    await ExecuteControlQueryAsync("DISCARD ALL", CancellationToken.None).ConfigureAwait(false);
                    CassiePooledSession session = new(_client!, _stream!, _serverVersion, DateStyle, StartupDateStyle, _backendProcessId, _backendSecretKey, _sessionCreatedTimestamp);
                    CassieConnectionPool pool = _pool;
                    _client = null;
                    _stream = null;
                    _serverVersion = string.Empty;
                    DateStyle = CassieDateStyle.Default;
                    StartupDateStyle = CassieDateStyle.Default;
                    _dateStyleNeedsRefresh = false;
                    _backendProcessId = 0;
                    _backendSecretKey = 0;
                    _pool = null;
                    _poolKey = null;
                    _ownsPoolSlot = false;
                    _preparedStatementsToClose.Clear();
                    SetState(ConnectionState.Closed);
                    await pool.ReturnAsync(session).ConfigureAwait(false);
                    CassieLogging.ConnectionClosed(Logger);
                    return;
                }
                catch (Exception exception) when (exception is IOException or SocketException or CassieException or OperationCanceledException or ObjectDisposedException)
                {
                    ActiveTransaction?.ConnectionClosed();
                }
            }

            bool wasOpen = _state == ConnectionState.Open;
            ActiveTransaction?.ConnectionClosed();
            if (_stream is not null && _state == ConnectionState.Open)
            {
                try { await _stream.WriteAsync(new byte[] { (byte)'X', 0, 0, 0, 4 }).ConfigureAwait(false); } catch (IOException) { }
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

        private async Task NegotiateTlsAsync(CassieConnectionStringBuilder settings, CancellationToken cancellationToken)
        {
            _stream = await NegotiateTlsCoreAsync(_stream!, settings, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<Stream> NegotiateTlsCoreAsync(Stream stream, CassieConnectionStringBuilder settings, CancellationToken cancellationToken)
        {
            await stream.WriteAsync(new byte[] { 0, 0, 0, 8, 4, 210, 22, 47 }, cancellationToken).ConfigureAwait(false);
            byte[] response = new byte[1];
            await stream.ReadExactlyAsync(response, cancellationToken).ConfigureAwait(false);
            if (response[0] == (byte)'N')
            {
                return settings.SslMode == CassieSslMode.Require
                    ? throw new InvalidOperationException("The server does not support TLS, but SSL Mode is Require.")
                    : stream;
            }

            if (response[0] != (byte)'S')
            {
                throw new IOException("The server returned an invalid TLS negotiation response.");
            }

            SslStream tls = new(stream, leaveInnerStreamOpen: false);
            SslClientAuthenticationOptions authenticationOptions = new() { TargetHost = settings.Host };
            if (settings.SslRootCertificate.Length > 0)
            {
                X509Certificate2Collection trustedRoots = [];
                trustedRoots.ImportFromPem(await File.ReadAllTextAsync(settings.SslRootCertificate, cancellationToken).ConfigureAwait(false));
                if (trustedRoots.Count == 0)
                {
                    throw new InvalidOperationException("SSL Root Certificate must contain at least one PEM certificate.");
                }

                X509ChainPolicy chainPolicy = new()
                {
                    TrustMode = X509ChainTrustMode.CustomRootTrust,
                    RevocationMode = X509RevocationMode.NoCheck
                };
                chainPolicy.CustomTrustStore.AddRange(trustedRoots);
                authenticationOptions.CertificateChainPolicy = chainPolicy;
            }

            await tls.AuthenticateAsClientAsync(authenticationOptions, cancellationToken).ConfigureAwait(false);
            return tls;
        }

        private async Task SendStartupAsync(CassieConnectionStringBuilder settings, CancellationToken cancellationToken)
        {
            using MemoryStream payload = new();
            await payload.WriteAsync(new byte[] { 0, 3, 0, 0 }, cancellationToken).ConfigureAwait(false);
            WriteCString(payload, "user"); WriteCString(payload, settings.Username);
            WriteCString(payload, "database"); WriteCString(payload, settings.Database);
            WriteCString(payload, "client_encoding"); WriteCString(payload, "UTF8");
            WriteCString(payload, "application_name"); WriteCString(payload, settings.ApplicationName);
            if (settings.Options.Length > 0)
            {
                WriteCString(payload, "options"); WriteCString(payload, settings.Options);
            }

            payload.WriteByte(0);
            byte[] body = payload.ToArray();
            byte[] packet = new byte[body.Length + 4];
            BinaryPrimitives.WriteInt32BigEndian(packet, packet.Length);
            body.CopyTo(packet, 4);
            await _stream!.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
        }

        private async Task CompleteAuthenticationAsync(CassieConnectionStringBuilder settings, CancellationToken cancellationToken)
        {
            bool authenticated = false;
            while (true)
            {
                (byte tag, byte[] payload) = await ReadBackendFrameAsync(_stream!, cancellationToken).ConfigureAwait(false);
                if (tag == (byte)'R')
                {
                    if (payload.Length != 4)
                    {
                        throw new IOException("Invalid authentication frame from server.");
                    }

                    int method = BinaryPrimitives.ReadInt32BigEndian(payload);
                    if (method == 3)
                    {
                        byte[] passwordPayload = new byte[Encoding.UTF8.GetByteCount(settings.Password) + 1];
                        _ = Encoding.UTF8.GetBytes(settings.Password, passwordPayload);
                        try
                        {
                            await WriteFrontendFrameAsync((byte)'p', passwordPayload, cancellationToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(passwordPayload);
                        }
                    }
                    else
                    {
                        authenticated = method == 0 ? true : throw new NotSupportedException($"Cassie requested unsupported authentication method {method}.");
                    }
                }
                else if (tag == (byte)'S')
                {
                    string[] fields = Encoding.UTF8.GetString(payload).Split('\0');
                    if (fields.Length >= 2)
                    {
                        if (fields[0] == "server_version")
                        {
                            _serverVersion = fields[1];
                        }

                        HandleParameterStatus(fields[0], fields[1]);
                    }
                }
                else if (tag == (byte)'K')
                {
                    if (payload.Length != 8)
                    {
                        throw new IOException("Invalid backend key data frame.");
                    }

                    _backendProcessId = BinaryPrimitives.ReadInt32BigEndian(payload);
                    _backendSecretKey = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(4));
                }
                else if (tag == (byte)'E')
                {
                    throw ParseServerError(payload);
                }
                else if (tag == (byte)'Z')
                {
                    if (!authenticated)
                    {
                        throw new IOException("The server became ready before authentication completed.");
                    }

                    HandleReadyForQuery(payload);
                    return;
                }
            }
        }

        internal void HandleParameterStatus(string name, string value)
        {
            if (string.Equals(name, "DateStyle", StringComparison.OrdinalIgnoreCase))
            {
                DateStyle = CassieDateStyle.TryParse(value, out CassieDateStyle style) ? style : null;
                _dateStyleNeedsRefresh = false;
            }
        }

        internal void HandleParameterStatusMessage(byte[] payload)
        {
            int nameEnd = Array.IndexOf(payload, (byte)0);
            if (nameEnd < 0)
            {
                AbortTransport();
                throw new IOException("Cassie returned an invalid ParameterStatus frame.");
            }

            int valueStart = nameEnd + 1;
            int valueEnd = Array.IndexOf(payload, (byte)0, valueStart);
            if (valueEnd != payload.Length - 1)
            {
                AbortTransport();
                throw new IOException("Cassie returned an invalid ParameterStatus frame.");
            }

            HandleParameterStatus(
                Encoding.UTF8.GetString(payload, 0, nameEnd),
                Encoding.UTF8.GetString(payload, valueStart, valueEnd - valueStart));
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

        internal void HandleReadyForQuery(ReadOnlySpan<byte> payload)
        {
            if (payload.Length != 1
                || (payload[0] != (byte)'I' && payload[0] != (byte)'T' && payload[0] != (byte)'E'))
            {
                AbortTransport();
                throw new IOException("Cassie returned an invalid ReadyForQuery transaction status.");
            }

            byte priorStatus = _transactionStatus;
            _transactionStatus = payload[0];
            if ((payload[0] == (byte)'I' && priorStatus != (byte)'I')
                || (payload[0] == (byte)'T' && priorStatus == (byte)'I'))
            {
                _transactionMayHaveChangedDateStyle = false;
            }
        }

        internal void MarkDateStyleNeedsRefresh()
        {
            DateStyle = null;
            _dateStyleNeedsRefresh = true;
        }

        private async Task RefreshDateStyleIfNeededAsync(CancellationToken cancellationToken)
        {
            if (!_dateStyleNeedsRefresh || _refreshingDateStyle || _state != ConnectionState.Open || ReaderActive || _transactionStatus == (byte)'E')
            {
                return;
            }

            _refreshingDateStyle = true;
            try
            {
                CassieDataReader reader = await ExecuteQueryAsync("SHOW DateStyle", cancellationToken).ConfigureAwait(false);
                try
                {
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        throw new IOException("Cassie did not return DateStyle from SHOW DateStyle.");
                    }

                    HandleParameterStatus("DateStyle", reader.GetString(0));
                }
                finally
                {
                    await reader.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _refreshingDateStyle = false;
            }
        }

        internal void ObserveCommandComplete(string tag)
        {
            ReadOnlySpan<char> command = tag.AsSpan().TrimStart();
            int separator = command.IndexOf(' ');
            ReadOnlySpan<char> verb = separator < 0 ? command : command[..separator];
            if (verb.Equals("SET", StringComparison.OrdinalIgnoreCase)
                || verb.Equals("RESET", StringComparison.OrdinalIgnoreCase))
            {
                MarkDateStyleNeedsRefresh();
                _transactionMayHaveChangedDateStyle |= _transactionStatus == (byte)'T';
            }
            else if (verb.Equals("COMMIT", StringComparison.OrdinalIgnoreCase)
                || verb.Equals("END", StringComparison.OrdinalIgnoreCase)
                || verb.Equals("ROLLBACK", StringComparison.OrdinalIgnoreCase)
                || verb.Equals("ABORT", StringComparison.OrdinalIgnoreCase))
            {
                if (_transactionMayHaveChangedDateStyle)
                {
                    MarkDateStyleNeedsRefresh();
                }
            }
            else if (tag.Equals("DISCARD ALL", StringComparison.OrdinalIgnoreCase))
            {
                DateStyle = StartupDateStyle;
                _dateStyleNeedsRefresh = false;
                _transactionMayHaveChangedDateStyle = false;
            }
        }

        private static async Task<(byte Tag, byte[] Payload)> ReadBackendFrameAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = new byte[5];
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
            if (length is < 4 or > (16 * 1024 * 1024))
            {
                throw new IOException("Invalid pgwire frame length.");
            }

            byte[] payload = new byte[length - 4];
            await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
            return (header[0], payload);
        }

        private async Task WriteFrontendFrameAsync(byte tag, byte[] payload, CancellationToken cancellationToken)
        {
            int frameLength = checked(payload.Length + 5);
            byte[]? rentedFrame = frameLength <= 64 * 1024 ? ArrayPool<byte>.Shared.Rent(frameLength) : null;
            byte[] frame = rentedFrame ?? new byte[frameLength];
            frame[0] = tag;
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), frameLength - 1);
            payload.CopyTo(frame, 5);
            try
            {
                await _stream!.WriteAsync(frame.AsMemory(0, frameLength), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (rentedFrame is not null)
                {
                    CryptographicOperations.ZeroMemory(rentedFrame);
                    ArrayPool<byte>.Shared.Return(rentedFrame);
                }
            }
        }

        private static byte[] BuildPortalPageFrames(int fetchSize)
        {
            using MemoryStream frames = new();
            using MemoryStream execute = new();
            WriteCString(execute, string.Empty);
            WriteInt32(execute, fetchSize);
            AppendFrame(frames, (byte)'E', execute.GetBuffer().AsSpan(0, (int)execute.Length));
            AppendFrame(frames, (byte)'H', []);
            return frames.ToArray();
        }

        private static void AppendFrame(MemoryStream batch, byte tag, ReadOnlySpan<byte> payload)
        {
            batch.WriteByte(tag);
            WriteInt32(batch, checked(payload.Length + 4));
            batch.Write(payload);
        }

        private async Task WriteFrontendBatchAsync(MemoryStream batch, CancellationToken cancellationToken)
        {
            byte[] buffer = batch.GetBuffer();
            try
            {
                await _stream!.WriteAsync(buffer.AsMemory(0, (int)batch.Length), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }

        private async Task ExecutePortalPageAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _stream!.WriteAsync(_portalPageFrames, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                AbortTransport();
                throw;
            }
        }

        private async Task SyncExtendedQueryAsync()
        {
            try
            {
                await WriteFrontendFrameAsync((byte)'S', [], CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                AbortTransport();
                throw;
            }
        }

        internal string PrepareServerStatement(CassiePreparedCommand prepared, int commandTimeout)
        {
            return PrepareServerStatementAsync(prepared, commandTimeout, CancellationToken.None).GetAwaiter().GetResult();
        }

        internal async Task<string> PrepareServerStatementAsync(CassiePreparedCommand prepared, int commandTimeout, CancellationToken cancellationToken)
        {
            ValidateSqlText(prepared.Sql);
            if (_state != ConnectionState.Open || _stream is null)
            {
                throw new InvalidOperationException("The connection must be open to prepare a command.");
            }

            if (ReaderActive)
            {
                throw new InvalidOperationException("A reader is already active on this connection.");
            }

            string statementName = "cassie_dotnet_" + _backendProcessId.ToString(CultureInfo.InvariantCulture) + "_" + Interlocked.Increment(ref _nextPreparedStatementId).ToString(CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N");
            ReaderActive = true;
            bool ready = false;
            CassieException? serverError = null;
            try
            {
                using MemoryStream parse = new();
                WriteCString(parse, statementName);
                WriteCString(parse, prepared.Sql);
                WriteInt16(parse, checked((short)prepared.ParameterTypeOids.Count));
                foreach (int typeOid in prepared.ParameterTypeOids)
                {
                    WriteInt32(parse, typeOid);
                }

                await WriteFrontendFrameAsync((byte)'P', parse.ToArray(), cancellationToken).ConfigureAwait(false);
                using MemoryStream describe = new();
                describe.WriteByte((byte)'S');
                WriteCString(describe, statementName);
                await WriteFrontendFrameAsync((byte)'D', describe.ToArray(), cancellationToken).ConfigureAwait(false);
                await WriteFrontendFrameAsync((byte)'S', [], cancellationToken).ConfigureAwait(false);
                using CancellationTokenSource timeout = commandTimeout > 0 ? new(TimeSpan.FromSeconds(commandTimeout)) : new(Timeout.InfiniteTimeSpan);
                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                while (!ready)
                {
                    (byte tag, byte[] payload) = await ReadBackendFrameAsync(_stream, linked.Token).ConfigureAwait(false);
                    if (tag == (byte)'E')
                    {
                        serverError = ParseServerError(payload);
                    }

                    if (tag == (byte)'Z')
                    {
                        HandleReadyForQuery(payload);
                        ready = true;
                    }
                }

                return serverError is not null ? throw serverError : statementName;
            }
            catch
            {
                if (!ready)
                {
                    AbortTransport();
                }

                throw;
            }
            finally
            {
                if (_state == ConnectionState.Open)
                {
                    ReaderActive = false;
                }
            }
        }

        internal void CloseServerStatement(string statementName)
        {
            CloseServerStatementAsync(statementName, CancellationToken.None).GetAwaiter().GetResult();
        }

        internal async Task CloseServerStatementAsync(string statementName, CancellationToken cancellationToken)
        {
            if (_state != ConnectionState.Open || _stream is null)
            {
                return;
            }

            if (ReaderActive)
            {
                if (!_preparedStatementsToClose.Contains(statementName, StringComparer.Ordinal))
                {
                    _preparedStatementsToClose.Add(statementName);
                }

                return;
            }

            ReaderActive = true;
            try
            {
                await CloseServerStatementCoreAsync(statementName, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (_state == ConnectionState.Open)
                {
                    await ReaderClosedAsync().ConfigureAwait(false);
                }
            }
        }

        private async Task CloseServerStatementCoreAsync(string statementName, CancellationToken cancellationToken)
        {
            bool ready = false;
            CassieException? serverError = null;
            try
            {
                using MemoryStream close = new();
                close.WriteByte((byte)'S');
                WriteCString(close, statementName);
                await WriteFrontendFrameAsync((byte)'C', close.ToArray(), cancellationToken).ConfigureAwait(false);
                await WriteFrontendFrameAsync((byte)'S', [], cancellationToken).ConfigureAwait(false);
                while (!ready)
                {
                    (byte tag, byte[] payload) = await ReadBackendFrameAsync(_stream!, cancellationToken).ConfigureAwait(false);
                    if (tag == (byte)'E')
                    {
                        serverError = ParseServerError(payload);
                    }

                    if (tag == (byte)'Z')
                    {
                        HandleReadyForQuery(payload);
                        ready = true;
                    }
                }

                if (serverError is not null && serverError.SqlState != "26000")
                {
                    throw serverError;
                }
            }
            catch
            {
                if (!ready)
                {
                    AbortTransport();
                }

                throw;
            }
        }

        internal async Task<CassieDataReader> ExecuteQueryAsync(string sql, CancellationToken cancellationToken, int commandTimeout = 0, CommandBehavior behavior = CommandBehavior.Default, CassieDiagnostics.OperationScope? operationScope = null)
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

            await RefreshDateStyleIfNeededAsync(cancellationToken).ConfigureAwait(false);
            if (sql.Contains("set_config", StringComparison.OrdinalIgnoreCase))
            {
                MarkDateStyleNeedsRefresh();
                _transactionMayHaveChangedDateStyle |= _transactionStatus == (byte)'T';
            }

            ReaderActive = true;
            bool requestMayHaveStarted = false;
            CassieDataReader? reader = null;
            try
            {
                byte[] query = Encoding.UTF8.GetBytes(sql + "\0");
                requestMayHaveStarted = true;
                await WriteFrontendFrameAsync((byte)'Q', query, cancellationToken).ConfigureAwait(false);
                reader = new(this, _stream, commandTimeout, behavior, operationScope);
                await reader.InitializeAsync(cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    await reader.CancelAndDrainAsync().ConfigureAwait(false);
                    throw new OperationCanceledException("The query was canceled.", cancellationToken);
                }

                return reader;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (reader is null)
                {
                    AbortTransport();
                }
                else
                {
                    await reader.CancelAndDrainAsync().ConfigureAwait(false);
                }

                throw;
            }
            catch
            {
                if (requestMayHaveStarted && ReaderActive)
                {
                    AbortTransport();
                }
                else if (!requestMayHaveStarted)
                {
                    ReaderActive = false;
                }

                throw;
            }
        }

        [SuppressMessage("Reliability", "CA2000", Justification = "The returned reader is transferred to the command caller, who owns disposal.")]
        internal async Task<CassieDataReader> ExecuteParameterizedQueryAsync(CassiePreparedCommand prepared, int commandTimeout, CancellationToken cancellationToken, CommandBehavior behavior = CommandBehavior.Default, bool binaryResults = false, string? statementName = null, CassieDiagnostics.OperationScope? operationScope = null)
        {
            ValidateSqlText(prepared.Sql);
            if (_state != ConnectionState.Open || _stream is null)
            {
                throw new InvalidOperationException("The connection is not open.");
            }

            if (ReaderActive)
            {
                throw new InvalidOperationException("A reader is already active on this connection.");
            }

            bool schemaOnly = (behavior & CommandBehavior.SchemaOnly) != 0;
            if (!schemaOnly && prepared.ParameterNames.Count == 0 && !binaryResults && statementName is null)
            {
                return await ExecuteQueryAsync(prepared.Sql, cancellationToken, commandTimeout, behavior, operationScope).ConfigureAwait(false);
            }

            if (prepared.Values.Count > short.MaxValue)
            {
                throw new NotSupportedException("The pgwire protocol limits one command to 32767 parameters.");
            }

            if (!binaryResults)
            {
                await RefreshDateStyleIfNeededAsync(cancellationToken).ConfigureAwait(false);
            }

            if (prepared.Sql.Contains("set_config", StringComparison.OrdinalIgnoreCase))
            {
                MarkDateStyleNeedsRefresh();
                _transactionMayHaveChangedDateStyle |= _transactionStatus == (byte)'T';
            }

            ReaderActive = true;
            bool requestMayHaveStarted = false;
            CassieDataReader? reader = null;
            using MemoryStream batch = new();
            try
            {
                requestMayHaveStarted = true;
                if (statementName is null)
                {
                    using MemoryStream parse = new();
                    WriteCString(parse, string.Empty);
                    WriteCString(parse, prepared.Sql);
                    WriteInt16(parse, checked((short)prepared.ParameterTypeOids.Count));
                    foreach (int typeOid in prepared.ParameterTypeOids)
                    {
                        WriteInt32(parse, typeOid);
                    }

                    AppendFrame(batch, (byte)'P', parse.GetBuffer().AsSpan(0, (int)parse.Length));
                }

                if (schemaOnly)
                {
                    using MemoryStream describeStatement = new();
                    describeStatement.WriteByte((byte)'S');
                    WriteCString(describeStatement, statementName ?? string.Empty);
                    AppendFrame(batch, (byte)'D', describeStatement.GetBuffer().AsSpan(0, (int)describeStatement.Length));
                    AppendFrame(batch, (byte)'S', []);
                    await WriteFrontendBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                    reader = new(this, _stream, commandTimeout, behavior, operationScope);
                    await reader.InitializeAsync(cancellationToken).ConfigureAwait(false);
                    return reader;
                }

                using MemoryStream bind = new();
                WriteCString(bind, string.Empty);
                WriteCString(bind, statementName ?? string.Empty);
                WriteInt16(bind, checked((short)prepared.Values.Count));
                byte[]?[] binaryParameters = new byte[prepared.Values.Count][];
                for (int index = 0; index < prepared.Values.Count; index++)
                {
                    CassieParameterFormat requestedFormat = prepared.ParameterFormats is { } formats ? formats[index] : CassieParameterFormat.Auto;
                    if (!Enum.IsDefined(requestedFormat))
                    {
                        throw new ArgumentOutOfRangeException(nameof(prepared), "A parameter format is invalid.");
                    }

                    byte[]? binary = requestedFormat == CassieParameterFormat.Text
                        ? null
                        : CassieParameterCodec.GetBinaryParameter(prepared.Values[index], prepared.ParameterTypeOids[index]);
                    if (requestedFormat == CassieParameterFormat.Binary && binary is null)
                    {
                        throw new NotSupportedException($"Binary parameters for PostgreSQL OID {prepared.ParameterTypeOids[index]} are not supported by this provider.");
                    }

                    binaryParameters[index] = requestedFormat == CassieParameterFormat.Text ? null : binary;
                    WriteInt16(bind, binaryParameters[index] is null ? (short)0 : (short)1);
                }

                WriteInt16(bind, checked((short)prepared.Values.Count));
                for (int index = 0; index < prepared.Values.Count; index++)
                {
                    object? value = prepared.Values[index];
                    if (value is null or DBNull)
                    {
                        WriteInt32(bind, -1);
                        continue;
                    }

                    byte[]? binary = binaryParameters[index];
                    byte[] bytes = binary ?? CassieParameterCodec.EncodeTextParameter(value, prepared.ParameterTypeOids[index]);
                    WriteInt32(bind, bytes.Length);
                    bind.Write(bytes);
                }

                WriteInt16(bind, binaryResults ? (short)1 : (short)0);
                if (binaryResults)
                {
                    WriteInt16(bind, 1);
                }
                AppendFrame(batch, (byte)'B', bind.GetBuffer().AsSpan(0, (int)bind.Length));
                using MemoryStream describe = new();
                describe.WriteByte((byte)'P');
                WriteCString(describe, string.Empty);
                AppendFrame(batch, (byte)'D', describe.GetBuffer().AsSpan(0, (int)describe.Length));
                batch.Write(_portalPageFrames);
                await WriteFrontendBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                reader = new(this, _stream, commandTimeout, behavior, operationScope, ExecutePortalPageAsync, SyncExtendedQueryAsync);
                await reader.InitializeAsync(cancellationToken).ConfigureAwait(false);
                return reader;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (reader is null)
                {
                    AbortTransport();
                }
                else
                {
                    await reader.CancelAndDrainAsync().ConfigureAwait(false);
                }

                throw;
            }
            catch
            {
                if (requestMayHaveStarted && ReaderActive)
                {
                    AbortTransport();
                }
                else if (!requestMayHaveStarted)
                {
                    ReaderActive = false;
                }

                throw;
            }
        }

        private static void WriteInt16(Stream stream, short value)
        {
            Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteInt16BigEndian(bytes, value);
            stream.Write(bytes);
        }

        private static void WriteInt32(Stream stream, int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            stream.Write(bytes);
        }

        internal Task<(byte Tag, byte[] Payload)> ReadQueryFrameAsync(Stream stream, CancellationToken cancellationToken)
        {
            return ReadBackendFrameAsync(stream, cancellationToken);
        }

        internal async Task ReaderClosedAsync()
        {
            ReaderActive = false;
            if (_preparedStatementsToClose.Count == 0)
            {
                return;
            }

            ReaderActive = true;
            try
            {
                while (_preparedStatementsToClose.Count > 0 && _state == ConnectionState.Open)
                {
                    string statement = _preparedStatementsToClose[0];
                    _preparedStatementsToClose.RemoveAt(0);
                    await CloseServerStatementCoreAsync(statement, CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                if (_state == ConnectionState.Open)
                {
                    ReaderActive = false;
                }
            }
        }

        internal void AbortTransport()
        {
            lock (_abortGate)
            {
                ActiveTransaction?.ConnectionClosed();
                ReaderActive = false;
                _stream?.Dispose();
                _client?.Dispose();
                _stream = null;
                _client = null;
                if (_pool is not null && _ownsPoolSlot)
                {
                    _pool.ReleaseReservedSlot();
                }

                _pool = null;
                _poolKey = null;
                _ownsPoolSlot = false;
                _preparedStatementsToClose.Clear();
            }

            SetState(ConnectionState.Closed);
        }

        /// <summary>Requests server-side cancellation and aborts the session if the server does not finish the statement.</summary>
        internal void RequestCancel()
        {
            if (!ReaderActive)
            {
                return;
            }

            long serial = Interlocked.Read(ref _operationSerial);
            bool sent = CancelCurrentQueryAsync().GetAwaiter().GetResult();
            if (!sent)
            {
                if (Interlocked.Read(ref _operationSerial) == serial && ReaderActive)
                {
                    AbortTransport();
                }

                return;
            }

            _ = AbortIfStillRunningAsync(serial);
        }

        private async Task AbortIfStillRunningAsync(long serial)
        {
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (ReaderActive && Interlocked.Read(ref _operationSerial) == serial)
            {
                AbortTransport();
            }
        }

        internal async Task<bool> CancelCurrentQueryAsync()
        {
            if (!ReaderActive || _backendProcessId == 0 || _backendSecretKey == 0)
            {
                return false;
            }

            CassieConnectionStringBuilder settings = new(_connectionString);
            byte[] packet = new byte[16];
            BinaryPrimitives.WriteInt32BigEndian(packet, 16);
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4), 80877102);
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8), _backendProcessId);
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(12), _backendSecretKey);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(Math.Clamp(settings.Timeout, 1, 5)));
            try
            {
                using TcpClient cancelClient = new() { NoDelay = true };
                await cancelClient.ConnectAsync(settings.Host, settings.Port, timeout.Token).ConfigureAwait(false);
                Stream cancelStream = cancelClient.GetStream();
                if (settings.SslMode != CassieSslMode.Disable)
                {
                    cancelStream = await NegotiateTlsCoreAsync(cancelStream, settings, timeout.Token).ConfigureAwait(false);
                }

                await using (cancelStream.ConfigureAwait(false))
                {
                    await cancelStream.WriteAsync(packet, timeout.Token).ConfigureAwait(false);
                    await cancelStream.FlushAsync(timeout.Token).ConfigureAwait(false);
                }

                return true;
            }
            catch (Exception exception) when (exception is SocketException or IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException or System.Security.Authentication.AuthenticationException)
            {
                return false;
            }
        }

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

        internal async Task ExecuteControlQueryAsync(string sql, CancellationToken cancellationToken)
        {
            CassieDataReader reader = await ExecuteQueryAsync(sql, cancellationToken).ConfigureAwait(false);
            try
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { }
            }
            finally
            {
                await reader.DisposeAsync().ConfigureAwait(false);
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
            return BeginTransactionCoreAsync(isolationLevel, CancellationToken.None).GetAwaiter().GetResult();
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

        [SuppressMessage("Reliability", "CA1031", Justification = "Any rollback failure leaves the server transaction state unknown, so abort the session and preserve the original cancellation.")]
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
            catch
            {
                AbortTransport();
            }
        }

        private async Task DisposeTransportAsync()
        {
            if (_stream is not null)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }

            _client?.Dispose();
            _stream = null;
            _client = null;
        }

        private static void WriteCString(Stream stream, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            stream.Write(bytes);
            stream.WriteByte(0);
        }

        private static CassieException ParseServerError(byte[] payload)
        {
            Dictionary<byte, string> fields = [];
            int offset = 0;
            while (offset < payload.Length && payload[offset] != 0)
            {
                byte field = payload[offset++];
                int end = Array.IndexOf(payload, (byte)0, offset);
                if (end < 0)
                {
                    break;
                }

                string value = Encoding.UTF8.GetString(payload, offset, end - offset);
                fields[field] = value;
                offset = end + 1;
            }

            return new CassieException(
                fields.GetValueOrDefault((byte)'M') ?? "Cassie returned an error during connection startup.",
                fields.GetValueOrDefault((byte)'C'),
                severity: fields.GetValueOrDefault((byte)'V') ?? fields.GetValueOrDefault((byte)'S'),
                detail: fields.GetValueOrDefault((byte)'D'),
                hint: fields.GetValueOrDefault((byte)'H'),
                position: fields.GetValueOrDefault((byte)'P'),
                schemaName: fields.GetValueOrDefault((byte)'s'),
                tableName: fields.GetValueOrDefault((byte)'t'),
                columnName: fields.GetValueOrDefault((byte)'c'),
                constraintName: fields.GetValueOrDefault((byte)'n'),
                file: fields.GetValueOrDefault((byte)'F'),
                line: fields.GetValueOrDefault((byte)'L'),
                routine: fields.GetValueOrDefault((byte)'R'));
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
