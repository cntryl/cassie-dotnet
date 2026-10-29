using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace Cntryl.Cassie
{
    /// <summary>Represents a command to execute against Cassie.</summary>
    public sealed class CassieCommand : DbCommand
    {
        private readonly CassieParameterCollection _parameters = [];
        private string _commandText = string.Empty;
        private int _commandTimeout = 30;
        private bool _prepareRequested;
        private CassieConnection? _preparedConnection;
        private Guid _preparedSessionIdentity;
        private string? _preparedStatementName;
        private string? _preparedSql;
        private int[]? _preparedTypeOids;

        /// <inheritdoc />
        [AllowNull]
        public override string CommandText { get => _commandText; set => _commandText = value ?? string.Empty; }
        /// <inheritdoc />
        public override int CommandTimeout
        {
            get => _commandTimeout;
            set => _commandTimeout = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }
        /// <inheritdoc />
        public override CommandType CommandType { get; set; } = CommandType.Text;
        /// <inheritdoc />
        public override bool DesignTimeVisible { get; set; }
        /// <summary>Requests binary result fields for queries executed through the extended protocol.</summary>
        /// <remarks>The query must return types with binary result codecs supported by this provider.</remarks>
        public bool BinaryResults { get; set; }
        /// <inheritdoc />
        public override UpdateRowSource UpdatedRowSource { get; set; }
        /// <inheritdoc />
        protected override DbConnection? DbConnection { get; set; }
        /// <inheritdoc />
        protected override DbParameterCollection DbParameterCollection => _parameters;
        /// <inheritdoc />
        protected override DbTransaction? DbTransaction { get; set; }

        /// <summary>Resolves named text markers or builds a positional stored-procedure call.</summary>
        /// <returns>The SQL text, ordered parameter names, and ordered parameter values.</returns>
        /// <exception cref="InvalidOperationException">A text marker is not bound, or a binding is unused.</exception>
        public CassiePreparedCommand PrepareParameters()
        {
            if (CommandType == CommandType.StoredProcedure)
            {
                return PrepareStoredProcedure();
            }

            if (CommandType != CommandType.Text)
            {
                throw new NotSupportedException($"Cassie does not support command type '{CommandType}'.");
            }

            CassieConnection.ValidateSqlText(CommandText);

            if (_parameters.Count == 0 && CommandText.AsSpan().IndexOf('@') < 0)
            {
                return new CassiePreparedCommand(
                    CommandText,
                    [],
                    [],
                    [],
                    []);
            }

            if (_parameters.Count == 1)
            {
                return PrepareSingleParameter();
            }

            Dictionary<string, DbParameter> bindings = new(StringComparer.OrdinalIgnoreCase);
            foreach (DbParameter parameter in _parameters)
            {
                if (parameter.Direction != ParameterDirection.Input)
                {
                    throw new NotSupportedException("Cassie parameters are input-only; CALL output parameters are not supported.");
                }

                string name = NormalizeParameterName(parameter.ParameterName);
                if (name.Length == 0)
                {
                    throw new InvalidOperationException("A parameter name cannot be empty.");
                }

                if (!bindings.TryAdd(name, parameter))
                {
                    throw new InvalidOperationException($"Parameter '{name}' is bound more than once.");
                }
            }

            List<string> names = [];
            List<object?> values = [];
            List<int> typeOids = [];
            List<CassieParameterFormat> parameterFormats = [];
            Dictionary<string, int> slots = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
            string rewritten = Rewrite(CommandText, name =>
            {
                _ = used.Add(name);
                if (!bindings.TryGetValue(name, out DbParameter? parameter))
                {
                    throw new InvalidOperationException($"SQL parameter '{name}' has no matching binding.");
                }

                if (!slots.TryGetValue(name, out int slot))
                {
                    slot = names.Count + 1;
                    slots.Add(name, slot);
                    names.Add(name);
                    values.Add(parameter.Value is DBNull ? null : parameter.Value);
                    typeOids.Add(GetPostgresTypeOid(parameter));
                    parameterFormats.Add(parameter is CassieParameter cassieParameter ? cassieParameter.ParameterFormat : CassieParameterFormat.Auto);
                }

                return "$" + slot.ToString(CultureInfo.InvariantCulture);
            });

            string[] unused = [.. bindings.Keys.Where(name => !used.Contains(name))];
            return unused.Length > 0
                ? throw new InvalidOperationException($"Parameter binding(s) are not used by SQL: {string.Join(", ", unused)}.")
                : new CassiePreparedCommand(rewritten, names.AsReadOnly(), values.AsReadOnly(), typeOids.AsReadOnly(), parameterFormats.AsReadOnly());
        }

        private CassiePreparedCommand PrepareSingleParameter()
        {
            DbParameter parameter = _parameters[0];
            if (parameter.Direction != ParameterDirection.Input)
            {
                throw new NotSupportedException("Cassie parameters are input-only; CALL output parameters are not supported.");
            }

            string name = NormalizeParameterName(parameter.ParameterName);
            if (name.Length == 0)
            {
                throw new InvalidOperationException("A parameter name cannot be empty.");
            }

            bool used = false;
            string rewritten = Rewrite(CommandText, marker =>
            {
                if (!marker.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"SQL parameter '{marker}' has no matching binding.");
                }

                used = true;
                return "$1";
            });
            if (!used)
            {
                throw new InvalidOperationException($"Parameter binding(s) are not used by SQL: {name}.");
            }

            object? value = parameter.Value is DBNull ? null : parameter.Value;
            int typeOid = GetPostgresTypeOid(parameter);
            CassieParameterFormat format = parameter is CassieParameter cassieParameter
                ? cassieParameter.ParameterFormat
                : CassieParameterFormat.Auto;
            return new CassiePreparedCommand(rewritten, [name], [value], [typeOid], [format]);
        }

        private CassiePreparedCommand PrepareStoredProcedure()
        {
            string procedureName = QuoteProcedureName(CommandText);
            List<string> names = [];
            List<object?> values = [];
            List<int> typeOids = [];
            List<CassieParameterFormat> parameterFormats = [];
            StringBuilder sql = new();
            _ = sql.Append("CALL ").Append(procedureName).Append('(');

            foreach (DbParameter parameter in _parameters)
            {
                if (parameter.Direction != ParameterDirection.Input)
                {
                    throw new NotSupportedException("Cassie parameters are input-only; CALL output parameters are not supported.");
                }

                if (names.Count > 0)
                {
                    _ = sql.Append(", ");
                }

                _ = sql.Append('$').Append((names.Count + 1).ToString(CultureInfo.InvariantCulture));
                string parameterName = NormalizeParameterName(parameter.ParameterName ?? string.Empty);
                names.Add(parameterName.Length == 0 ? (names.Count + 1).ToString(CultureInfo.InvariantCulture) : parameterName);
                values.Add(parameter.Value is DBNull ? null : parameter.Value);
                typeOids.Add(GetPostgresTypeOid(parameter));
                parameterFormats.Add(parameter is CassieParameter cassieParameter ? cassieParameter.ParameterFormat : CassieParameterFormat.Auto);
            }

            _ = sql.Append(')');
            return new CassiePreparedCommand(sql.ToString(), names.AsReadOnly(), values.AsReadOnly(), typeOids.AsReadOnly(), parameterFormats.AsReadOnly());
        }

        [SuppressMessage("Globalization", "CA1308", Justification = "PostgreSQL folds unquoted procedure identifiers to lowercase before resolution.")]
        private static string QuoteProcedureName(string commandText)
        {
            if (string.IsNullOrWhiteSpace(commandText))
            {
                throw InvalidProcedureName();
            }

            List<string> components = [];
            int index = 0;
            while (true)
            {
                SkipProcedureNameWhitespace(commandText, ref index);
                if (index >= commandText.Length || components.Count == 2)
                {
                    throw InvalidProcedureName();
                }

                string component;
                if (commandText[index] == '"')
                {
                    index++;
                    StringBuilder quoted = new();
                    bool closed = false;
                    while (index < commandText.Length)
                    {
                        char current = commandText[index++];
                        if (current == '\0')
                        {
                            throw InvalidProcedureName();
                        }

                        if (current != '"')
                        {
                            _ = quoted.Append(current);
                            continue;
                        }

                        if (index < commandText.Length && commandText[index] == '"')
                        {
                            _ = quoted.Append('"');
                            index++;
                            continue;
                        }

                        closed = true;
                        break;
                    }

                    if (!closed || quoted.Length == 0)
                    {
                        throw InvalidProcedureName();
                    }

                    component = quoted.ToString();
                }
                else
                {
                    int start = index;
                    if (!IsProcedureIdentifierStart(commandText[index]))
                    {
                        throw InvalidProcedureName();
                    }

                    index++;
                    while (index < commandText.Length && IsProcedureIdentifierPart(commandText[index]))
                    {
                        index++;
                    }

                    component = commandText[start..index].ToLowerInvariant();
                }

                components.Add(component);
                SkipProcedureNameWhitespace(commandText, ref index);
                if (index == commandText.Length)
                {
                    break;
                }

                if (commandText[index++] != '.')
                {
                    throw InvalidProcedureName();
                }
            }

            return string.Join('.', components.Select(component => $"\"{component.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));
        }

        private static bool IsProcedureIdentifierStart(char value)
        {
            return char.IsLetter(value) || value == '_';
        }

        private static bool IsProcedureIdentifierPart(char value)
        {
            return char.IsLetterOrDigit(value) || value is '_' or '$';
        }

        private static void SkipProcedureNameWhitespace(string value, ref int index)
        {
            while (index < value.Length && char.IsWhiteSpace(value[index]))
            {
                index++;
            }
        }

        private static ArgumentException InvalidProcedureName()
        {
            return new ArgumentException("CommandText must be a procedure name, optionally qualified by schema.", "CommandText");
        }

        private static int GetPostgresTypeOid(DbType dbType, object? value)
        {
            if (value is Array parameterArray and not byte[])
            {
                return CassieTypeMap.GetArrayParameterOid(parameterArray);
            }

            if (value is CassieArray cassieArray)
            {
                return CassieTypeMap.GetArrayParameterOid(cassieArray);
            }

            if (dbType == DbType.Xml)
            {
                throw new NotSupportedException("Cassie does not support XML parameters.");
            }

            int explicitOid = dbType switch
            {
                DbType.Boolean => 16,
                DbType.Binary => 17,
                DbType.Byte or DbType.SByte or DbType.Int16 => 21,
                DbType.Int32 or DbType.UInt16 => 23,
                DbType.Int64 => 20,
                DbType.UInt32 => 26,
                DbType.UInt64 or DbType.Decimal or DbType.Currency or DbType.VarNumeric => 1700,
                DbType.Single => 700,
                DbType.Double => 701,
                DbType.Guid => 2950,
                DbType.Date => 1082,
                DbType.Time => 1083,
                DbType.DateTimeOffset => 1184,
                DbType.DateTime or DbType.DateTime2 => 1114,
                DbType.AnsiStringFixedLength or DbType.StringFixedLength => 1042,
                DbType.AnsiString or DbType.String => 25,
                DbType.Object => 0,
                _ => 0
            };
            return explicitOid != 0
                ? explicitOid
                : value switch
                {
                    bool => 16,
                    byte or sbyte or short => 21,
                    int or ushort => 23,
                    long => 20,
                    uint => 26,
                    ulong or decimal or CassieNumeric => 1700,
                    CassieTemporalValue temporal => temporal.PostgresTypeOid,
                    CassieLsn => 3220,
                    CassieVector vector => CassieTypeMap.GetVectorOid(vector.Dimension),
                    CassieNetworkAddress networkAddress => networkAddress.IsCidr ? 650 : 869,
                    CassieBitString bits => bits.IsVarying ? 1562 : 1560,
                    CassieTimeWithTimeZone => 1266,
                    CassieInterval => 1186,
                    System.Net.NetworkInformation.PhysicalAddress macAddress => macAddress.GetAddressBytes().Length switch
                    {
                        6 => 829,
                        8 => 774,
                        _ => throw new ArgumentException("MAC address parameters must contain six or eight bytes.", nameof(value))
                    },
                    float => 700,
                    double => 701,
                    byte[] => 17,
                    Guid => 2950,
                    DateOnly => 1082,
                    TimeOnly => 1083,
                    TimeSpan => 1083,
                    DateTimeOffset => 1184,
                    DateTime => 1114,
                    Array array => CassieTypeMap.GetArrayParameterOid(array),
                    string or char => 25,
                    _ => 0
                };
        }

        private static int GetPostgresTypeOid(DbParameter parameter)
        {
            if (parameter is CassieParameter cassieParameter)
            {
                if (cassieParameter.PostgresTypeOid < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(parameter), "PostgreSQL type OID cannot be negative.");
                }

                if (cassieParameter.PostgresTypeOid != 0)
                {
                    return parameter.Value is CassieTemporalValue temporal && temporal.PostgresTypeOid != cassieParameter.PostgresTypeOid
                        ? throw new ArgumentException("The temporal value does not match the explicitly selected PostgreSQL type OID.", nameof(parameter))
                        : cassieParameter.PostgresTypeOid;
                }
            }

            return GetPostgresTypeOid(parameter.DbType, parameter.Value);
        }

        private static string NormalizeParameterName(string name)
        {
            return name.Trim().TrimStart('@', ':');
        }

        private static string Rewrite(string sql, Func<string, string> replace)
        {
            StringBuilder result = new(sql.Length);
            ScanState state = ScanState.Normal;
            int blockCommentDepth = 0;
            string? dollarQuoteDelimiter = null;
            bool escapeString = false;
            for (int index = 0; index < sql.Length; index++)
            {
                char current = sql[index];
                char next = index + 1 < sql.Length ? sql[index + 1] : '\0';
                if (dollarQuoteDelimiter is not null)
                {
                    if (sql.AsSpan(index).StartsWith(dollarQuoteDelimiter, StringComparison.Ordinal))
                    {
                        _ = result.Append(dollarQuoteDelimiter);
                        index += dollarQuoteDelimiter.Length - 1;
                        dollarQuoteDelimiter = null;
                    }
                    else
                    {
                        _ = result.Append(current);
                    }

                    continue;
                }
                if (state == ScanState.LineComment)
                {
                    _ = result.Append(current);
                    if (current is '\r' or '\n')
                    {
                        state = ScanState.Normal;
                    }

                    continue;
                }

                if (state == ScanState.BlockComment)
                {
                    _ = result.Append(current);
                    if (current == '/' && next == '*') { _ = result.Append(next); index++; blockCommentDepth++; }
                    else if (current == '*' && next == '/')
                    {
                        _ = result.Append(next);
                        index++;
                        blockCommentDepth--;
                        if (blockCommentDepth == 0)
                        {
                            state = ScanState.Normal;
                        }
                    }
                    continue;
                }

                if (state is ScanState.SingleQuote or ScanState.DoubleQuote)
                {
                    char delimiter = state == ScanState.SingleQuote ? '\'' : '"';
                    _ = result.Append(current);
                    if (current == delimiter)
                    {
                        if (next == delimiter) { _ = result.Append(next); index++; }
                        else
                        {
                            state = ScanState.Normal;
                        }
                    }
                    else if (current == '\\' && state == ScanState.SingleQuote && escapeString && next != '\0') { _ = result.Append(next); index++; }
                    continue;
                }

                if (current == '-' && next == '-') { _ = result.Append(current).Append(next); index++; state = ScanState.LineComment; continue; }
                if (current == '/' && next == '*') { _ = result.Append(current).Append(next); index++; state = ScanState.BlockComment; blockCommentDepth = 1; continue; }
                if (current == '\'') { _ = result.Append(current); escapeString = SqlParameterScanner.IsEscapeStringPrefix(sql, index); state = ScanState.SingleQuote; continue; }
                if (current == '"') { _ = result.Append(current); state = ScanState.DoubleQuote; continue; }
                if (current == '$' && SqlParameterScanner.TryReadDollarQuoteDelimiter(sql, index, out string dollarDelimiterToken))
                {
                    _ = result.Append(dollarDelimiterToken);
                    index += dollarDelimiterToken.Length - 1;
                    dollarQuoteDelimiter = dollarDelimiterToken;
                    continue;
                }
                if (current == '@' && next != '@' && (char.IsLetter(next) || next == '_'))
                {
                    int start = index + 1;
                    index = start;
                    while (index < sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index] is '_' or '$'))
                    {
                        index++;
                    }

                    _ = result.Append(replace(sql[start..index]));
                    index--;
                    continue;
                }

                _ = result.Append(current);
            }

            return result.ToString();
        }

        private enum ScanState { Normal, SingleQuote, DoubleQuote, LineComment, BlockComment }

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
            using CassieDataReader reader = (CassieDataReader)ExecuteDbDataReader(CommandBehavior.Default);
            do
            {
                while (reader.Read()) { }
            }
            while (reader.NextResult());

            return reader.RecordsAffected;
        }

        /// <inheritdoc />
        public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            CassieDataReader reader = (CassieDataReader)await ExecuteDbDataReaderAsync(CommandBehavior.Default, cancellationToken).ConfigureAwait(false);
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

        /// <summary>Streams input to a Cassie <c>COPY ... FROM STDIN</c> or <c>RESTORE DATABASE ... FROM STDIN</c> command.</summary>
        /// <param name="source">A readable stream containing the COPY input bytes.</param>
        /// <param name="cancellationToken">A token used to cancel COPY and recover the connection.</param>
        /// <returns>The number of rows reported by Cassie.</returns>
        public Task<int> CopyFromAsync(Stream source, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (!source.CanRead)
            {
                throw new ArgumentException("The COPY input stream must be readable.", nameof(source));
            }

            if (CommandType != CommandType.Text)
            {
                throw new NotSupportedException("Only text COPY commands are supported.");
            }

            if (_parameters.Count != 0)
            {
                throw new NotSupportedException("COPY commands cannot use bound parameters.");
            }

            if (DbConnection is not CassieConnection connection)
            {
                throw new InvalidOperationException("The command requires an open Cassie connection.");
            }

            connection.ValidateCommandTransaction(DbTransaction);
            return CassieDiagnostics.ObserveAsync("copy.from", () => connection.ExecuteCopyFromAsync(CommandText, source, CommandTimeout, cancellationToken), connection.Logger);
        }

        /// <summary>Streams output from a Cassie <c>COPY ... TO STDOUT</c> command.</summary>
        /// <param name="destination">A writable stream that receives the server's COPY bytes.</param>
        /// <param name="cancellationToken">A token used to cancel COPY and recover the connection.</param>
        /// <returns>The number of rows reported by Cassie.</returns>
        public Task<int> CopyToAsync(Stream destination, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(destination);
            if (!destination.CanWrite)
            {
                throw new ArgumentException("The COPY output stream must be writable.", nameof(destination));
            }

            if (CommandType != CommandType.Text)
            {
                throw new NotSupportedException("Only text COPY commands are supported.");
            }

            if (_parameters.Count != 0)
            {
                throw new NotSupportedException("COPY commands cannot use bound parameters.");
            }

            if (DbConnection is not CassieConnection connection)
            {
                throw new InvalidOperationException("The command requires an open Cassie connection.");
            }

            connection.ValidateCommandTransaction(DbTransaction);
            return CassieDiagnostics.ObserveAsync("copy.to", () => connection.ExecuteCopyToAsync(CommandText, destination, CommandTimeout, cancellationToken), connection.Logger);
        }

        /// <inheritdoc />
        public override object? ExecuteScalar()
        {
            using CassieDataReader reader = (CassieDataReader)ExecuteDbDataReader(CommandBehavior.SingleRow);
            return reader.Read() && reader.FieldCount > 0 ? reader.GetValue(0) : null;
        }

        /// <inheritdoc />
        public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
        {
            CassieDataReader reader = (CassieDataReader)await ExecuteDbDataReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && reader.FieldCount > 0 ? reader.GetValue(0) : null;
            }
        }

        /// <inheritdoc />
        public override void Prepare()
        {
            if (CommandType is not (CommandType.Text or CommandType.StoredProcedure))
            {
                throw new NotSupportedException($"Cassie cannot prepare command type '{CommandType}'.");
            }

            CassiePreparedCommand prepared = PrepareParameters();
            _prepareRequested = true;
            if (DbConnection is CassieConnection connection && connection.State == ConnectionState.Open)
            {
                connection.ValidateCommandTransaction(DbTransaction);
                EnsureServerPrepared(connection, prepared);
            }
        }

        private void EnsureServerPrepared(CassieConnection connection, CassiePreparedCommand prepared)
        {
            bool matches = ReferenceEquals(connection, _preparedConnection)
                && connection.SessionIdentity == _preparedSessionIdentity
                && _preparedSql == prepared.Sql
                && _preparedTypeOids is not null
                && _preparedTypeOids.AsSpan().SequenceEqual(prepared.ParameterTypeOids.ToArray());
            if (matches)
            {
                return;
            }

            ReleaseServerPrepared();
            _preparedStatementName = connection.PrepareServerStatement(prepared, CommandTimeout);
            _preparedSql = prepared.Sql;
            _preparedTypeOids = [.. prepared.ParameterTypeOids];
            _preparedConnection = connection;
            _preparedSessionIdentity = connection.SessionIdentity;
        }

        private async Task EnsureServerPreparedAsync(CassieConnection connection, CassiePreparedCommand prepared, CancellationToken cancellationToken)
        {
            bool matches = ReferenceEquals(connection, _preparedConnection)
                && connection.SessionIdentity == _preparedSessionIdentity
                && _preparedSql == prepared.Sql
                && _preparedTypeOids is not null
                && _preparedTypeOids.AsSpan().SequenceEqual(prepared.ParameterTypeOids.ToArray());
            if (matches)
            {
                return;
            }

            await ReleaseServerPreparedAsync(cancellationToken).ConfigureAwait(false);
            _preparedStatementName = await connection.PrepareServerStatementAsync(prepared, CommandTimeout, cancellationToken).ConfigureAwait(false);
            _preparedSql = prepared.Sql;
            _preparedTypeOids = [.. prepared.ParameterTypeOids];
            _preparedConnection = connection;
            _preparedSessionIdentity = connection.SessionIdentity;
        }

        public override async Task PrepareAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CommandType is not (CommandType.Text or CommandType.StoredProcedure))
            {
                throw new NotSupportedException($"Cassie cannot prepare command type '{CommandType}'.");
            }

            CassiePreparedCommand prepared = PrepareParameters();
            _prepareRequested = true;
            if (DbConnection is CassieConnection connection && connection.State == ConnectionState.Open)
            {
                connection.ValidateCommandTransaction(DbTransaction);
                await EnsureServerPreparedAsync(connection, prepared, cancellationToken).ConfigureAwait(false);
            }
        }

        private void ReleaseServerPrepared()
        {
            try
            {
                if (_preparedStatementName is not null && _preparedConnection?.State == ConnectionState.Open)
                {
                    _preparedConnection.CloseServerStatement(_preparedStatementName);
                }
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or CassieException or InvalidOperationException or TimeoutException)
            {
                // Releasing a prepared statement during disposal must not throw; a failed release leaves a dead session.
            }
            finally
            {
                ClearServerPrepared();
            }
        }

        private async Task ReleaseServerPreparedAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (_preparedStatementName is not null && _preparedConnection?.State == ConnectionState.Open)
                {
                    await _preparedConnection.CloseServerStatementAsync(_preparedStatementName, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or CassieException or InvalidOperationException or TimeoutException)
            {
                // Releasing a prepared statement during disposal must not throw; a failed release leaves a dead session.
            }
            finally
            {
                ClearServerPrepared();
            }
        }

        private void ClearServerPrepared()
        {
            _preparedConnection = null;
            _preparedStatementName = null;
            _preparedSql = null;
            _preparedTypeOids = null;
            _preparedSessionIdentity = Guid.Empty;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ReleaseServerPrepared();
            }

            base.Dispose(disposing);
        }

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            try
            {
                await ReleaseServerPreparedAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                await base.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        protected override DbParameter CreateDbParameter()
        {
            return new CassieParameter();
        }

        /// <inheritdoc />
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            return ExecuteDbDataReaderAsync(behavior, CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <inheritdoc />
        [SuppressMessage("Reliability", "CA2000", Justification = "The returned reader is transferred to the DbCommand caller, who owns disposal.")]
        protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        {
            if (CommandType is not (CommandType.Text or CommandType.StoredProcedure))
            {
                throw new NotSupportedException($"Cassie does not support command type '{CommandType}'.");
            }

            if (DbConnection is not CassieConnection connection)
            {
                throw new InvalidOperationException("The command requires an open Cassie connection.");
            }

            connection.ValidateCommandTransaction(DbTransaction);
            CassieDiagnostics.OperationScope operationScope = CassieDiagnostics.StartOperation("query");
            try
            {
                CassiePreparedCommand prepared = PrepareParameters();
                string? statementName = null;
                if (_prepareRequested)
                {
                    await EnsureServerPreparedAsync(connection, prepared, cancellationToken).ConfigureAwait(false);
                    statementName = _preparedStatementName;
                }

                return await (prepared.ParameterNames.Count == 0 && !BinaryResults
                    && (behavior & CommandBehavior.SchemaOnly) == 0
                    && statementName is null
                    ? connection.ExecuteQueryAsync(prepared.Sql, cancellationToken, CommandTimeout, behavior, operationScope)
                    : connection.ExecuteParameterizedQueryAsync(prepared, CommandTimeout, cancellationToken, behavior, BinaryResults, statementName, operationScope)).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                operationScope.Fail(exception, connection.Logger);
                operationScope.Dispose();
                throw;
            }
        }
    }
}
