using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Data;
using Cntryl.Cassie.Diagnostics;
using Cntryl.Cassie.Protocol;

namespace Cntryl.Cassie.Connection;

public sealed partial class CassieConnection
{
    private static readonly byte[] ExecuteAllFrame = ExtendedQueryMessages.ExecuteAllFrame();

    /// <inheritdoc />
    public override bool CanCreateBatch => true;

    /// <summary>Creates a batch associated with this connection.</summary>
    public new CassieBatch CreateBatch()
    {
        return (CassieBatch)CreateDbBatch();
    }

    /// <inheritdoc />
    protected override DbBatch CreateDbBatch()
    {
        return new CassieBatch { Connection = this, Timeout = DefaultCommandTimeout() };
    }

    /// <summary>
    ///     Sends every command in one write (Parse unless the command is already prepared, then Bind, Describe, Execute; one
    ///     Sync at the end) and returns a
    ///     reader positioned on the first command's result. All parameters are encoded before the session is touched, so
    ///     local validation failures (including parameter encoding, done by the caller before this method) leave the
    ///     connection and its transaction untouched.
    /// </summary>
    internal async Task<CassieDataReader> ExecuteBatchAsync(
        IReadOnlyList<CassiePreparedCommand> commands,
        IReadOnlyList<EncodedParameters> encodedParameters,
        IReadOnlyList<string?> statementNames,
        CommandBudget budget,
        CommandBehavior behavior,
        CassieDiagnostics.OperationScope? operationScope,
        Action<CommandTag?> statementCompleted,
        Action<CassieException> statementFailed,
        CancellationToken cancellationToken
    )
    {
        var session = RequireSession("The connection is not open.");
        ThrowIfReaderActive();

        using MemoryStream request = new();

        try
        {
            for (var index = 0; index < commands.Count; index++)
            {
                ParameterizedQueryBatch.AppendExecuteRequest(request, commands[index], statementNames[index], false,
                    ExecuteAllFrame, encodedParameters[index]);
            }

            PgWriter.AppendFrame(request, FrontendMessage.Sync, []);
            await RefreshDateStyleIfNeededAsync(budget, cancellationToken).ConfigureAwait(false);

            foreach (var prepared in commands)
            {
                session.DateStyle.ObserveSql(prepared.Sql);
            }


            return await RunReaderAsync(
                token => session.WriteBatchAsync(request, token),
                () => new CassieDataReader(new ConnectionReaderSession(this, session), budget, behavior,
                    operationScope, statementCompleted: statementCompleted, statementFailed: statementFailed),
                budget,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(request.GetBuffer());
        }
    }

    private int DefaultCommandTimeout()
    {
        return _defaultCommandTimeout ??= CommandTimeoutFrom(_connectionString);
    }

    internal static int CommandTimeoutFrom(string connectionString)
    {
        try
        {
            return new CassieConnectionStringBuilder(connectionString).CommandTimeout;
        }
        catch (ArgumentException)
        {
            // An invalid connection string is reported when the connection opens.
            return 30;
        }
    }
}
