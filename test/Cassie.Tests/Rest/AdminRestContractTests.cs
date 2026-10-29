using Cntryl.Cassie.Rest;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Cntryl.Cassie.Tests.Rest
{
    public sealed class AdminRestContractTests
    {
        private static readonly Uri ServiceAddress = new("https://cassie.example/proxy/");
        private static readonly Guid OperationId = Guid.Parse("00000000-1111-2222-3333-444444444444");

        [Theory]
        [InlineData("livez")]
        [InlineData("readyz")]
        [InlineData("startupz")]
        [InlineData("targetz")]
        public async Task ShouldUseUnversionedProbePathGivenKubernetesProbeWhenSnapshotIsRequested(string endpoint)
        {
            // Arrange
            using ScriptedHttpHandler handler = new((request, _) =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal(new Uri(ServiceAddress, endpoint), request.RequestUri);
                return Task.FromResult(ScriptedHttpHandler.Json(/*lang=json,strict*/ """{"ready":true}"""));
            });
            using HttpClient http = new(handler) { BaseAddress = ServiceAddress };
            using CassieRestClient client = new(http);

            // Act
            System.Text.Json.Nodes.JsonObject snapshot = await (endpoint switch
            {
                "livez" => client.Admin.GetLivenessAsync(TestContext.Current.CancellationToken),
                "readyz" => client.Admin.GetReadinessAsync(TestContext.Current.CancellationToken),
                "startupz" => client.Admin.GetStartupAsync(TestContext.Current.CancellationToken),
                _ => client.Admin.GetTargetAsync(TestContext.Current.CancellationToken)
            });

            // Assert
            Assert.True(snapshot["ready"]!.GetValue<bool>());
            Assert.Equal(1, handler.Requests);
        }

        private const string ManifestJson = /*lang=json,strict*/ """
            {
              "manifest_version":1,"instance_id":"node-a","projection_id":"postgres.public.events",
              "projection_version_id":null,"projection_kind":"materialized","schema_epoch":18446744073709551615,
              "projection_definition_hash":18446744073709551615,"source_identity":null,"source_checkpoint":"offset-1",
              "source_position":18446744073709551615,"generated_ms":18446744073709551615,"expires_at_ms":18446744073709551615,
              "hash":{"algorithm":"blake3","digest_length":32,"canonical_encoder_version":1,"row_hash_version":1,"range_hash_version":1,"root_hash_version":1},
              "root":{"digest":"root-digest","row_count":18446744073709551615,"range_count":1,"state":"ready","computed_ms":18446744073709551615},
              "ranges":[{"range_id":18446744073709551615,"first_row_id":null,"last_row_id":"last","row_count":18446744073709551615,"digest":"range-digest","state":"ready","computed_ms":18446744073709551615}],
              "row_hashes":[{"row_id":"last","digest":"row-digest","state":"ready","computed_ms":18446744073709551615}],
              "manifest_digest":"manifest-digest"
            }
            """;

        private const string ReportJson = /*lang=json,strict*/ """
            {
              "report_id":"report-1","created_ms":18446744073709551615,"projection_id":"postgres.public.events",
              "projection_version_id":null,"state":"consistent","compatibility_status":"compatible",
              "manifest_count":18446744073709551615,"instance_ids":["node-a"],"root_digest":"root-digest","manifest_digest":null,
              "mismatch_count":18446744073709551615,"divergent_range_count":18446744073709551615,
              "divergent_row_count":18446744073709551615,"stale_manifest_count":18446744073709551615,
              "incompatible_manifest_count":18446744073709551615,"unverifiable_count":18446744073709551615,
              "diagnostic_sample":["comparison detail"],"last_error":null
            }
            """;

        [Fact]
        public async Task ShouldSendDatabaseAndOperationIdGivenSqlRequestWhenQueryIsExecuted()
        {
            // Arrange
            using ScriptedHttpHandler handler = new(async (request, cancellationToken) =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal(new Uri(ServiceAddress, "v1/admin/query/execute"), request.RequestUri);
                using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert.Equal("ledger", body.RootElement.GetProperty("database").GetString());
                Assert.Equal("SELECT 1", body.RootElement.GetProperty("sql").GetString());
                Assert.Equal(OperationId, body.RootElement.GetProperty("operation_id").GetGuid());
                return ScriptedHttpHandler.Json(/*lang=json,strict*/ """
                    {"columns":[{"name":"value","data_type":"numeric","type_oid":1700,"typlen":-1,"atttypmod":-1,"format_code":0,"nullable":true}],"rows":[[18446744073709551615],[null]],"command":"SELECT 2"}
                    """);
            });
            using HttpClient http = new(handler) { BaseAddress = ServiceAddress };
            using CassieRestClient client = new(http);

            // Act
            CassieRestQueryResult result = await client.Admin.ExecuteQueryAsync(new("ledger", "SELECT 1", OperationId), TestContext.Current.CancellationToken);

            // Assert
            CassieRestColumn column = Assert.Single(result.Columns);
            Assert.Equal(1700L, column.TypeOid);
            Assert.Equal((short)-1, column.TypeLength);
            Assert.Equal(-1, column.TypeModifier);
            Assert.True(column.IsNullable);
            Assert.Equal(ulong.MaxValue, result.Rows[0][0].GetUInt64());
            Assert.Equal(JsonValueKind.Null, result.Rows[1][0].ValueKind);
            Assert.Equal("SELECT 2", result.Command);
            Assert.Equal(1, handler.Requests);
        }

        [Fact]
        public async Task ShouldDescribeWithoutExecutingGivenValidationResponseWhenSqlIsValidated()
        {
            // Arrange
            using ScriptedHttpHandler handler = new((request, _) =>
            {
                Assert.Equal(new Uri(ServiceAddress, "v1/admin/query/validate"), request.RequestUri);
                return Task.FromResult(ScriptedHttpHandler.Json(/*lang=json,strict*/ """{"valid":true,"command":"CREATE TABLE"}"""));
            });
            using HttpClient http = new(handler) { BaseAddress = ServiceAddress };
            using CassieRestClient client = new(http);

            // Act
            CassieQueryValidationResult result = await client.Admin.ValidateQueryAsync(new("ledger", "CREATE TABLE example(id INT)"), TestContext.Current.CancellationToken);

            // Assert
            Assert.True(result.Valid);
            Assert.Equal("CREATE TABLE", result.Command);
            Assert.Null(result.Columns);
        }

        [Fact]
        public async Task ShouldPreserveVersionedPlanGivenNestedExplanationWhenSqlIsExplained()
        {
            // Arrange
            using ScriptedHttpHandler handler = new((request, _) =>
            {
                Assert.Equal(new Uri(ServiceAddress, "v1/admin/query/explain"), request.RequestUri);
                return Task.FromResult(ScriptedHttpHandler.Json(/*lang=json,strict*/ """
                    {"columns":[],"rows":[],"command":"EXPLAIN","plan":{"format_version":7,"nodes":[{"id":1,"unknown_extension":{"preserved":true}}],"analyze":null}}
                    """));
            });
            using HttpClient http = new(handler) { BaseAddress = ServiceAddress };
            using CassieRestClient client = new(http);

            // Act
            CassieQueryExplanation result = await client.Admin.ExplainQueryAsync(new("ledger", "SELECT 1"), TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(7, result.Plan["format_version"]!.GetValue<int>());
            Assert.True(result.Plan["nodes"]![0]!["unknown_extension"]!["preserved"]!.GetValue<bool>());
            Assert.Null(result.Plan["analyze"]);
            Assert.Empty(result.Rows);
        }

        [Fact]
        public async Task ShouldDecodeCatalogHierarchyGivenScopedDatabaseWhenSchemaIsRequested()
        {
            // Arrange
            using ScriptedHttpHandler handler = new((request, _) =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal(new Uri(ServiceAddress, "v1/admin/query/schema?database=ledger"), request.RequestUri);
                return Task.FromResult(ScriptedHttpHandler.Json(/*lang=json,strict*/ """
                    {"sections":[{"id":"tables","label":"Tables","items":[{"id":"ledger.public.events","kind":"table","label":"events","database":"ledger","schema":"public","name":"events","columns":[{"id":"id","name":"id","data_type":"int4","primary_key":true}]}]}]}
                    """));
            });
            using HttpClient http = new(handler) { BaseAddress = ServiceAddress };
            using CassieRestClient client = new(http);

            // Act
            CassieCatalogSchema result = await client.Admin.GetSchemaAsync("ledger", TestContext.Current.CancellationToken);

            // Assert
            CassieCatalogItem item = Assert.Single(Assert.Single(result.Sections).Items);
            Assert.Equal("ledger", item.Database);
            Assert.Equal("public", item.Schema);
            Assert.Null(item.Metadata);
            CassieCatalogColumn column = Assert.Single(item.Columns);
            Assert.True(column.PrimaryKey);
            Assert.Equal("int4", column.DataType);
        }

        [Fact]
        public async Task ShouldEscapeQueryValueGivenDatabaseWithUriMetacharactersWhenSchemaIsRequested()
        {
            // Arrange
            using ScriptedHttpHandler handler = new((request, _) =>
            {
                Assert.Equal("?database=ledger%26other%3Dvalue", request.RequestUri!.Query);
                return Task.FromResult(ScriptedHttpHandler.Json(/*lang=json,strict*/ """{"sections":[]}"""));
            });
            using HttpClient http = new(handler) { BaseAddress = ServiceAddress };
            using CassieRestClient client = new(http);

            // Act
            CassieCatalogSchema result = await client.Admin.GetSchemaAsync("ledger&other=value", TestContext.Current.CancellationToken);

            // Assert
            Assert.Empty(result.Sections);
            Assert.Equal(1, handler.Requests);
        }

        [Fact]
        public async Task ShouldHandleCreatedStatusGivenDatabaseNameWhenDatabaseIsCreatedAndListed()
        {
            // Arrange
            using ScriptedHttpHandler handler = new(async (request, cancellationToken) =>
            {
                Assert.Equal(new Uri(ServiceAddress, "v1/admin/databases"), request.RequestUri);
                if (request.Method == HttpMethod.Get)
                {
                    return ScriptedHttpHandler.Json(/*lang=json,strict*/ """[{"name":"ledger","description":"Read model"},{"name":"postgres"}]""");
                }

                Assert.Equal(HttpMethod.Post, request.Method);
                using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert.Equal("ledger", body.RootElement.GetProperty("name").GetString());
                return ScriptedHttpHandler.Json(/*lang=json,strict*/ """{"name":"ledger"}""", HttpStatusCode.Created);
            });
            using HttpClient http = new(handler) { BaseAddress = ServiceAddress };
            using CassieRestClient client = new(http);

            // Act
            CassieDatabaseInfo created = await client.Admin.CreateDatabaseAsync("ledger", TestContext.Current.CancellationToken);
            IReadOnlyList<CassieDatabaseInfo> databases = await client.Admin.ListDatabasesAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal("ledger", created.Name);
            Assert.Null(created.Description);
            Assert.Equal("Read model", databases[0].Description);
            Assert.Null(databases[1].Description);
            Assert.Equal(2, handler.Requests);
        }

        [Fact]
        public async Task ShouldUseOperationResourceGivenRegisteredOperationWhenQueryIsCanceled()
        {
            // Arrange
            using ScriptedHttpHandler handler = new((request, _) =>
            {
                Assert.Equal(HttpMethod.Delete, request.Method);
                Assert.Equal(new Uri(ServiceAddress, "v1/admin/query-operations/" + OperationId.ToString("D")), request.RequestUri);
                Assert.Null(request.Content);
                return Task.FromResult(ScriptedHttpHandler.Json(/*lang=json,strict*/ """{"operation_id":"00000000-1111-2222-3333-444444444444","cancelled":true}"""));
            });
            using HttpClient http = new(handler) { BaseAddress = ServiceAddress };
            using CassieRestClient client = new(http);

            // Act
            CassieQueryCancellation result = await client.Admin.CancelQueryAsync(OperationId, TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(OperationId, result.OperationId);
            Assert.True(result.Cancelled);
        }

        [Theory]
        [InlineData(HttpStatusCode.NotFound)]
        [InlineData(HttpStatusCode.Conflict)]
        [InlineData(HttpStatusCode.RequestTimeout)]
        public async Task ShouldPreserveCancellationStatusGivenServerRejectionWhenQueryCancellationFails(HttpStatusCode status)
        {
            // Arrange
            using ScriptedHttpHandler handler = new((_, _) => Task.FromResult(ScriptedHttpHandler.Json(/*lang=json,strict*/ """{"error":"query cancellation rejected"}""", status)));
            using HttpClient http = new(handler) { BaseAddress = ServiceAddress };
            using CassieRestClient client = new(http);

            // Act
            CassieRestException error = await Assert.ThrowsAsync<CassieRestException>(() => client.Admin.CancelQueryAsync(OperationId, TestContext.Current.CancellationToken));

            // Assert
            Assert.Equal(status, error.StatusCode);
            Assert.Equal("query cancellation rejected", error.Error);
            Assert.Equal(1, handler.Requests);
        }

        [Fact]
        public async Task ShouldRoundTripUnsignedManifestFieldsGivenExportedManifestWhenConsistencyIsCompared()
        {
            // Arrange
            using ScriptedHttpHandler handler = new(async (request, cancellationToken) =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                if (request.RequestUri == new Uri(ServiceAddress, "v1/admin/projections/postgres.public.events/verification-manifests"))
                {
                    Assert.Equal("node-a", body.RootElement.GetProperty("instance_id").GetString());
                    Assert.Equal(ulong.MaxValue, body.RootElement.GetProperty("generated_ms").GetUInt64());
                    Assert.Equal(ulong.MaxValue, body.RootElement.GetProperty("ttl_ms").GetUInt64());
                    Assert.True(body.RootElement.GetProperty("include_row_hashes").GetBoolean());
                    return ScriptedHttpHandler.Json(ManifestJson);
                }

                Assert.Equal(new Uri(ServiceAddress, "v1/admin/projection-consistency-checks"), request.RequestUri);
                JsonElement manifest = body.RootElement.GetProperty("manifests")[0];
                Assert.Equal(ulong.MaxValue, manifest.GetProperty("schema_epoch").GetUInt64());
                Assert.Equal(ulong.MaxValue, manifest.GetProperty("projection_definition_hash").GetUInt64());
                Assert.Equal(ulong.MaxValue, manifest.GetProperty("source_position").GetUInt64());
                Assert.Equal(ulong.MaxValue, manifest.GetProperty("root").GetProperty("row_count").GetUInt64());
                Assert.Equal(ulong.MaxValue, manifest.GetProperty("ranges")[0].GetProperty("range_id").GetUInt64());
                Assert.Equal(ulong.MaxValue, manifest.GetProperty("row_hashes")[0].GetProperty("computed_ms").GetUInt64());
                return ScriptedHttpHandler.Json(ReportJson);
            });
            using HttpClient http = new(handler) { BaseAddress = ServiceAddress };
            using CassieRestClient client = new(http);

            // Act
            CassieProjectionVerificationManifest manifest = await client.Admin.ExportVerificationManifestAsync("postgres.public.events", new("node-a", ulong.MaxValue, ulong.MaxValue, true), TestContext.Current.CancellationToken);
            CassieProjectionConsistencyReport report = await client.Admin.CompareVerificationManifestsAsync([manifest], TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal((ushort)32, manifest.Hash.DigestLength);
            Assert.Null(manifest.ProjectionVersionId);
            Assert.Null(manifest.SourceIdentity);
            Assert.Null(Assert.Single(manifest.Ranges).FirstRowId);
            Assert.Equal("row-digest", Assert.Single(manifest.RowHashes).Digest);
            Assert.Equal(ulong.MaxValue, report.CreatedMs);
            Assert.Equal(ulong.MaxValue, report.ManifestCount);
            Assert.Equal(ulong.MaxValue, report.MismatchCount);
            Assert.Equal(ulong.MaxValue, report.DivergentRangeCount);
            Assert.Equal(ulong.MaxValue, report.DivergentRowCount);
            Assert.Equal(ulong.MaxValue, report.StaleManifestCount);
            Assert.Equal(ulong.MaxValue, report.IncompatibleManifestCount);
            Assert.Equal(ulong.MaxValue, report.UnverifiableCount);
            Assert.Null(report.LastError);
            Assert.Equal(2, handler.Requests);
        }

        [Fact]
        public async Task ShouldDecodeReportsEnvelopeGivenNullableReportMetadataWhenReportsAreListed()
        {
            // Arrange
            using ScriptedHttpHandler handler = new((request, _) =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal(new Uri(ServiceAddress, "v1/admin/projection-consistency-reports"), request.RequestUri);
                return Task.FromResult(ScriptedHttpHandler.Json("{\"reports\":[" + ReportJson + "]}"));
            });
            using HttpClient http = new(handler) { BaseAddress = ServiceAddress };
            using CassieRestClient client = new(http);

            // Act
            IReadOnlyList<CassieProjectionConsistencyReport> reports = await client.Admin.ListProjectionConsistencyReportsAsync(TestContext.Current.CancellationToken);

            // Assert
            CassieProjectionConsistencyReport report = Assert.Single(reports);
            Assert.Equal("report-1", report.ReportId);
            Assert.Null(report.ManifestDigest);
            Assert.Null(report.ProjectionVersionId);
            Assert.Equal(["node-a"], report.InstanceIds);
            Assert.Equal(["comparison detail"], report.DiagnosticSample);
        }
    }
}
