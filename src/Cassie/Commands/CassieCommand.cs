using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Cntryl.Cassie.Connection;
using Cntryl.Cassie.Data;
using Cntryl.Cassie.Diagnostics;

namespace Cntryl.Cassie.Commands;

/// <summary>Represents a command to execute against Cassie.</summary>
public sealed class CassieCommand : DbCommand
{
    private readonly PreparedStatementLease _preparedStatement = new();
    private string _commandText = string.Empty;
    private int _commandTimeout = 30;

    /// <inheritdoc />
    [AllowNull]
    public override string CommandText
    {
        get => _commandText;
        set => _commandText = value ?? string.Empty;
    }

    /// <inheritdoc />
    public override int CommandTimeout
    {
        get => _commandTimeout;
        set => _commandTimeout = CommandTimeouts.Validate(value);
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

    /// <summary>Gets or sets the Cassie connection this command runs on.</summary>
    public new CassieConnection? Connection { get; set; }

    /// <summary>Gets the parameters bound to this command.</summary>
    public new CassieParameterCollection Parameters { get; } = [];

    /// <summary>Gets or sets the Cassie transaction this command runs in.</summary>
    public new CassieTransaction? Transaction { get; set; }

    /// <inheritdoc />
    /// <exception cref="InvalidCastException">The value is not a <see cref="CassieConnection" />.</exception>
    protected override DbConnection? DbConnection
    {
        get => Connection;
        set => Connection = value is null or CassieConnection
            ? (CassieConnection?)value
            : throw new InvalidCastException(
                $"A Cassie command requires a {nameof(CassieConnection)}, not {value.GetType().Name}.");
    }

    /// <inheritdoc />
    protected override DbParameterCollection DbParameterCollection => Parameters;

    /// <inheritdoc />
    /// <exception cref="InvalidCastException">The value is not a <see cref="CassieTransaction" />.</exception>
    protected override DbTransaction? DbTransaction
    {
        get => Transaction;
        set => Transaction = value is null or CassieTransaction
            ? (CassieTransaction?)value
            : throw new InvalidCastException(
                $"A Cassie command requires a {nameof(CassieTransaction)}, not {value.GetType().Name}.");
    }

    /// <summary>Resolves named text markers or builds a positional stored-procedure call.</summary>
    /// <returns>The SQL text, ordered parameter names, and ordered parameter values.</returns>
    /// <exception cref="InvalidOperationException">A text marker is not bound, or a binding is unused.</exception>
    internal CassiePreparedCommand PrepareParameters()
    {
        var prepared = CommandParameterBinder.Prepare(CommandText, CommandType, Parameters);
        ParameterizedQueryBatch.ValidateParameterCount(prepared);
        return prepared;
    }

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
        using var reader = (CassieDataReader)ExecuteDbDataReader(CommandBehavior.Default);
        do
        {
            while (reader.Read())
            {
            }
        } while (reader.NextResult());

        return reader.RecordsAffected;
    }

    /// <inheritdoc />
    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        var reader = (CassieDataReader)await ExecuteDbDataReaderAsync(CommandBehavior.Default, cancellationToken)
            .ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            do
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                }
            } while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

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

        if (Parameters.Count != 0)
        {
            throw new NotSupportedException("COPY commands cannot use bound parameters.");
        }

        if (DbConnection is not CassieConnection connection)
        {
            throw new InvalidOperationException("The command requires an open Cassie connection.");
        }

        connection.ValidateCommandTransaction(DbTransaction);
        return CassieDiagnostics.ObserveAsync("copy.from",
            () => connection.ExecuteCopyFromAsync(CommandText, source, CommandTimeout, cancellationToken),
            connection.Logger);
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

        if (Parameters.Count != 0)
        {
            throw new NotSupportedException("COPY commands cannot use bound parameters.");
        }

        if (DbConnection is not CassieConnection connection)
        {
            throw new InvalidOperationException("The command requires an open Cassie connection.");
        }

        connection.ValidateCommandTransaction(DbTransaction);
        return CassieDiagnostics.ObserveAsync("copy.to",
            () => connection.ExecuteCopyToAsync(CommandText, destination, CommandTimeout, cancellationToken),
            connection.Logger);
    }

    /// <inheritdoc />
    public override object? ExecuteScalar()
    {
        using var reader = (CassieDataReader)ExecuteDbDataReader(CommandBehavior.SingleRow);
        return reader.Read() && reader.FieldCount > 0 ? reader.GetValue(0) : null;
    }

    /// <inheritdoc />
    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        var reader = (CassieDataReader)await ExecuteDbDataReaderAsync(CommandBehavior.SingleRow, cancellationToken)
            .ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && reader.FieldCount > 0
                ? reader.GetValue(0)
                : null;
        }
    }

    /// <inheritdoc />
    public override void Prepare()
    {
        if (DbConnection is CassieConnection connection)
        {
            connection.RunSynchronously(() => PrepareAsync(CancellationToken.None));
        }
        else
        {
            SynchronousCall.Wait(PrepareAsync(CancellationToken.None));
        }
    }

    public override async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (CommandType is not (CommandType.Text or CommandType.StoredProcedure))
        {
            throw new NotSupportedException($"Cassie cannot prepare command type '{CommandType}'.");
        }


        var prepared = PrepareParameters();
        _preparedStatement.Request();

        if (DbConnection is CassieConnection connection && connection.State == ConnectionState.Open)
        {
            connection.ValidateCommandTransaction(DbTransaction);
            CassieConnection.ValidateSqlText(prepared.Sql);
            ParameterizedQueryBatch.ValidateParameterCount(prepared);
            var budget = connection.StartCommandBudget(CommandTimeout, cancellationToken);
            prepared = await connection.ResolveVectorTypesAsync(prepared, false, budget, cancellationToken)
                .ConfigureAwait(false);
            _ = await _preparedStatement.EnsurePreparedAsync(connection, prepared, budget, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _preparedStatement.Release();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        try
        {
            await _preparedStatement.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Creates a parameter for this command.</summary>
    public new CassieParameter CreateParameter()
    {
        return new CassieParameter();
    }

    /// <inheritdoc />
    protected override DbParameter CreateDbParameter()
    {
        return new CassieParameter();
    }

    /// <inheritdoc />
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        return DbConnection is CassieConnection connection
            ? connection.RunSynchronously(() => ExecuteDbDataReaderAsync(behavior, CancellationToken.None))
            : SynchronousCall.Wait(ExecuteDbDataReaderAsync(behavior, CancellationToken.None));
    }

    /// <inheritdoc />
    [SuppressMessage("Reliability", "CA2000",
        Justification = "The returned reader is transferred to the DbCommand caller, who owns disposal.")]
    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken
    )
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
        var operationScope = CassieDiagnostics.StartOperation("query");

        try
        {
            var prepared = PrepareParameters();
            CassieConnection.ValidateSqlText(prepared.Sql);
            ParameterizedQueryBatch.ValidateParameterCount(prepared);
            var schemaOnly = (behavior & CommandBehavior.SchemaOnly) != 0;
            var extended = prepared.ParameterNames.Count != 0 || BinaryResults || schemaOnly ||
                           _preparedStatement.IsRequested;
            var encoded = extended && !schemaOnly ? ParameterizedQueryBatch.EncodeParameters(prepared) : null;
            var budget = connection.StartCommandBudget(CommandTimeout, cancellationToken);

            if (extended)
            {
                prepared = await connection.ResolveVectorTypesAsync(prepared, BinaryResults || schemaOnly, budget,
                    cancellationToken).ConfigureAwait(false);
            }


            var statementName = _preparedStatement.IsRequested
                ? await _preparedStatement.EnsurePreparedAsync(connection, prepared, budget, cancellationToken)
                    .ConfigureAwait(false)
                : null;

            return await (extended
                ? connection.ExecuteParameterizedQueryAsync(prepared, budget, cancellationToken, behavior,
                    BinaryResults, statementName, operationScope, encoded)
                : connection.ExecuteQueryCoreAsync(prepared.Sql, budget, behavior, operationScope, true, cancellationToken))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            operationScope.Fail(exception, connection.Logger);
            operationScope.Dispose();
            throw;
        }
    }
}
