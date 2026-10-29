using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Cntryl.Cassie.Rest
{
    /// <summary>Accesses Cassie's administration API using the owning REST client's session.</summary>
    public sealed class CassieAdminClient
    {
        private readonly CassieRestClient _client;

        internal CassieAdminClient(CassieRestClient client)
        {
            _client = client;
        }

        /// <summary>Lists databases visible to the authenticated role.</summary>
        public async Task<IReadOnlyList<CassieDatabaseInfo>> ListDatabasesAsync(CancellationToken cancellationToken = default)
        {
            return await _client.SendAsync(HttpMethod.Get, "v1/admin/databases", null, CassieRestJsonContext.Default.CassieDatabaseInfoArray, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Creates a logical database.</summary>
        public Task<CassieDatabaseInfo> CreateDatabaseAsync(string name, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            return _client.SendAsync(HttpMethod.Post, "v1/admin/databases",
                CassieRestClient.CreateJsonContent(new CassieDatabaseCreateRequest(name), CassieRestJsonContext.Default.CassieDatabaseCreateRequest),
                CassieRestJsonContext.Default.CassieDatabaseInfo, cancellationToken);
        }

        /// <summary>Gets the catalog for an explicitly selected database.</summary>
        public Task<CassieCatalogSchema> GetSchemaAsync(string database, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(database);
            return _client.SendAsync(HttpMethod.Get, "v1/admin/query/schema?database=" + Uri.EscapeDataString(database), null,
                CassieRestJsonContext.Default.CassieCatalogSchema, cancellationToken);
        }

        /// <summary>Executes SQL in the requested database; an operation ID permits explicit server cancellation.</summary>
        public Task<CassieRestQueryResult> ExecuteQueryAsync(CassieAdminQueryRequest request, CancellationToken cancellationToken = default)
        {
            return SendQueryAsync("execute", request, CassieRestJsonContext.Default.CassieRestQueryResult, cancellationToken);
        }

        /// <summary>Validates SQL without executing it; invalid SQL is reported as a REST error.</summary>
        public Task<CassieQueryValidationResult> ValidateQueryAsync(CassieAdminQueryRequest request, CancellationToken cancellationToken = default)
        {
            return SendQueryAsync("validate", request, CassieRestJsonContext.Default.CassieQueryValidationResult, cancellationToken);
        }

        /// <summary>Explains SQL, preserving the server's versioned plan object.</summary>
        public Task<CassieQueryExplanation> ExplainQueryAsync(CassieAdminQueryRequest request, CancellationToken cancellationToken = default)
        {
            return SendQueryAsync("explain", request, CassieRestJsonContext.Default.CassieQueryExplanation, cancellationToken);
        }

        /// <summary>Requests and waits for server cancellation of an operation owned by this REST session.</summary>
        /// <remarks>This is separate from canceling a local HTTP request through its cancellation token.</remarks>
        public Task<CassieQueryCancellation> CancelQueryAsync(Guid operationId, CancellationToken cancellationToken = default)
        {
            return _client.SendAsync(HttpMethod.Delete, "v1/admin/query-operations/" + operationId.ToString("D"), null,
                CassieRestJsonContext.Default.CassieQueryCancellation, cancellationToken);
        }

        /// <summary>Exports a projection verification manifest from the current server.</summary>
        public Task<CassieProjectionVerificationManifest> ExportVerificationManifestAsync(string projection, CassieManifestExportOptions? options = null, CancellationToken cancellationToken = default)
        {
            string path = "v1/admin/projections/" + CassieRestClient.EscapePathSegment(projection, nameof(projection)) + "/verification-manifests";
            return _client.SendAsync(HttpMethod.Post, path,
                CassieRestClient.CreateJsonContent(options ?? new CassieManifestExportOptions(), CassieRestJsonContext.Default.CassieManifestExportOptions),
                CassieRestJsonContext.Default.CassieProjectionVerificationManifest, cancellationToken);
        }

        /// <summary>Compares exported verification manifests and records the consistency report.</summary>
        public Task<CassieProjectionConsistencyReport> CompareVerificationManifestsAsync(IReadOnlyList<CassieProjectionVerificationManifest> manifests, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(manifests);
            return _client.SendAsync(HttpMethod.Post, "v1/admin/projection-consistency-checks",
                CassieRestClient.CreateJsonContent(new CassieManifestComparisonRequest(manifests), CassieRestJsonContext.Default.CassieManifestComparisonRequest),
                CassieRestJsonContext.Default.CassieProjectionConsistencyReport, cancellationToken);
        }

        /// <summary>Lists the latest consistency report for each projection.</summary>
        public async Task<IReadOnlyList<CassieProjectionConsistencyReport>> ListProjectionConsistencyReportsAsync(CancellationToken cancellationToken = default)
        {
            CassieProjectionReportsResult result = await _client.SendAsync(HttpMethod.Get, "v1/admin/projection-consistency-reports", null,
                CassieRestJsonContext.Default.CassieProjectionReportsResult, cancellationToken).ConfigureAwait(false);
            return result.Reports;
        }

        /// <summary>Gets the server's readiness snapshot.</summary>
        public Task<JsonObject> GetReadinessAsync(CancellationToken cancellationToken = default)
        {
            return GetSnapshotAsync("readyz", cancellationToken);
        }

        /// <summary>Gets the server's startup snapshot.</summary>
        public Task<JsonObject> GetStartupAsync(CancellationToken cancellationToken = default)
        {
            return GetSnapshotAsync("startupz", cancellationToken);
        }

        /// <summary>Gets the server's process liveness snapshot.</summary>
        public Task<JsonObject> GetLivenessAsync(CancellationToken cancellationToken = default)
        {
            return GetSnapshotAsync("livez", cancellationToken);
        }

        /// <summary>Gets the server's capability and target snapshot.</summary>
        public Task<JsonObject> GetTargetAsync(CancellationToken cancellationToken = default)
        {
            return GetSnapshotAsync("targetz", cancellationToken);
        }

        /// <summary>Gets the server's structured metrics snapshot.</summary>
        public Task<JsonObject> GetMetricsAsync(CancellationToken cancellationToken = default)
        {
            return GetSnapshotAsync("metrics", cancellationToken);
        }

        private Task<JsonObject> GetSnapshotAsync(string path, CancellationToken cancellationToken)
        {
            return _client.SendAsync(HttpMethod.Get, path, null, CassieRestJsonContext.Default.JsonObject, cancellationToken);
        }

        private Task<T> SendQueryAsync<T>(string operation, CassieAdminQueryRequest request, JsonTypeInfo<T> responseType, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.Database);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.Sql);
            return _client.SendAsync(HttpMethod.Post, "v1/admin/query/" + operation,
                CassieRestClient.CreateJsonContent(request, CassieRestJsonContext.Default.CassieAdminQueryRequest), responseType, cancellationToken);
        }
    }
}
