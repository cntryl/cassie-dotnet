using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Data;
using Cntryl.Cassie.Diagnostics;
using Cntryl.Cassie.Protocol;
using System.Data;
using System.Security.Cryptography;
using System.Text;

namespace Cntryl.Cassie.Connection
{
    public sealed partial class CassieConnection
    {
        internal async Task<CassieDataReader> ExecuteQueryAsync(string sql, CancellationToken cancellationToken, int commandTimeout = 0, CommandBehavior behavior = CommandBehavior.Default, CassieDiagnostics.OperationScope? operationScope = null)
        {
            ValidateSqlText(sql);
            PgSession session = RequireSession("The connection is not open.");
            ThrowIfReaderActive();
            await RefreshDateStyleIfNeededAsync(cancellationToken).ConfigureAwait(false);
            session.DateStyle.ObserveSql(sql);
            byte[] query = Encoding.UTF8.GetBytes(sql + "\0");
            return await RunReaderAsync(
                () => session.WriteFrameAsync(FrontendMessage.Query, query, cancellationToken),
                () => new CassieDataReader(new ConnectionReaderSession(this, session.Stream), commandTimeout, behavior, operationScope),
                cancelAfterInitialize: true,
                cancellationToken).ConfigureAwait(false);
        }

        internal async Task<CassieDataReader> ExecuteParameterizedQueryAsync(CassiePreparedCommand prepared, int commandTimeout, CancellationToken cancellationToken, CommandBehavior behavior = CommandBehavior.Default, bool binaryResults = false, string? statementName = null, CassieDiagnostics.OperationScope? operationScope = null, EncodedParameters? encodedParameters = null)
        {
            ValidateSqlText(prepared.Sql);
            PgSession session = RequireSession("The connection is not open.");
            ThrowIfReaderActive();
            bool schemaOnly = (behavior & CommandBehavior.SchemaOnly) != 0;
            if (!schemaOnly && prepared.ParameterNames.Count == 0 && !binaryResults && statementName is null)
            {
                return await ExecuteQueryAsync(prepared.Sql, cancellationToken, commandTimeout, behavior, operationScope).ConfigureAwait(false);
            }

            ParameterizedQueryBatch.ValidateParameterCount(prepared);

            using MemoryStream batch = new();
            try
            {
                // Local validation must finish before a refresh or execution touches the session.
                if (schemaOnly)
                {
                    ParameterizedQueryBatch.AppendSchemaRequest(batch, prepared, statementName);
                }
                else
                {
                    ParameterizedQueryBatch.AppendExecuteRequest(batch, prepared, statementName, binaryResults, _portalPageFrames, encodedParameters);
                }

                if (!binaryResults)
                {
                    await RefreshDateStyleIfNeededAsync(cancellationToken).ConfigureAwait(false);
                }

                session.DateStyle.ObserveSql(prepared.Sql);
                return await RunReaderAsync(
                    () => session.WriteBatchAsync(batch, cancellationToken),
                    () => schemaOnly
                        ? new CassieDataReader(new ConnectionReaderSession(this, session.Stream), commandTimeout, behavior, operationScope)
                        : new CassieDataReader(new ConnectionReaderSession(this, session.Stream), commandTimeout, behavior, operationScope, ExecutePortalPageAsync, SyncExtendedQueryAsync),
                    cancelAfterInitialize: false,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(batch.GetBuffer());
            }
        }

        internal async Task ExecuteControlQueryAsync(string sql, CancellationToken cancellationToken)
        {
            CassieDataReader reader = await ExecuteQueryAsync(sql, cancellationToken).ConfigureAwait(false);
            try
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    // Control statements return no rows of interest; reading to the end completes the response.
                }
            }
            finally
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }
        }

        private void ThrowIfReaderActive()
        {
            if (_gate.IsActive)
            {
                throw new InvalidOperationException("A reader is already active on this connection.");
            }
        }

        /// <summary>
        /// Sends one request and returns a reader positioned on its first result. The gate is held from the first
        /// byte written, so any failure after that point leaves the stream out of sync and aborts the transport.
        /// </summary>
        private async Task<CassieDataReader> RunReaderAsync(Func<Task> sendRequest, Func<CassieDataReader> createReader, bool cancelAfterInitialize, CancellationToken cancellationToken)
        {
            _gate.Begin();
            CassieDataReader? reader = null;
            bool initialized = false;
            try
            {
                await sendRequest().ConfigureAwait(false);
                reader = createReader();
                await reader.InitializeAsync(cancellationToken).ConfigureAwait(false);
                if (cancelAfterInitialize && cancellationToken.IsCancellationRequested)
                {
                    await reader.CancelAndDrainAsync().ConfigureAwait(false);
                    throw new OperationCanceledException("The query was canceled.", cancellationToken);
                }

                initialized = true;
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
                if (_gate.IsActive)
                {
                    AbortTransport();
                }

                throw;
            }
            finally
            {
                if (!initialized && reader is not null)
                {
                    await reader.StopCancellationAsync().ConfigureAwait(false);
                }
            }
        }
    }
}
