using System.Buffers.Text;
using System.Buffers.Binary;
using Cntryl.Cassie.Connection;
using Cntryl.Cassie.Protocol;
using Cntryl.Cassie.Diagnostics;
using Cntryl.Cassie.Types;
using Cntryl.Cassie.Types.Handlers;
using System.Collections;
using System.Data;
using System.Data.Common;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;

namespace Cntryl.Cassie.Data
{
    /// <summary>Streams text- or binary-format rows returned by a Cassie query.</summary>
    public sealed class CassieDataReader : DbDataReader, IDbColumnSchemaGenerator
    {
        private const int BoolOid = 16;
        private const int Int8Oid = 20;
        private const int Int2Oid = 21;
        private const int Int4Oid = 23;
        private const int Float4Oid = 700;
        private const int Float8Oid = 701;
        private const int DateOid = 1082;
        private const int TimestampOid = 1114;
        private const int TimestampTzOid = 1184;
        private const int NumericOid = 1700;
        private const int UuidOid = 2950;
        private const string UnexpectedSuspendedPortalMessage = "Cassie returned an unexpected suspended portal response.";
        private readonly IReaderSession _session;
        private readonly ReaderCancellation _cancellation;
        private short[] _formatCodes = [];
        private readonly List<ReaderColumn> _columns = [];
        private byte[]? _rowPayload;
        private int[] _fieldOffsets = [];
        private int[] _fieldLengths = [];
        private object?[] _rawValues = [];
        private bool _closed;
        private volatile bool _complete;
        private bool _resultEnded;
        private bool _hasCurrentResult;
        private bool _hasRows;
        private bool _hasRowsKnown;
        private bool _prefetchedRow;
        private int _recordsAffected = -1;
        private readonly bool _closeConnectionOnClose;
        private readonly bool _sequentialAccess;
        private readonly bool _singleRow;
        private readonly bool _singleResult;
        private readonly CassieDiagnostics.OperationScope? _operationScope;
        private readonly Func<CancellationToken, Task>? _resumePortalAsync;
        private readonly Func<Task>? _sendExtendedSyncAsync;
        private readonly Action<CommandTag?>? _statementCompleted;
        private int _rowsReadCurrentResult;
        private int _lastSequentialOrdinal = -1;
        private int _operationFinished;
        private int _extendedSyncSent;
        private object?[]? _converted;

        internal CassieDataReader(
            IReaderSession session,
            int commandTimeout,
            CommandBehavior behavior,
            CassieDiagnostics.OperationScope? operationScope = null,
            Func<CancellationToken, Task>? resumePortalAsync = null,
            Func<Task>? sendExtendedSyncAsync = null,
            Action<CommandTag?>? statementCompleted = null)
        {
            _statementCompleted = statementCompleted;
            _session = session;
            _cancellation = new ReaderCancellation(session, commandTimeout);
            _operationScope = operationScope;
            _resumePortalAsync = resumePortalAsync;
            _sendExtendedSyncAsync = sendExtendedSyncAsync;
            _closeConnectionOnClose = (behavior & CommandBehavior.CloseConnection) != 0;
            _sequentialAccess = (behavior & CommandBehavior.SequentialAccess) != 0;
            _singleRow = (behavior & CommandBehavior.SingleRow) != 0;
            _singleResult = (behavior & CommandBehavior.SingleResult) != 0;
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

                bool hasRow = Read();
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

        internal async Task InitializeAsync(CancellationToken cancellationToken)
        {
            try
            {
                await InitializeCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (_cancellation.IsTimeoutTransportFailure(exception))
            {
                _session.AbortTransport();
                throw ReaderCancellation.CreateTimeoutException(exception);
            }
        }

        private async Task InitializeCoreAsync(CancellationToken cancellationToken)
        {
            while (_columns.Count == 0 && !_complete && !_resultEnded)
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
                    _ = BeginStatementResultWithoutRows();
                }
                else if (tag == (byte)'I')
                {
                    ObserveEmptyStatement();
                    _ = BeginStatementResultWithoutRows();
                }
                else if (tag == (byte)'S')
                {
                    ParseParameterStatus(payload);
                }
                else if (tag == (byte)'E')
                {
                    throw await ReadServerErrorAsync(payload, cancellationToken).ConfigureAwait(false);
                }
                else if (tag == (byte)'G')
                {
                    await _session.RejectCopyFromAsync().ConfigureAwait(false);
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
            bool entered = _session.EnterSynchronous();
            try
            {
                return SynchronousCall.Wait(ReadAsync(CancellationToken.None));
            }
            finally
            {
                if (entered)
                {
                    _session.ExitSynchronous();
                }
            }
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

                _rowPayload = null;
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
                            IndexDataRow(payload);

                            _lastSequentialOrdinal = -1;
                            _rowsReadCurrentResult++;
                            _hasRows = true;
                            _hasRowsKnown = true;
                            return true;
                        case (byte)'S': ParseParameterStatus(payload); break;
                        case (byte)'C': ParseCommandComplete(payload); _hasRowsKnown = true; _resultEnded = true; return false;
                        case (byte)'I': ClearColumns(); _hasRows = false; _hasRowsKnown = true; _resultEnded = true; return false;
                        case (byte)'E':
                            throw await ReadServerErrorAsync(payload, cancellationToken).ConfigureAwait(false);
                        case (byte)'Z': await MarkReadyAsync(payload).ConfigureAwait(false); break;
                        case (byte)'G': await _session.RejectCopyFromAsync().ConfigureAwait(false); break;
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
            catch (Exception exception) when (_cancellation.IsTimeoutTransportFailure(exception))
            {
                _session.AbortTransport();
                TimeoutException timeout = ReaderCancellation.CreateTimeoutException(exception);
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
            if (_complete || !_session.IsOpen)
            {
                FinishOperation();
                return;
            }

            try
            {
                if (!await _cancellation.RequestCancelAsync().ConfigureAwait(false))
                {
                    _session.AbortTransport();
                    FinishOperation();
                    return;
                }

                await EnsureExtendedSyncSentAsync().ConfigureAwait(false);
                using CancellationTokenSource drainTimeout = new(TimeSpan.FromSeconds(5));
                await DrainToReadyAsync(drainTimeout.Token, allowAbortOnCancellation: true).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _session.AbortTransport();
                FinishOperation(exception);
                throw;
            }
        }

        /// <inheritdoc />
        public override bool NextResult()
        {
            bool entered = _session.EnterSynchronous();
            try
            {
                return SynchronousCall.Wait(NextResultAsync(CancellationToken.None));
            }
            finally
            {
                if (entered)
                {
                    _session.ExitSynchronous();
                }
            }
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
                    ClearColumns();
                    _rowPayload = null;
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

                ClearColumns();
                _rowPayload = null;
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
                        case (byte)'C':
                            ParseCommandComplete(payload);
                            if (BeginStatementResultWithoutRows())
                            {
                                return true;
                            }

                            break;
                        case (byte)'I':
                            ObserveEmptyStatement();
                            if (BeginStatementResultWithoutRows())
                            {
                                return true;
                            }

                            break;
                        case (byte)'E':
                            throw await ReadServerErrorAsync(payload, cancellationToken).ConfigureAwait(false);
                        case (byte)'G': await _session.RejectCopyFromAsync().ConfigureAwait(false); break;
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
            catch (Exception exception) when (_cancellation.IsTimeoutTransportFailure(exception))
            {
                _session.AbortTransport();
                TimeoutException timeout = ReaderCancellation.CreateTimeoutException(exception);
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
                string text => CassieResultCodec.ConvertValue(text, GetColumn(ordinal).TypeOid, _session.DateStyle),
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
            return CassieTypeHandlers.FindFieldReader<T>() is { } reader
                ? reader.Read(this, ordinal)
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
            return GetFieldLength(ordinal) < 0;
        }

        /// <inheritdoc />
        public override bool GetBoolean(int ordinal)
        {
            return TryGetBooleanField(ordinal, out bool boolean)
                ? boolean
                : Convert.ToBoolean(GetValue(ordinal), CultureInfo.InvariantCulture);
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
            return TryGetDateTimeField(ordinal, out DateTime fast) ? fast : GetValue(ordinal) switch
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
            return TryGetDecimalField(ordinal, out decimal fast) ? fast : GetValue(ordinal) switch
            {
                CassieNumeric numeric => numeric.ToDecimal(),
                decimal value => value,
                _ => decimal.Parse(GetRequiredString(ordinal), CultureInfo.InvariantCulture)
            };
        }

        /// <inheritdoc />
        public override double GetDouble(int ordinal)
        {
            return TryGetDoubleField(ordinal, out double fast) ? fast : GetValue(ordinal) switch
            {
                double value => value,
                float value => value,
                _ => double.Parse(GetRequiredString(ordinal), CultureInfo.InvariantCulture)
            };
        }

        /// <inheritdoc />
        public override float GetFloat(int ordinal)
        {
            return TryGetFloatField(ordinal, out float fast) ? fast : GetValue(ordinal) switch
            {
                float value => value,
                _ => float.Parse(GetRequiredString(ordinal), CultureInfo.InvariantCulture)
            };
        }

        /// <inheritdoc />
        public override Guid GetGuid(int ordinal)
        {
            return TryGetGuidField(ordinal, out Guid fast) ? fast : GetValue(ordinal) switch
            {
                Guid value => value,
                _ => Guid.Parse(GetRequiredString(ordinal))
            };
        }

        /// <inheritdoc />
        public override short GetInt16(int ordinal)
        {
            return TryGetIntegerField(ordinal, out long integer) && integer is >= short.MinValue and <= short.MaxValue
                ? (short)integer
                : GetValue(ordinal) switch
                {
                    short value => value,
                    _ => short.Parse(GetRequiredString(ordinal), CultureInfo.InvariantCulture)
                };
        }

        /// <inheritdoc />
        public override int GetInt32(int ordinal)
        {
            return TryGetIntegerField(ordinal, out long integer) && integer is >= int.MinValue and <= int.MaxValue
                ? (int)integer
                : GetValue(ordinal) switch
                {
                    int value => value,
                    short value => value,
                    _ => int.Parse(GetRequiredString(ordinal), CultureInfo.InvariantCulture)
                };
        }

        /// <inheritdoc />
        public override long GetInt64(int ordinal)
        {
            return TryGetIntegerField(ordinal, out long integer)
                ? integer
                : GetValue(ordinal) switch
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
            bool entered = _session.EnterSynchronous();
            try
            {
                SynchronousCall.Wait(CloseAsync());
            }
            finally
            {
                if (entered)
                {
                    _session.ExitSynchronous();
                }
            }
        }

        /// <inheritdoc />
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
                _session.AbortTransport();
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
                await _cancellation.DisposeAsync().ConfigureAwait(false);

                _closed = true;
                FinishOperation();
            }

            if (_closeConnectionOnClose)
            {
                await _session.CloseAsync().ConfigureAwait(false);
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
        public override DataTable GetSchemaTable()
        {
            return ReaderSchema.CreateTable(_columns);
        }

        /// <summary>Gets column metadata while preserving unknown origin and constraint values.</summary>
        public ReadOnlyCollection<DbColumn> GetColumnSchema()
        {
            return ReaderSchema.CreateColumns(_columns);
        }

        private ReaderColumn GetColumn(int ordinal)
        {
            return (uint)ordinal < (uint)_columns.Count ? _columns[ordinal] : throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "Column ordinal is out of range.");
        }

        /// <summary>Validates access to a field of the current row and returns its byte length, or -1 for NULL.</summary>
        private int GetFieldLength(int ordinal)
        {
            EnsureOpen();
            _ = GetColumn(ordinal);
            if (_sequentialAccess && ordinal < _lastSequentialOrdinal)
            {
                throw new InvalidOperationException("Columns must be read in column order when SequentialAccess is enabled.");
            }

            _lastSequentialOrdinal = Math.Max(_lastSequentialOrdinal, ordinal);
            return _rowPayload is null ? throw new InvalidOperationException("The reader is not positioned on a row.") : _fieldLengths[ordinal];
        }

        /// <summary>Returns the field as a text string or binary byte array, decoding it on first access.</summary>
        private object? GetRowValue(int ordinal)
        {
            int length = GetFieldLength(ordinal);
            return length < 0
                ? null
                : _rawValues[ordinal] ??= RowMessages.DecodeField(_rowPayload!, _fieldOffsets[ordinal], length, _formatCodes[ordinal]);
        }

        /// <summary>
        /// Reads an integer column straight from the row bytes, without creating a string or boxing.
        /// Returns false for anything unusual so the general path reports it exactly as before.
        /// </summary>
        private bool TryGetIntegerField(int ordinal, out long value)
        {
            value = 0;
            int length = GetFieldLength(ordinal);
            if (length < 0)
            {
                return false;
            }

            int typeOid = _columns[ordinal].TypeOid;
            ReadOnlySpan<byte> field = _rowPayload.AsSpan(_fieldOffsets[ordinal], length);
            if (_formatCodes[ordinal] == 0)
            {
                (long minimum, long maximum) = typeOid switch
                {
                    Int2Oid => (short.MinValue, short.MaxValue),
                    Int4Oid => (int.MinValue, int.MaxValue),
                    Int8Oid => (long.MinValue, long.MaxValue),
                    _ => (1L, 0L)
                };
                return minimum <= maximum
                    && Utf8Parser.TryParse(field, out value, out int consumed)
                    && consumed == length
                    && value >= minimum
                    && value <= maximum;
            }

            switch (typeOid)
            {
                case Int2Oid when length == sizeof(short):
                    value = BinaryPrimitives.ReadInt16BigEndian(field);
                    return true;
                case Int4Oid when length == sizeof(int):
                    value = BinaryPrimitives.ReadInt32BigEndian(field);
                    return true;
                case Int8Oid when length == sizeof(long):
                    value = BinaryPrimitives.ReadInt64BigEndian(field);
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Exposes a non-null field of the current row as raw bytes with its type and wire format.
        /// Returns false for NULL and for format codes other than text or binary, so the general path reports them.
        /// </summary>
        private bool TryGetFieldBytes(int ordinal, out int typeOid, out bool binary, out ReadOnlySpan<byte> field)
        {
            int length = GetFieldLength(ordinal);
            short format = length < 0 ? (short)-1 : _formatCodes[ordinal];
            typeOid = _columns[ordinal].TypeOid;
            binary = format == 1;
            field = format is 0 or 1 ? _rowPayload.AsSpan(_fieldOffsets[ordinal], length) : default;
            return format is 0 or 1;
        }

        private bool TryGetBooleanField(int ordinal, out bool value)
        {
            value = false;
            if (!TryGetFieldBytes(ordinal, out int typeOid, out bool binary, out ReadOnlySpan<byte> field) || typeOid != BoolOid)
            {
                return false;
            }

            if (binary)
            {
                value = field.Length == 1 && field[0] == 1;
                return field.Length == 1 && field[0] <= 1;
            }

            if (field.SequenceEqual("t"u8) || field.SequenceEqual("true"u8) || field.SequenceEqual("TRUE"u8))
            {
                value = true;
                return true;
            }

            return field.SequenceEqual("f"u8) || field.SequenceEqual("false"u8) || field.SequenceEqual("FALSE"u8);
        }

        private bool TryGetDoubleField(int ordinal, out double value)
        {
            value = 0;
            if (!TryGetFieldBytes(ordinal, out int typeOid, out bool binary, out ReadOnlySpan<byte> field))
            {
                return false;
            }

            switch (typeOid)
            {
                case Float8Oid when binary:
                    if (field.Length != sizeof(double))
                    {
                        return false;
                    }

                    value = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(field));
                    return true;
                case Float8Oid:
                    return double.TryParse(field, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value);
                case Float4Oid:
                    if (!TryReadSingle(field, binary, out float single))
                    {
                        return false;
                    }

                    value = single;
                    return true;
                default:
                    return false;
            }
        }

        private bool TryGetFloatField(int ordinal, out float value)
        {
            value = 0;
            return TryGetFieldBytes(ordinal, out int typeOid, out bool binary, out ReadOnlySpan<byte> field)
                && typeOid == Float4Oid
                && TryReadSingle(field, binary, out value);
        }

        private static bool TryReadSingle(ReadOnlySpan<byte> field, bool binary, out float value)
        {
            if (!binary)
            {
                return float.TryParse(field, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value);
            }

            value = field.Length == sizeof(float) ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(field)) : 0;
            return field.Length == sizeof(float);
        }

        private bool TryGetGuidField(int ordinal, out Guid value)
        {
            value = Guid.Empty;
            if (!TryGetFieldBytes(ordinal, out int typeOid, out bool binary, out ReadOnlySpan<byte> field) || typeOid != UuidOid)
            {
                return false;
            }

            if (!binary)
            {
                return Guid.TryParse(field, out value);
            }

            if (field.Length != 16)
            {
                return false;
            }

            value = new Guid(field, bigEndian: true);
            return true;
        }

        private bool TryGetDecimalField(int ordinal, out decimal value)
        {
            value = 0;
            return TryGetFieldBytes(ordinal, out int typeOid, out bool binary, out ReadOnlySpan<byte> field)
                && typeOid == NumericOid
                && !binary
                && IsPlainDecimalText(field)
                && decimal.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>Accepts only an optional minus sign, digits, and an optional fraction, a form every numeric parser agrees on.</summary>
        private static bool IsPlainDecimalText(ReadOnlySpan<byte> text)
        {
            int index = text.Length > 0 && text[0] == (byte)'-' ? 1 : 0;
            int integerStart = index;
            while (index < text.Length && char.IsAsciiDigit((char)text[index]))
            {
                index++;
            }

            if (index == integerStart)
            {
                return false;
            }

            if (index == text.Length)
            {
                return true;
            }

            if (text[index] != (byte)'.')
            {
                return false;
            }

            int fractionStart = ++index;
            while (index < text.Length && char.IsAsciiDigit((char)text[index]))
            {
                index++;
            }

            return index > fractionStart && index == text.Length;
        }

        private bool TryGetDateTimeField(int ordinal, out DateTime value)
        {
            value = default;
            if (!TryGetFieldBytes(ordinal, out int typeOid, out bool binary, out ReadOnlySpan<byte> field) || !binary)
            {
                return false;
            }

            switch (typeOid)
            {
                case DateOid when field.Length == sizeof(int):
                    if (!DateHandler.TryReadBinaryDate(field, out DateOnly date))
                    {
                        return false;
                    }

                    value = date.ToDateTime(TimeOnly.MinValue);
                    return true;
                case TimestampOid when field.Length == sizeof(long):
                    return TimestampHandler.TryReadBinaryTimestamp(field, out value);
                case TimestampTzOid when field.Length == sizeof(long):
                    return TimestampTzHandler.TryReadBinaryTimestampTz(field, out value);
                default:
                    return false;
            }
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

        internal TimeSpan GetTimeSpanValue(int ordinal)
        {
            return GetValue(ordinal) switch
            {
                TimeOnly time => time.ToTimeSpan(),
                TimeSpan time => time,
                CassieTemporalValue { Kind: CassieTemporalKind.Time, HasRawValue: true } time => TimeSpan.FromTicks(checked(time.Value * 10)),
                _ => throw new InvalidCastException("The column is not a TIME value.")
            };
        }

        internal CassieTemporalValue GetTemporalValue(int ordinal)
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

        internal ulong GetUInt64Value(int ordinal)
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

        internal ValueTask StopCancellationAsync()
        {
            return _cancellation.DisposeAsync();
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
            _operationScope?.Fail(exception, _session.Logger);
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
                    serverError = ServerErrorParser.Parse(payload);
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
                throw TranslateServerError(serverError, cancellationToken);
            }
        }

        private ValueTask<(byte Tag, byte[] Payload)> ReadQueryFrameAsync(CancellationToken cancellationToken, bool allowAbortOnCancellation = false)
        {
            return _resumePortalAsync is null && _sendExtendedSyncAsync is null
                ? _cancellation.ReadFrameAsync(cancellationToken, allowAbortOnCancellation)
                : ReadQueryFrameWithProtocolHandlingAsync(allowAbortOnCancellation, cancellationToken);
        }

        private async ValueTask<(byte Tag, byte[] Payload)> ReadQueryFrameWithProtocolHandlingAsync(bool allowAbortOnCancellation, CancellationToken cancellationToken)
        {
            while (true)
            {
                (byte tag, byte[] payload) = await _cancellation.ReadFrameAsync(cancellationToken, allowAbortOnCancellation).ConfigureAwait(false);
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

        private async Task MarkReadyAsync(byte[] payload)
        {
            if (!_hasRowsKnown)
            {
                _hasRowsKnown = true;
            }

            _session.HandleReadyForQuery(payload);
            _complete = true;
            await _cancellation.DisposeAsync().ConfigureAwait(false);

            await _session.ReaderClosedAsync().ConfigureAwait(false);
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
                        throw await ReadServerErrorAsync(payload, cancellationToken).ConfigureAwait(false);
                    case (byte)'G':
                        await _session.RejectCopyFromAsync().ConfigureAwait(false);
                        break;
                    case (byte)'Z':
                        await MarkReadyAsync(payload).ConfigureAwait(false);
                        return;
                }
            }
        }

        private void ClearColumns()
        {
            _columns.Clear();
            _formatCodes = [];
        }

        private void ParseRowDescription(byte[] payload)
        {
            FieldDescription[] fields = RowMessages.ParseRowDescription(payload);
            ClearColumns();
            _formatCodes = new short[fields.Length];
            _converted = null;
            _rowPayload = null;
            _fieldOffsets = new int[fields.Length];
            _fieldLengths = new int[fields.Length];
            _rawValues = new object?[fields.Length];
            _hasRows = false;
            _hasRowsKnown = false;
            _prefetchedRow = false;
            _rowsReadCurrentResult = 0;
            for (int i = 0; i < fields.Length; i++)
            {
                FieldDescription field = fields[i];
                _formatCodes[i] = field.FormatCode;
                _columns.Add(new ReaderColumn(field.Name, field.TableOid, field.AttributeNumber, field.TypeOid, field.TypeSize, field.TypeModifier, CassieResultCodec.GetClrType(field.TypeOid, field.FormatCode), field.FormatCode));
            }
        }

        private void ParseParameterStatus(byte[] payload)
        {
            _session.HandleParameterStatusMessage(payload);
        }

        /// <summary>Positions the reader on a row. Fields are decoded only when a getter asks for them.</summary>
        private void IndexDataRow(byte[] payload)
        {
            int fieldCount = _formatCodes.Length;
            RowMessages.IndexDataRow(payload, _fieldOffsets.AsSpan(0, fieldCount), _fieldLengths.AsSpan(0, fieldCount));
            Array.Clear(_rawValues);
            if (_converted is not null)
            {
                Array.Clear(_converted);
            }

            _rowPayload = payload;
        }

        private void ParseCommandComplete(byte[] payload)
        {
            CommandTag tag = CommandTag.FromPayload(payload);
            _session.ObserveCommandComplete(tag);
            if (tag.CountsRecords)
            {
                _recordsAffected = _recordsAffected < 0 ? tag.RowCount : checked(_recordsAffected + tag.RowCount);
            }

            _statementCompleted?.Invoke(tag);
        }

        /// <summary>Reports an EmptyQueryResponse to a pipelined batch, which counts it as a statement.</summary>
        private void ObserveEmptyStatement()
        {
            _statementCompleted?.Invoke(null);
        }

        /// <summary>
        /// A pipelined batch yields one result per statement, including statements that return no rows, so a
        /// completed statement with no preceding RowDescription becomes a result of its own. A plain command skips it.
        /// </summary>
        private bool BeginStatementResultWithoutRows()
        {
            if (_statementCompleted is null)
            {
                return false;
            }

            _hasCurrentResult = true;
            _resultEnded = true;
            _hasRowsKnown = true;
            return true;
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

        private async Task<Exception> ReadServerErrorAsync(byte[] payload, CancellationToken cancellationToken)
        {
            CassieException error = ServerErrorParser.Parse(payload);
            MarkOperationFailed(error);
            await DrainToReadyAsync(cancellationToken).ConfigureAwait(false);
            return TranslateServerError(error, cancellationToken);
        }

        private Exception TranslateServerError(CassieException error, CancellationToken cancellationToken)
        {
            return error.SqlState != "57014" ? error
                : _cancellation.TimedOut
                    ? new TimeoutException("The command exceeded CommandTimeout and was cancelled by Cassie.")
                    : cancellationToken.IsCancellationRequested
                        ? new OperationCanceledException("The query was canceled.", error, cancellationToken)
                        : error;
        }

    }
}
