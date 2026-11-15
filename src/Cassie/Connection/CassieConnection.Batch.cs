using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Data;
using Cntryl.Cassie.Diagnostics;
using Cntryl.Cassie.Protocol;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;

namespace Cntryl.Cassie.Connection
{
    public sealed partial class CassieConnection
    {
        private static readonly byte[] ExecuteAllFrame = ExtendedQueryMessages.ExecuteAllFrame();

        /// <inheritdoc />
        public override bool CanCreateBatch => true;

        /// <inheritdoc />
        protected override DbBatch CreateDbBatch()
        {
            return new CassieBatch { Connection = this, Timeout = DefaultCommandTimeout() };
        }

        /// <summary>
        /// Sends every command in one write (Parse, Bind, Describe, Execute per command, then one Sync) and returns a
        /// reader positioned on the first command's result. All parameters are encoded before the session is touched, so
        /// local validation failures (including parameter encoding, done by the caller before this method) leave the connection and its transaction untouched.
        /// </summary>
        internal async Task<CassieDataReader> ExecuteBatchAsync(IReadOnlyList<CassiePreparedCommand> commands, IReadOnlyList<EncodedParameters> encodedParameters, int commandTimeout, CommandBehavior behavior, CassieDiagnostics.OperationScope? operationScope, Action<CommandTag?> statementCompleted, CancellationToken cancellationToken)
        {
            PgSession session = RequireSession("The connection is not open.");
            ThrowIfReaderActive();

            using MemoryStream request = new();
            try
            {
                for (int index = 0; index < commands.Count; index++)
                {
                    ValidateSqlText(commands[index].Sql);
                    ParameterizedQueryBatch.AppendExecuteRequest(request, commands[index], statementName: null, binaryResults: false, ExecuteAllFrame, encodedParameters[index]);
                }

                PgWriter.AppendFrame(request, FrontendMessage.Sync, []);
                await RefreshDateStyleIfNeededAsync(cancellationToken).ConfigureAwait(false);
                foreach (CassiePreparedCommand prepared in commands)
                {
                    session.DateStyle.ObserveSql(prepared.Sql);
                }

                return await RunReaderAsync(
                    () => session.WriteBatchAsync(request, cancellationToken),
                    () => new CassieDataReader(new ConnectionReaderSession(this, session.Stream), commandTimeout, behavior, operationScope, statementCompleted: statementCompleted),
                    cancelAfterInitialize: false,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(request.GetBuffer());
            }
        }

        private int DefaultCommandTimeout()
        {
            return CommandTimeoutFrom(_connectionString);
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
}
