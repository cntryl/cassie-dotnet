using System.Collections;
using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Net.Sockets;
using System.Diagnostics.CodeAnalysis;

namespace Cntryl.Cassie
{
    /// <summary>Streams text- or binary-format rows returned by a Cassie query.</summary>
    [SuppressMessage("Reliability", "CA2213", Justification = "The reader borrows its connection and stream; disposing either would close the owning connection.")]
    public sealed class CassieDataReader : DbDataReader, IDbColumnSchemaGenerator
    {
        private const string UnexpectedSuspendedPortalMessage = "Cassie returned an unexpected suspended portal response.";
        private readonly CassieConnection _connection;
        private readonly Stream _stream;
        private readonly List<CassieColumn> _columns = [];
        private object?[]? _currentRow;
        private bool _closed;
        private volatile bool _complete;
        private bool _resultEnded;
        private bool _hasCurrentResult;
        private bool _hasRows;
        private bool _hasRowsKnown;
        private bool _prefetchedRow;
        private int _recordsAffected = -1;
        private readonly Timer? _commandTimer;
        private readonly bool _closeConnectionOnClose;
        private readonly bool _sequentialAccess;
        private readonly bool _singleRow;
        private readonly bool _singleResult;
        private readonly CassieDiagnostics.OperationScope? _operationScope;
        private readonly Func<CancellationToken, Task>? _resumePortalAsync;
        private readonly Func<Task>? _sendExtendedSyncAsync;
        private readonly Lock _cancelRequestLock = new();
        private Task<bool>? _cancelRequestTask;
        private int _rowsReadCurrentResult;
        private int _lastSequentialOrdinal = -1;
        private int _operationFinished;
        private int _extendedSyncSent;
        private volatile bool _commandTimedOut;
        private object?[]? _converted;

        internal CassieDataReader(
            CassieConnection connection,
            Stream stream,
            int commandTimeout,
            CommandBehavior behavior,
            CassieDiagnostics.OperationScope? operationScope = null,
            Func<CancellationToken, Task>? resumePortalAsync = null,
            Func<Task>? sendExtendedSyncAsync = null)
        {
            _connection = connection;
            _stream = stream;
            _operationScope = operationScope;
            _resumePortalAsync = resumePortalAsync;
            _sendExtendedSyncAsync = sendExtendedSyncAsync;
            _closeConnectionOnClose = (behavior & CommandBehavior.CloseConnection) != 0;
            _sequentialAccess = (behavior & CommandBehavior.SequentialAccess) != 0;
            _singleRow = (behavior & CommandBehavior.SingleRow) != 0;
            _singleResult = (behavior & CommandBehavior.SingleResult) != 0;
            if (commandTimeout > 0)
            {
                _commandTimer = new Timer(static state => ((CassieDataReader)state!).CancelForTimeout(), this, TimeSpan.FromSeconds(commandTimeout), Timeout.InfiniteTimeSpan);
            }
        }

        /// <inheritdoc />
        public override int Depth => 0;
        /// <inheritdoc />
        public override int FieldCount => _columns.Count;
        /// <inheritdoc />
        public override bool HasRows
        {
            get
            {
                EnsureOpen();
                if (_hasRowsKnown)
                {
                    return _hasRows;
                }

                if (_columns.Count == 0)
                {
                    _hasRowsKnown = true;
                    return false;
                }

                bool hasRow = ReadAsync(CancellationToken.None).GetAwaiter().GetResult();
                if (hasRow)
                {
                    _prefetchedRow = true;
                }

                return hasRow;
            }
        }
        /// <inheritdoc />
        public override bool IsClosed => _closed;
        /// <inheritdoc />
        public override int RecordsAffected => _recordsAffected;
        /// <inheritdoc />
        public override object this[int ordinal] => GetValue(ordinal);
        /// <inheritdoc />
        public override object this[string name] => GetValue(GetOrdinal(name));

        private bool IsTimeoutTransportFailure(Exception exception)
        {
            return _commandTimedOut && exception is IOException or SocketException or ObjectDisposedException;
        }

        private static TimeoutException CreateTimeoutException(Exception inner)
        {
            return new TimeoutException("The command exceeded CommandTimeout and its cancellation could not be confirmed.", inner);
        }

        internal async Task InitializeAsync(CancellationToken cancellationToken)
        {
            try
            {
                await InitializeCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTimeoutTransportFailure(exception))
            {
                _connection.AbortTransport();
                throw CreateTimeoutException(exception);
            }
        }

        private async Task InitializeCoreAsync(CancellationToken cancellationToken)
        {
            while (_columns.Count == 0 && !_complete)
            {
                (byte tag, byte[] payload) = await ReadQueryFrameAsync(cancellationToken).ConfigureAwait(false);
                if (tag == (byte)'T')
                {
                    ParseRowDescription(payload);
                    _hasCurrentResult = true;
                }
                else if (tag == (byte)'C')
                {
                    ParseCommandComplete(payload);
                }
                else if (tag == (byte)'S')
                {
                    ParseParameterStatus(payload);
                }
                else if (tag == (byte)'E')
                {
                    CassieException error = ParseError(payload);
                    MarkOperationFailed(error);
                    await DrainToReadyAsync(cancellationToken).ConfigureAwait(false);
                    if (error.SqlState == "57014" && _commandTimedOut)
                    {
                        throw new TimeoutException("The command exceeded CommandTimeout and was cancelled by Cassie.");
                    }

                    if (error.SqlState == "57014" && cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException("The query was canceled.", error, cancellationToken);
                    }

                    throw error;
                }
                else if (tag == (byte)'G')
                {
                    await _connection.RejectCopyFromAsync().ConfigureAwait(false);
                }
                else if (tag == (byte)'s')
                {
                    throw new IOException(UnexpectedSuspendedPortalMessage);
                }
                else if (tag == (byte)'Z') { await MarkReadyAsync(payload).ConfigureAwait(false); }
            }
        }

        /// <inheritdoc />
        public override bool Read()
        {
            return ReadAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <inheritdoc />
        public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
        {
            EnsureOpen();
            try
            {
                if (_prefetchedRow)
                {
                    _prefetchedRow = false;
                    return true;
                }

                if (_complete || _resultEnded)
                {
                    return false;
                }

                _currentRow = null;
                if (_singleRow && _rowsReadCurrentResult > 0)
                {
                    await DrainCurrentResultAsync(cancellationToken).ConfigureAwait(false);
                    return false;
                }

                while (!_complete)
                {
                    (byte tag, byte[] payload) = await ReadQueryFrameAsync(cancellationToken).ConfigureAwait(false);
                    switch (tag)
                    {
                        case (byte)'T': ParseRowDescription(payload); _hasCurrentResult = true; break;
                        case (byte)'D':
                            _currentRow = ParseDataRow(payload);
                            if (_converted is not null)
                            {
                                Array.Clear(_converted);
                            }

                            _lastSequentialOrdinal = -1;
                            _rowsReadCurrentResult++;
                            _hasRows = true;
                            _hasRowsKnown = true;
                            return true;
                        case (byte)'S': ParseParameterStatus(payload); break;
                        case (byte)'C': ParseCommandComplete(payload); _hasRowsKnown = true; _resultEnded = true; return false;
                        case (byte)'I': _columns.Clear(); _hasRows = false; _hasRowsKnown = true; _resultEnded = true; return false;
                        case (byte)'E':
                            CassieException error = ParseError(payload);
                            MarkOperationFailed(error);
                            await DrainToReadyAsync(cancellationToken).ConfigureAwait(false);
                            if (_commandTimedOut && error.SqlState == "57014")
                            {
                                throw new TimeoutException("The command exceeded CommandTimeout and was cancelled by Cassie.");
                            }

                            if (error.SqlState == "57014" && cancellationToken.IsCancellationRequested)
                            {
                                throw new OperationCanceledException("The query was canceled.", error, cancellationToken);
                            }

                            throw error;
                        case (byte)'Z': await MarkReadyAsync(payload).ConfigureAwait(false); break;
                        case (byte)'G': await _connection.RejectCopyFromAsync().ConfigureAwait(false); break;
                        case (byte)'s': throw new IOException(UnexpectedSuspendedPortalMessage);
                        default:
                            break;
                    }
                }

                return false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !_complete)
            {
                MarkOperationFailed(new OperationCanceledException("The query read was canceled.", cancellationToken));
                await CancelAndDrainAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception exception) when (IsTimeoutTransportFailure(exception))
            {
                _connection.AbortTransport();
                TimeoutException timeout = CreateTimeoutException(exception);
                FinishOperation(timeout);
                throw timeout;
            }
            catch (Exception exception)
            {
                FinishOperation(exception);
                throw;
            }
        }

        internal async Task CancelAndDrainAsync()
        {
            MarkOperationFailed(new OperationCanceledException("The query was canceled."));
            if (_complete || _connection.State != ConnectionState.Open)
            {
                FinishOperation();
                return;
            }

            try
            {
                if (!await GetOrStartCancelRequest().ConfigureAwait(false))
                {
                    _connection.AbortTransport();
                    FinishOperation();
                    return;
                }

                await EnsureExtendedSyncSentAsync().ConfigureAwait(false);
                using CancellationTokenSource drainTimeout = new(TimeSpan.FromSeconds(5));
                await DrainToReadyAsync(drainTimeout.Token, allowAbortOnCancellation: true).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _connection.AbortTransport();
                FinishOperation(exception);
                throw;
            }
        }

        /// <inheritdoc />
        public override bool NextResult()
        {
            return NextResultAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <inheritdoc />
        public override async Task<bool> NextResultAsync(CancellationToken cancellationToken)
        {
            EnsureOpen();
            try
            {
                if (_complete)
                {
                    return false;
                }

                if (_singleResult)
                {
                    await DrainToReadyAsync(cancellationToken, propagateServerError: true).ConfigureAwait(false);
                    _columns.Clear();
                    _currentRow = null;
                    _resultEnded = false;
                    _hasCurrentResult = false;
                    _hasRows = false;
                    _hasRowsKnown = true;
                    _prefetchedRow = false;
                    _rowsReadCurrentResult = 0;
                    return false;
                }

                if (!_resultEnded && _hasCurrentResult)
                {
                    while (await ReadAsync(cancellationToken).ConfigureAwait(false)) { }
                }

                _columns.Clear();
                _currentRow = null;
                _resultEnded = false;
                _hasCurrentResult = false;
                _hasRows = false;
                _hasRowsKnown = false;
                _prefetchedRow = false;
                _rowsReadCurrentResult = 0;
                while (!_complete)
                {
                    (byte tag, byte[] payload) = await ReadQueryFrameAsync(cancellationToken).ConfigureAwait(false);
                    switch (tag)
                    {
                        case (byte)'T':
                            ParseRowDescription(payload);
                            _hasCurrentResult = true;
                            return true;
                        case (byte)'S': ParseParameterStatus(payload); break;
                        case (byte)'C': ParseCommandComplete(payload); break;
                        case (byte)'I': break;
                        case (byte)'E':
                            CassieException error = ParseError(payload);
                            MarkOperationFailed(error);
                            await DrainToReadyAsync(cancellationToken).ConfigureAwait(false);
                            if (_commandTimedOut && error.SqlState == "57014")
                            {
                                throw new TimeoutException("The command exceeded CommandTimeout and was cancelled by Cassie.");
                            }

                            if (error.SqlState == "57014" && cancellationToken.IsCancellationRequested)
                            {
                                throw new OperationCanceledException("The query was canceled.", error, cancellationToken);
                            }

                            throw error;
                        case (byte)'G': await _connection.RejectCopyFromAsync().ConfigureAwait(false); break;
                        case (byte)'s': throw new IOException(UnexpectedSuspendedPortalMessage);
                        case (byte)'Z': await MarkReadyAsync(payload).ConfigureAwait(false); break;
                    }
                }

                return false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !_complete)
            {
                MarkOperationFailed(new OperationCanceledException("The query read was canceled.", cancellationToken));
                await CancelAndDrainAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception exception) when (IsTimeoutTransportFailure(exception))
            {
                _connection.AbortTransport();
                TimeoutException timeout = CreateTimeoutException(exception);
                FinishOperation(timeout);
                throw timeout;
            }
            catch (Exception exception)
            {
                FinishOperation(exception);
                throw;
            }
        }

        /// <inheritdoc />
        public override string GetName(int ordinal)
        {
            return GetColumn(ordinal).Name;
        }

        /// <inheritdoc />
        public override int GetOrdinal(string name)
        {
            int exactIndex = _columns.FindIndex(column => string.Equals(column.Name, name, StringComparison.Ordinal));
            if (exactIndex >= 0)
            {
                return exactIndex;
            }

            int caseInsensitiveIndex = _columns.FindIndex(column => string.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase));
            return caseInsensitiveIndex >= 0
                ? caseInsensitiveIndex
                : throw new ArgumentException($"Column '{name}' was not found.", nameof(name));
        }

        /// <inheritdoc />
        [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)]
        public override Type GetFieldType(int ordinal)
        {
            return GetColumn(ordinal).FieldType;
        }

        /// <inheritdoc />
        public override string GetDataTypeName(int ordinal)
        {
            return CassieTypeMap.GetDataTypeName(GetColumn(ordinal).TypeOid);
        }

        /// <inheritdoc />
        public override object GetValue(int ordinal)
        {
            object? raw = GetRowValue(ordinal);
            if (raw is null)
            {
                return DBNull.Value;
            }

            object?[] cache = _converted ??= new object?[_columns.Count];
            if (cache[ordinal] is { } cached)
            {
                return cached;
            }

            object value = raw switch
            {
                string text => CassieResultCodec.ConvertValue(text, GetColumn(ordinal).TypeOid, _connection.DateStyle),
                byte[] bytes => CassieResultCodec.ConvertBinaryValue(bytes, GetColumn(ordinal).TypeOid),
                _ => raw
            };
            cache[ordinal] = value;
            return value;
        }

        /// <inheritdoc />
        public override int GetValues(object[] values)
        {
            ArgumentNullException.ThrowIfNull(values);
            int count = Math.Min(values.Length, FieldCount);
            for (int i = 0; i < count; i++)
            {
                values[i] = GetValue(i);
            }

            return count;
        }

        /// <inheritdoc />
        public override T GetFieldValue<T>(int ordinal)
        {
            return typeof(T) == typeof(decimal)
                ? (T)(object)GetDecimal(ordinal)
                : typeof(T) == typeof(CassieTemporalValue)
                    ? (T)(object)GetTemporalValue(ordinal)
                : typeof(T) == typeof(DateTime)
                    ? (T)(object)GetDateTime(ordinal)
                    : typeof(T) == typeof(TimeSpan)
                        ? (T)(object)GetTimeSpanValue(ordinal)
                        : typeof(T) == typeof(ulong)
                            ? (T)(object)GetUInt64Value(ordinal)
                            : base.GetFieldValue<T>(ordinal);
        }

        /// <inheritdoc />
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
        {
            object value = GetValue(ordinal);
            return value is not byte[] bytes
                ? throw new InvalidCastException("The column is not bytea.")
                : CopyFieldData(bytes, dataOffset, buffer, bufferOffset, length);
        }

        /// <inheritdoc />
        public override Stream GetStream(int ordinal)
        {
            object value = GetValue(ordinal);
            return value is byte[] bytes
                ? new MemoryStream(bytes, writable: false)
                : throw new InvalidCastException("The column does not contain binary data.");
        }

        /// <inheritdoc />
        public override TextReader GetTextReader(int ordinal)
        {
            object value = GetValue(ordinal);
            return value is string text
                ? new StringReader(text)
                : throw new InvalidCastException("The column does not contain text data.");
        }

        /// <inheritdoc />
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
        {
            return CopyFieldData(GetString(ordinal).ToCharArray(), dataOffset, buffer, bufferOffset, length);
        }

        /// <inheritdoc />
        public override bool IsDBNull(int ordinal)
        {
            return GetRowValue(ordinal) is null;
        }

        /// <inheritdoc />
        public override bool GetBoolean(int ordinal)
        {
            return Convert.ToBoolean(GetValue(ordinal), CultureInfo.InvariantCulture);
        }

        /// <inheritdoc />
        public override byte GetByte(int ordinal)
        {
            return Convert.ToByte(GetValue(ordinal), CultureInfo.InvariantCulture);
        }

        /// <inheritdoc />
        public override char GetChar(int ordinal)
        {
            return Convert.ToChar(GetValue(ordinal), CultureInfo.InvariantCulture);
        }

        /// <inheritdoc />
        public override DateTime GetDateTime(int ordinal)
        {
            return GetValue(ordinal) switch
            {
                DateOnly date => date.ToDateTime(TimeOnly.MinValue),
                DateTime dateTime => dateTime,
                DateTimeOffset dateTimeOffset => dateTimeOffset.UtcDateTime,
                string text => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DBNull => throw new InvalidCastException("The column is NULL."),
                _ => throw new InvalidCastException("The column does not contain a date or timestamp value.")
            };
        }

        /// <inheritdoc />
        public override decimal GetDecimal(int ordinal)
        {
            return GetValue(ordinal) switch
            {
                CassieNumeric numeric => numeric.ToDecimal(),
                decimal value => value,
                _ => decimal.Parse(GetRequiredString(ordinal), CultureInfo.InvariantCulture)
            };
        }

        /// <inheritdoc />
        public override double GetDouble(int ordinal)
        {
            return GetValue(ordinal) switch
            {
                double value => value,
                float value => value,
                _ => double.Parse(GetRequiredString(ordinal), CultureInfo.InvariantCulture)
            };
        }

        /// <inheritdoc />
        public override float GetFloat(int ordinal)
        {
            return GetValue(ordinal) switch
            {
                float value => value,
                _ => float.Parse(GetRequiredString(ordinal), CultureInfo.InvariantCulture)
            };
        }

        /// <inheritdoc />
        public override Guid GetGuid(int ordinal)
        {
            return GetValue(ordinal) switch
            {
                Guid value => value,
                _ => Guid.Parse(GetRequiredString(ordinal))
            };
        }

        /// <inheritdoc />
        public override short GetInt16(int ordinal)
        {
            return GetValue(ordinal) switch
            {
                short value => value,
                _ => short.Parse(GetRequiredString(ordinal), CultureInfo.InvariantCulture)
            };
        }

        /// <inheritdoc />
        public override int GetInt32(int ordinal)
        {
            return GetValue(ordinal) switch
            {
                int value => value,
                short value => value,
                _ => int.Parse(GetRequiredString(ordinal), CultureInfo.InvariantCulture)
            };
        }

        /// <inheritdoc />
        public override long GetInt64(int ordinal)
        {
            return GetValue(ordinal) switch
            {
                long value => value,
                int value => value,
                short value => value,
                _ => long.Parse(GetRequiredString(ordinal), CultureInfo.InvariantCulture)
            };
        }

        /// <inheritdoc />
        public override string GetString(int ordinal)
        {
            return GetRequiredString(ordinal);
        }

        /// <inheritdoc />
        public override IEnumerator GetEnumerator()
        {
            return new DbEnumerator(this, closeReader: false);
        }

        /// <inheritdoc />
        public override void Close()
        {
            CloseAsync().GetAwaiter().GetResult();
        }

        /// <inheritdoc />
        [SuppressMessage("Reliability", "CA2213", Justification = "The reader borrows its connection and stream; disposing either would close the owning connection.")]
        public override async Task CloseAsync()
        {
            if (_closed)
            {
                return;
            }

            try
            {
                if (!_complete)
                {
                    while (!_complete)
                    {
                        while (await ReadAsync(CancellationToken.None).ConfigureAwait(false)) { }
                        if (!_complete && !await NextResultAsync(CancellationToken.None).ConfigureAwait(false))
                        {
                            break;
                        }
                    }
                }
            }
            catch (IOException exception)
            {
                MarkOperationFailed(exception);
                _connection.AbortTransport();
            }
            catch (CassieException exception)
            {
                MarkOperationFailed(exception);
                // Closing a reader discards any unread server error after the
                // protocol has been drained back to ReadyForQuery.
            }
            catch (Exception exception)
            {
                FinishOperation(exception);
                throw;
            }
            finally
            {
                if (_commandTimer is not null)
                {
                    await _commandTimer.DisposeAsync().ConfigureAwait(false);
                }

                _closed = true;
                FinishOperation();
            }

            if (_closeConnectionOnClose)
            {
                await _connection.CloseAsync().ConfigureAwait(false);
            }
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
        [UnconditionalSuppressMessage("Trimming", "IL2111", Justification = "The returned schema table reports Type metadata from the fixed built-in OID map, whose field types are rooted by the OID mapping.")]
        public override DataTable GetSchemaTable()
        {
            DataTable table = new();
            _ = table.Columns.Add("ColumnName", typeof(string));
            _ = table.Columns.Add("ColumnOrdinal", typeof(int));
            _ = table.Columns.Add("ColumnSize", typeof(int));
            _ = table.Columns.Add("NumericPrecision", typeof(short));
            _ = table.Columns.Add("NumericScale", typeof(short));
            _ = table.Columns.Add("IsUnique", typeof(bool));
            _ = table.Columns.Add("IsKey", typeof(bool));
            _ = table.Columns.Add("BaseServerName", typeof(string));
            _ = table.Columns.Add("BaseCatalogName", typeof(string));
            _ = table.Columns.Add("BaseColumnName", typeof(string));
            _ = table.Columns.Add("BaseSchemaName", typeof(string));
            _ = table.Columns.Add("BaseTableName", typeof(string));
            _ = table.Columns.Add("DataType", typeof(Type));
            _ = table.Columns.Add("AllowDBNull", typeof(bool));
            _ = table.Columns.Add("ProviderType", typeof(int));
            _ = table.Columns.Add("IsAliased", typeof(bool));
            _ = table.Columns.Add("IsExpression", typeof(bool));
            _ = table.Columns.Add("IsIdentity", typeof(bool));
            _ = table.Columns.Add("IsAutoIncrement", typeof(bool));
            _ = table.Columns.Add("IsRowVersion", typeof(bool));
            _ = table.Columns.Add("IsHidden", typeof(bool));
            _ = table.Columns.Add("IsLong", typeof(bool));
            _ = table.Columns.Add("IsReadOnly", typeof(bool));
            _ = table.Columns.Add("ProviderSpecificDataType", typeof(Type));
            _ = table.Columns.Add("DataTypeName", typeof(string));
            for (int i = 0; i < _columns.Count; i++)
            {
                CassieColumn column = _columns[i];
                _ = table.Rows.Add(
                    column.Name,
                    i,
                    GetColumnSize(column),
                    GetNumericPrecision(column),
                    GetNumericScale(column),
                    DBNull.Value,
                    DBNull.Value,
                    DBNull.Value,
                    DBNull.Value,
                    DBNull.Value,
                    DBNull.Value,
                    DBNull.Value,
                    column.FieldType,
                    DBNull.Value,
                    column.TypeOid,
                    DBNull.Value,
                    DBNull.Value,
                    DBNull.Value,
                    DBNull.Value,
                    DBNull.Value,
                    false,
                    DBNull.Value,
                    DBNull.Value,
                    column.FieldType,
                    CassieTypeMap.GetDataTypeName(column.TypeOid));
            }

            return table;
        }

        /// <summary>Gets column metadata while preserving unknown origin and constraint values.</summary>
        public ReadOnlyCollection<DbColumn> GetColumnSchema()
        {
            List<DbColumn> columns = new(_columns.Count);
            for (int ordinal = 0; ordinal < _columns.Count; ordinal++)
            {
                columns.Add(new CassieDbColumn(_columns[ordinal], ordinal));
            }

            return columns.AsReadOnly();
        }

        private CassieColumn GetColumn(int ordinal)
        {
            return (uint)ordinal < (uint)_columns.Count ? _columns[ordinal] : throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "Column ordinal is out of range.");
        }

        private object? GetRowValue(int ordinal)
        {
            EnsureOpen();
            _ = GetColumn(ordinal);
            if (_sequentialAccess && ordinal < _lastSequentialOrdinal)
            {
                throw new InvalidOperationException("Columns must be read in column order when SequentialAccess is enabled.");
            }

            _lastSequentialOrdinal = Math.Max(_lastSequentialOrdinal, ordinal);
            return _currentRow is null ? throw new InvalidOperationException("The reader is not positioned on a row.") : _currentRow[ordinal];
        }

        private string GetRequiredString(int ordinal)
        {
            object value = GetValue(ordinal);
            return value switch
            {
                DBNull => throw new InvalidCastException("The column is NULL."),
                byte[] bytes => "\\x" + Convert.ToHexString(bytes),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
            };
        }

        private TimeSpan GetTimeSpanValue(int ordinal)
        {
            return GetValue(ordinal) switch
            {
                TimeOnly time => time.ToTimeSpan(),
                TimeSpan time => time,
                CassieTemporalValue { Kind: CassieTemporalKind.Time, HasRawValue: true } time => TimeSpan.FromTicks(checked(time.Value * 10)),
                _ => throw new InvalidCastException("The column is not a TIME value.")
            };
        }

        private CassieTemporalValue GetTemporalValue(int ordinal)
        {
            object value = GetValue(ordinal);
            return value switch
            {
                CassieTemporalValue temporal => temporal,
                DateOnly date => CassieTemporalValue.FromDateDays(date.DayNumber - new DateOnly(2000, 1, 1).DayNumber),
                TimeOnly time => CassieTemporalValue.FromTimeMicroseconds(time.Ticks / 10),
                CassieTimeWithTimeZone timeWithTimeZone => CassieTemporalValue.FromTimeWithTimeZoneMicroseconds(timeWithTimeZone.Time.Ticks / 10, timeWithTimeZone.OffsetSeconds),
                DateTime timestamp => CassieTemporalValue.FromTimestampMicroseconds((timestamp.Ticks - new DateTime(2000, 1, 1).Ticks) / 10),
                DateTimeOffset timestampWithTimeZone => CassieTemporalValue.FromTimestampWithTimeZoneMicroseconds((timestampWithTimeZone.UtcTicks - new DateTime(2000, 1, 1).Ticks) / 10),
                DBNull => throw new InvalidCastException("The column is NULL."),
                _ => throw new InvalidCastException("The column does not contain a temporal value.")
            };
        }

        private ulong GetUInt64Value(int ordinal)
        {
            return GetValue(ordinal) switch
            {
                uint objectIdentifier => objectIdentifier,
                CassieNumeric numeric => ConvertNumericToUInt64(numeric),
                _ => throw new InvalidCastException("The column cannot be read as UInt64.")
            };
        }

        private static ulong ConvertNumericToUInt64(CassieNumeric numeric)
        {
            string rawValue = numeric.Value ?? string.Empty;
            ReadOnlySpan<char> value = rawValue.AsSpan();
            if (value.IsEmpty || value.SequenceEqual("NaN") || value.SequenceEqual("Infinity")
                || value.SequenceEqual("+Infinity") || value.SequenceEqual("-Infinity"))
            {
                throw new InvalidCastException("The NUMERIC value is not a finite UInt64.");
            }

            int signLength = value[0] is '+' or '-' ? 1 : 0;
            bool negative = signLength == 1 && value[0] == '-';
            int exponentOffset = value[signLength..].IndexOfAny(['e', 'E']);
            int exponentIndex = exponentOffset < 0 ? -1 : signLength + exponentOffset;
            ReadOnlySpan<char> mantissa = exponentIndex < 0 ? value[signLength..] : value[signLength..exponentIndex];
            ReadOnlySpan<char> exponentText = exponentIndex < 0 ? [] : value[(exponentIndex + 1)..];
            int exponent = 0;
            if (exponentIndex >= 0 && !int.TryParse(exponentText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
            {
                throw new OverflowException("The NUMERIC exponent is outside the UInt64 conversion range.");
            }

            int point = mantissa.IndexOf('.');
            ReadOnlySpan<char> integerPart = point < 0 ? mantissa : mantissa[..point];
            ReadOnlySpan<char> fractionalPart = point < 0 ? [] : mantissa[(point + 1)..];
            string digits = string.Concat(integerPart, fractionalPart);
            if (digits.Length == 0)
            {
                throw new InvalidCastException("The NUMERIC value is not a UInt64.");
            }

            if (!ContainsNonZeroDigit(digits.AsSpan()))
            {
                return 0;
            }

            if (negative)
            {
                throw new InvalidCastException("A negative NUMERIC value cannot be read as UInt64.");
            }

            long power = (long)exponent - fractionalPart.Length;
            if (power < 0)
            {
                long fractionalDigitCount = -power;
                if (fractionalDigitCount > digits.Length)
                {
                    throw new InvalidCastException("A fractional NUMERIC value cannot be read as UInt64.");
                }

                int integerDigitCount = digits.Length - (int)fractionalDigitCount;
                if (ContainsNonZeroDigit(digits.AsSpan(integerDigitCount)))
                {
                    throw new InvalidCastException("A fractional NUMERIC value cannot be read as UInt64.");
                }

                digits = digits[..integerDigitCount];
            }
            else
            {
                digits = digits.TrimStart('0');
                if (digits.Length == 0)
                {
                    return 0;
                }

                if (power + digits.Length > 20)
                {
                    throw new OverflowException("The NUMERIC value exceeds the UInt64 range.");
                }

                digits += new string('0', (int)power);
            }

            digits = digits.TrimStart('0');
            return digits.Length <= 20 && ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out ulong result)
                ? result
                : throw new OverflowException("The NUMERIC value exceeds the UInt64 range.");
        }

        private static bool ContainsNonZeroDigit(ReadOnlySpan<char> value)
        {
            foreach (char digit in value)
            {
                if (digit != '0')
                {
                    return true;
                }
            }

            return false;
        }

        private void EnsureOpen()
        {
            if (_closed)
            {
                throw new InvalidOperationException("The reader is closed.");
            }
        }

        private void MarkOperationFailed(Exception exception)
        {
            _operationScope?.Fail(exception, _connection.Logger);
        }

        private void FinishOperation(Exception? exception = null)
        {
            if (exception is not null)
            {
                MarkOperationFailed(exception);
            }

            if (Interlocked.Exchange(ref _operationFinished, 1) == 0)
            {
                _operationScope?.Dispose();
            }
        }

        private async Task DrainToReadyAsync(CancellationToken cancellationToken, bool allowAbortOnCancellation = false, bool propagateServerError = false)
        {
            CassieException? serverError = null;
            while (!_complete)
            {
                (byte tag, byte[] payload) = await ReadQueryFrameAsync(cancellationToken, allowAbortOnCancellation).ConfigureAwait(false);
                if (tag == (byte)'E')
                {
                    serverError = ParseError(payload);
                    MarkOperationFailed(serverError);
                }
                else if (tag == (byte)'S')
                {
                    ParseParameterStatus(payload);
                }
                else if (tag == (byte)'s')
                {
                    throw new IOException(UnexpectedSuspendedPortalMessage);
                }

                if (tag == (byte)'Z')
                {
                    await MarkReadyAsync(payload).ConfigureAwait(false);
                }
            }

            if (propagateServerError && serverError is not null)
            {
                if (_commandTimedOut && serverError.SqlState == "57014")
                {
                    throw new TimeoutException("The command exceeded CommandTimeout and was cancelled by Cassie.");
                }

                if (cancellationToken.IsCancellationRequested && serverError.SqlState == "57014")
                {
                    throw new OperationCanceledException("The query was canceled.", serverError, cancellationToken);
                }

                throw serverError;
            }
        }

        private Task<(byte Tag, byte[] Payload)> ReadQueryFrameAsync(CancellationToken cancellationToken, bool allowAbortOnCancellation = false)
        {
            return _resumePortalAsync is null && _sendExtendedSyncAsync is null
                ? ReadQueryFrameCoreAsync(cancellationToken, allowAbortOnCancellation)
                : ReadQueryFrameWithProtocolHandlingAsync(allowAbortOnCancellation, cancellationToken);
        }

        private async Task<(byte Tag, byte[] Payload)> ReadQueryFrameWithProtocolHandlingAsync(bool allowAbortOnCancellation, CancellationToken cancellationToken)
        {
            while (true)
            {
                (byte tag, byte[] payload) = await ReadQueryFrameCoreAsync(cancellationToken, allowAbortOnCancellation).ConfigureAwait(false);
                if (tag == (byte)'s')
                {
                    if (_resumePortalAsync is null || payload.Length != 0)
                    {
                        throw new IOException(UnexpectedSuspendedPortalMessage);
                    }

                    await _resumePortalAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (tag is (byte)'C' or (byte)'I' or (byte)'E')
                {
                    await EnsureExtendedSyncSentAsync().ConfigureAwait(false);
                }

                return (tag, payload);
            }
        }

        private async Task EnsureExtendedSyncSentAsync()
        {
            if (_sendExtendedSyncAsync is null || Interlocked.Exchange(ref _extendedSyncSent, 1) != 0)
            {
                return;
            }

            await _sendExtendedSyncAsync().ConfigureAwait(false);
        }

        private Task<(byte Tag, byte[] Payload)> ReadQueryFrameCoreAsync(CancellationToken cancellationToken, bool allowAbortOnCancellation = false)
        {
            return allowAbortOnCancellation || !cancellationToken.CanBeCanceled
                ? _connection.ReadQueryFrameAsync(_stream, cancellationToken)
                : ReadQueryFrameWithCancellationAsync(cancellationToken);
        }

        private async Task<(byte Tag, byte[] Payload)> ReadQueryFrameWithCancellationAsync(CancellationToken cancellationToken)
        {
            using CancellationTokenSource abortDelay = new();
            using CancellationTokenRegistration cancellationRegistration = cancellationToken.UnsafeRegister(static state =>
            {
                (CassieDataReader reader, CancellationTokenSource delay) = ((CassieDataReader Reader, CancellationTokenSource AbortDelay))state!;
                reader.StartCancelRequest();
                _ = reader.AbortAfterCancellationGracePeriodAsync(delay.Token);
            }, (this, abortDelay));

            try
            {
                // Finish a frame once reading starts so cancellation cannot leave the stream mid-frame.
                return await _connection.ReadQueryFrameAsync(_stream, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when ((cancellationToken.IsCancellationRequested || _commandTimedOut) && (exception is IOException or SocketException or ObjectDisposedException))
            {
                _connection.AbortTransport();
                if (_commandTimedOut && !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("The command exceeded CommandTimeout and its cancellation could not be confirmed.", exception);
                }

                throw new OperationCanceledException("The query was canceled.", exception, cancellationToken);
            }
            finally
            {
                await abortDelay.CancelAsync().ConfigureAwait(false);
            }
        }

        private Task<bool> GetOrStartCancelRequest()
        {
            lock (_cancelRequestLock)
            {
                return _cancelRequestTask ??= _connection.CancelCurrentQueryAsync();
            }
        }

        private void StartCancelRequest()
        {
            if (!_complete)
            {
                _ = ObserveCancelRequestAsync();
            }
        }

        private async Task ObserveCancelRequestAsync()
        {
            try
            {
                if (!await GetOrStartCancelRequest().ConfigureAwait(false) && !_complete)
                {
                    _connection.AbortTransport();
                }
            }
            catch (Exception exception) when (exception is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
            {
                if (!_complete)
                {
                    _connection.AbortTransport();
                }
            }
        }

        private async Task AbortAfterCancellationGracePeriodAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                if (!_complete)
                {
                    _connection.AbortTransport();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private void CancelForTimeout()
        {
            _commandTimedOut = true;
            _ = RequestTimeoutCancellationAsync();
            _ = AbortAfterTimeoutGracePeriodAsync();
        }

        private async Task RequestTimeoutCancellationAsync()
        {
            try
            {
                if (!await GetOrStartCancelRequest().ConfigureAwait(false) && !_complete)
                {
                    _connection.AbortTransport();
                }
            }
            catch (Exception exception) when (exception is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
            {
                if (!_complete)
                {
                    _connection.AbortTransport();
                }
            }
        }

        private async Task AbortAfterTimeoutGracePeriodAsync()
        {
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (!_complete)
            {
                _connection.AbortTransport();
            }
        }

        private async Task MarkReadyAsync(byte[] payload)
        {
            if (!_hasRowsKnown)
            {
                _hasRowsKnown = true;
            }

            _connection.HandleReadyForQuery(payload);
            _complete = true;
            if (_commandTimer is not null)
            {
                await _commandTimer.DisposeAsync().ConfigureAwait(false);
            }

            await _connection.ReaderClosedAsync().ConfigureAwait(false);
            FinishOperation();
        }

        private async Task DrainCurrentResultAsync(CancellationToken cancellationToken)
        {
            while (!_complete)
            {
                (byte tag, byte[] payload) = await ReadQueryFrameAsync(cancellationToken).ConfigureAwait(false);
                switch (tag)
                {
                    case (byte)'C':
                        ParseCommandComplete(payload);
                        _hasRowsKnown = true;
                        _resultEnded = true;
                        return;
                    case (byte)'S':
                        ParseParameterStatus(payload);
                        break;
                    case (byte)'I':
                        _hasRowsKnown = true;
                        _resultEnded = true;
                        return;
                    case (byte)'s':
                        throw new IOException(UnexpectedSuspendedPortalMessage);
                    case (byte)'E':
                        CassieException error = ParseError(payload);
                        MarkOperationFailed(error);
                        await DrainToReadyAsync(cancellationToken).ConfigureAwait(false);
                        if (_commandTimedOut && error.SqlState == "57014")
                        {
                            throw new TimeoutException("The command exceeded CommandTimeout and was cancelled by Cassie.");
                        }

                        if (error.SqlState == "57014" && cancellationToken.IsCancellationRequested)
                        {
                            throw new OperationCanceledException("The query was canceled.", error, cancellationToken);
                        }

                        throw error;
                    case (byte)'G':
                        await _connection.RejectCopyFromAsync().ConfigureAwait(false);
                        break;
                    case (byte)'Z':
                        await MarkReadyAsync(payload).ConfigureAwait(false);
                        return;
                }
            }
        }

        private void ParseRowDescription(byte[] payload)
        {
            int offset = 0;
            short count = ReadInt16(payload, ref offset);
            if (count < 0)
            {
                throw new IOException("RowDescription contained an invalid negative column count.");
            }

            _columns.Clear();
            _converted = null;
            _currentRow = null;
            _hasRows = false;
            _hasRowsKnown = false;
            _prefetchedRow = false;
            _rowsReadCurrentResult = 0;
            for (int i = 0; i < count; i++)
            {
                string name = ReadCString(payload, ref offset);
                uint tableOid = unchecked((uint)ReadInt32(payload, ref offset));
                short attributeNumber = ReadInt16(payload, ref offset);
                int oid = ReadInt32(payload, ref offset);
                short typeSize = ReadInt16(payload, ref offset);
                int typeModifier = ReadInt32(payload, ref offset);
                short format = ReadInt16(payload, ref offset);
                if (format is not (0 or 1))
                {
                    throw new IOException($"RowDescription column '{name}' used unsupported format code {format}.");
                }

                _columns.Add(new CassieColumn(name, tableOid, attributeNumber, oid, typeSize, typeModifier, CassieResultCodec.GetClrType(oid, format), format));
            }

            if (offset != payload.Length)
            {
                throw new IOException("RowDescription contained trailing payload bytes.");
            }
        }

        private void ParseParameterStatus(byte[] payload)
        {
            _connection.HandleParameterStatusMessage(payload);
        }

        private static int GetColumnSize(CassieColumn column)
        {
            return column.TypeOid switch
            {
                1042 or 1043 when column.TypeModifier >= 4 => column.TypeModifier - 4,
                1700 when column.TypeModifier >= 4 => (column.TypeModifier - 4) >> 16,
                _ when CassieTypeMap.IsVectorOid(column.TypeOid) => CassieTypeMap.GetVectorDimension(column.TypeOid),
                _ => column.TypeSize
            };
        }

        private static object GetNumericPrecision(CassieColumn column)
        {
            int precision = column.TypeOid switch
            {
                20 => 64,
                21 => 16,
                23 or 26 => 32,
                700 => 24,
                701 => 53,
                1700 when column.TypeModifier >= 4 => (column.TypeModifier - 4) >> 16,
                _ => -1
            };
            return precision >= 0 ? checked((short)precision) : DBNull.Value;
        }

        private static object GetNumericScale(CassieColumn column)
        {
            if (column.TypeOid == 1700 && column.TypeModifier >= 4)
            {
                int encodedScale = (column.TypeModifier - 4) & 0x7ff;
                int scale = (encodedScale & 0x400) == 0 ? encodedScale : encodedScale | ~0x7ff;
                return checked((short)scale);
            }

            return column.TypeOid is 20 or 21 or 23 or 26 or 700 or 701 ? (short)0 : DBNull.Value;
        }

        private object?[] ParseDataRow(byte[] payload)
        {
            int offset = 0;
            short count = ReadInt16(payload, ref offset);
            if (count != _columns.Count)
            {
                throw new IOException("DataRow column count did not match RowDescription.");
            }

            object?[] row = new object?[count];
            for (int i = 0; i < count; i++)
            {
                int length = ReadInt32(payload, ref offset);
                if (length == -1)
                {
                    row[i] = null;
                }
                else if (length < -1)
                {
                    throw new IOException("DataRow contained an invalid negative field length.");
                }
                else
                {
                    if (offset > payload.Length - length)
                    {
                        throw new IOException("Truncated DataRow value.");
                    }

                    row[i] = _columns[i].FormatCode switch
                    {
                        0 => Encoding.UTF8.GetString(payload, offset, length),
                        1 => payload.AsSpan(offset, length).ToArray(),
                        _ => throw new IOException($"Cassie returned unsupported result format {_columns[i].FormatCode}.")
                    };
                    offset += length;
                }
            }

            return offset == payload.Length
                ? row
                : throw new IOException("DataRow payload contained trailing bytes.");
        }

        private void ParseCommandComplete(byte[] payload)
        {
            string tag = Encoding.UTF8.GetString(payload).TrimEnd('\0');
            _connection.ObserveCommandComplete(tag);
            string[] parts = tag.Split(' ');
            if (parts[0] is not ("INSERT" or "UPDATE" or "DELETE" or "MERGE" or "COPY" or "MOVE" or "FETCH"))
            {
                return;
            }

            int affected = parts.Length > 1 && long.TryParse(parts[^1], CultureInfo.InvariantCulture, out long count) && count <= int.MaxValue ? (int)count : 0;
            _recordsAffected = _recordsAffected < 0 ? affected : checked(_recordsAffected + affected);
        }

        private static long CopyFieldData<T>(T[] source, long dataOffset, T[]? destination, int destinationOffset, int length)
        {
            if (dataOffset < 0 || dataOffset > source.LongLength)
            {
                throw new ArgumentOutOfRangeException(nameof(dataOffset));
            }

            if (destination is null)
            {
                return source.LongLength;
            }

            if (destinationOffset < 0 || length < 0 || destinationOffset > destination.Length - length)
            {
                throw new ArgumentOutOfRangeException(nameof(destinationOffset));
            }

            int count = (int)Math.Min(length, source.LongLength - dataOffset);
            Array.Copy(source, dataOffset, destination, destinationOffset, count);
            return count;
        }

        private static CassieException ParseError(byte[] payload)
        {
            Dictionary<byte, string> fields = [];
            int offset = 0;
            while (offset < payload.Length && payload[offset] != 0)
            {
                byte field = payload[offset++];
                string value = ReadCString(payload, ref offset);
                fields[field] = value;
            }

            return new CassieException(
                fields.GetValueOrDefault((byte)'M') ?? "Cassie returned a query error.",
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

        private static short ReadInt16(byte[] bytes, ref int offset)
        {
            if (offset > bytes.Length - 2)
            {
                throw new IOException("Truncated pgwire frame.");
            }

            short value = BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(offset));
            offset += 2;
            return value;
        }

        private static int ReadInt32(byte[] bytes, ref int offset)
        {
            if (offset > bytes.Length - 4)
            {
                throw new IOException("Truncated pgwire frame.");
            }

            int value = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset));
            offset += 4;
            return value;
        }

        private static string ReadCString(byte[] bytes, ref int offset)
        {
            int end = Array.IndexOf(bytes, (byte)0, offset);
            if (end < 0)
            {
                throw new IOException("Invalid null-terminated pgwire value.");
            }

            string value = Encoding.UTF8.GetString(bytes, offset, end - offset);
            offset = end + 1;
            return value;
        }

        private sealed record CassieColumn(
            string Name,
            uint TableOid,
            short AttributeNumber,
            int TypeOid,
            short TypeSize,
            int TypeModifier,
            [param: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)]
            [property: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)] Type FieldType,
            short FormatCode);

        private sealed class CassieDbColumn : DbColumn
        {
            internal CassieDbColumn(CassieColumn column, int ordinal)
            {
                ColumnName = column.Name;
                ColumnOrdinal = ordinal;
                ColumnSize = GetColumnSize(column);
                NumericPrecision = GetNumericPrecision(column) is short precision ? precision : null;
                NumericScale = GetNumericScale(column) is short scale ? scale : null;
                DataType = column.FieldType;
                DataTypeName = CassieTypeMap.GetDataTypeName(column.TypeOid);
                AllowDBNull = null;
                BaseCatalogName = null;
                BaseColumnName = null;
                BaseSchemaName = null;
                BaseServerName = null;
                BaseTableName = null;
                IsAliased = null;
                IsAutoIncrement = null;
                IsExpression = null;
                IsHidden = false;
                IsIdentity = null;
                IsKey = null;
                IsLong = null;
                IsReadOnly = null;
                IsUnique = null;
            }
        }
    }
}
