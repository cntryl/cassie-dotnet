using System.Net;
using System.Text.Json.Nodes;
using Cntryl.Cassie.Rest;
using Xunit;

namespace Cntryl.Cassie.Tests.Live;

[Collection(LiveEndpoint.CollectionName)]
public sealed class LiveRestContractTests
{
    [Fact]
    public async Task ShouldSearchAndManageDocumentsGivenVectorIndexWhenRestWorkflowRuns()
    {
        // Arrange
        await using var live = await LiveRestSession.OpenAsync();
        var collection = await live.CreateCollectionAsync([
            new CassieRestField("id", "INT4"), new CassieRestField("content", "TEXT"),
            new CassieRestField("embedding", "VECTOR(3)")
        ]);
        CassieVectorIndexDefinition definition = new("embedding", new Dictionary<string, string>
        {
            ["source_field"] = "content",
            ["metric"] = "cosine"
        });
        var index = await live.Client.CreateVectorIndexAsync(collection, definition,
            TestContext.Current.CancellationToken);
        var generated = await live.Client.AddDocumentAsync(collection,
            new JsonObject { ["id"] = 1, ["content"] = "The first vector document contains useful text" },
            TestContext.Current.CancellationToken);
        var explicitVector = await live.Client.AddDocumentAsync(collection,
            new JsonObject { ["id"] = 2, ["content"] = "The second document", ["embedding"] = new JsonArray(1, 0, 0) },
            TestContext.Current.CancellationToken);
        _ = await live.Client.AddDocumentAsync(collection, new JsonObject { ["id"] = 3 },
            TestContext.Current.CancellationToken);

        // Act
        var results = await live.Client.SearchAsync(collection,
            new CassieVectorSearchRequest("embedding", "Search useful document text", "cosine", 10),
            TestContext.Current.CancellationToken);
        var page = await live.Client.SearchAsync(collection,
            new CassieVectorSearchRequest("embedding", "Search useful document text", "cosine", 1, 1),
            TestContext.Current.CancellationToken);
        var empty = await live.Client.SearchAsync(collection,
            new CassieVectorSearchRequest("embedding", "Search useful document text", "cosine", 0),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("local", index.Provider);
        Assert.Equal(3, index.Dimensions);
        Assert.Equal("created", index.Status);
        Assert.Equal("bruteforce", index.IndexType);
        Assert.Equal("exists",
            (await live.Client.CreateVectorIndexAsync(collection, definition, TestContext.Current.CancellationToken))
            .Status);
        Assert.Contains("postgres.public." + collection,
            await live.Client.ListCollectionsAsync(TestContext.Current.CancellationToken));
        Assert.Equal("SELECT", results.Command);
        Assert.Equal("_id", results.Columns[0].Name);
        Assert.Equal(2, results.Rows.Count);
        Assert.Equal(new[] { generated, explicitVector }.Order(StringComparer.Ordinal),
            results.Rows.Select(row => row[0].GetString()).Order(StringComparer.Ordinal));
        Assert.Equal(results.Rows[1][0].GetString(), Assert.Single(page.Rows)[0].GetString());
        Assert.Empty(empty.Rows);
        var document = await live.Client.GetDocumentAsync(collection, generated, TestContext.Current.CancellationToken);
        Assert.Equal(1, document["id"]!.GetValue<int>());
        Assert.True(
            await live.Client.DeleteDocumentAsync(collection, generated, TestContext.Current.CancellationToken));
        Assert.False(
            await live.Client.DeleteDocumentAsync(collection, generated, TestContext.Current.CancellationToken));
        var missing = await Assert.ThrowsAsync<CassieRestException>(() =>
            live.Client.GetDocumentAsync(collection, generated, TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task ShouldRejectInvalidVectorGivenWrongDimensionsWhenDocumentIsAdded()
    {
        // Arrange
        await using var live = await LiveRestSession.OpenAsync();
        var collection = await live.CreateCollectionAsync([new CassieRestField("embedding", "VECTOR(3)")]);

        // Act
        var error = await Assert.ThrowsAsync<CassieRestException>(() => live.Client.AddDocumentAsync(collection,
            new JsonObject { ["embedding"] = new JsonArray(1, 2) }, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.NotEmpty(error.Error);
    }

    [Fact]
    public async Task ShouldExecuteDescribeAndExplainGivenExplicitDatabaseWhenAdminApiIsUsed()
    {
        // Arrange
        await using var live = await LiveRestSession.OpenAsync();
        var collection = await live.CreateCollectionAsync([new CassieRestField("answer", "INT4")]);
        CassieAdminQueryRequest request = new("postgres", "SELECT 42 AS answer", Guid.NewGuid());

        // Act
        var result = await live.Client.Admin.ExecuteQueryAsync(request, TestContext.Current.CancellationToken);
        var validation = await live.Client.Admin.ValidateQueryAsync(request with { OperationId = Guid.NewGuid() },
            TestContext.Current.CancellationToken);
        var explanation = await live.Client.Admin.ExplainQueryAsync(request with { OperationId = Guid.NewGuid() },
            TestContext.Current.CancellationToken);
        var schema = await live.Client.Admin.GetSchemaAsync("postgres", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(42, Assert.Single(result.Rows)[0].GetInt32());
        Assert.Equal("answer", Assert.Single(result.Columns).Name);
        Assert.True(validation.Valid);
        Assert.Equal("answer", Assert.Single(validation.Columns!).Name);
        Assert.NotEmpty(explanation.Plan);
        Assert.Contains(schema.Sections.SelectMany(section => section.Items), item => item.Name == collection);
        Assert.Contains(await live.Client.Admin.ListDatabasesAsync(TestContext.Current.CancellationToken),
            database => database.Name == "postgres");
        var invalid = await Assert.ThrowsAsync<CassieRestException>(() =>
            live.Client.Admin.ValidateQueryAsync(new CassieAdminQueryRequest("postgres", "SELECT FROM"),
                TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.NotEmpty(await live.Client.Admin.GetReadinessAsync(TestContext.Current.CancellationToken));
        Assert.NotEmpty(await live.Client.Admin.GetStartupAsync(TestContext.Current.CancellationToken));
        Assert.NotEmpty(await live.Client.Admin.GetLivenessAsync(TestContext.Current.CancellationToken));
        Assert.NotEmpty(await live.Client.Admin.GetTargetAsync(TestContext.Current.CancellationToken));
        Assert.NotEmpty(await live.Client.Admin.GetMetricsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ShouldCreateAndSelectDatabaseGivenAdminSessionWhenDatabaseApiIsUsed()
    {
        // Arrange
        await using var live = await LiveRestSession.OpenAsync();

        // Act
        var created = await live.CreateDatabaseAsync();
        var name = created.Name;
        var result = await live.Client.Admin.ExecuteQueryAsync(
            new CassieAdminQueryRequest(name, "SELECT current_database()"), TestContext.Current.CancellationToken);

        // Assert
        Assert.StartsWith("rest_database", name, StringComparison.Ordinal);
        Assert.Equal(name, Assert.Single(result.Rows)[0].GetString());
        Assert.Contains(await live.Client.Admin.ListDatabasesAsync(TestContext.Current.CancellationToken),
            database => database.Name == name);
    }

    [Fact]
    public async Task ShouldCancelServerQueryGivenOperationIdWhenExplicitCancellationIsRequested()
    {
        // Arrange
        await using var live = await LiveRestSession.OpenAsync();
        var operation = Guid.NewGuid();
        var pending = live.Client.Admin.ExecuteQueryAsync(
            new CassieAdminQueryRequest("postgres", "SELECT pg_sleep(10)", operation),
            TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));

        // Act
        CassieQueryCancellation cancelled;

        while (true)
        {
            try
            {
                cancelled = await live.Client.Admin.CancelQueryAsync(operation, timeout.Token);
                break;
            }
            catch (CassieRestException exception) when (exception.StatusCode == HttpStatusCode.NotFound &&
                                                        !pending.IsCompleted)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
            }
        }


        var error = await Assert.ThrowsAsync<CassieRestException>(() => pending);

        // Assert
        Assert.True(cancelled.Cancelled);
        Assert.Equal(operation, cancelled.OperationId);
        Assert.Equal(499, (int)error.StatusCode!);
        var followup = await live.Client.Admin.ExecuteQueryAsync(new CassieAdminQueryRequest("postgres", "SELECT 7"),
            TestContext.Current.CancellationToken);
        Assert.Equal(7, Assert.Single(followup.Rows)[0].GetInt32());
    }

    [Fact]
    public async Task ShouldExportCompareAndListGivenCollectionManifestsWhenConsistencyApiIsUsed()
    {
        // Arrange
        await using var live = await LiveRestSession.OpenAsync();
        var collection = await live.CreateCollectionAsync([new CassieRestField("title", "TEXT")]);
        _ = await live.Client.AddDocumentAsync(collection, new JsonObject { ["title"] = "A consistent document" },
            TestContext.Current.CancellationToken);
        var projection = "postgres.public." + collection;
        var first = await live.Client.Admin.ExportVerificationManifestAsync(projection,
            new CassieManifestExportOptions("first", IncludeRowHashes: true), TestContext.Current.CancellationToken);
        var second = await live.Client.Admin.ExportVerificationManifestAsync(projection,
            new CassieManifestExportOptions("second", IncludeRowHashes: true), TestContext.Current.CancellationToken);

        // Act
        var report =
            await live.Client.Admin.CompareVerificationManifestsAsync([first, second],
                TestContext.Current.CancellationToken);
        var reports =
            await live.Client.Admin.ListProjectionConsistencyReportsAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(projection, first.ProjectionId);
        Assert.NotEmpty(first.ManifestDigest);
        _ = Assert.Single(first.RowHashes);
        Assert.Equal("consistent", report.State);
        Assert.Equal(2UL, report.ManifestCount);
        Assert.Equal(0UL, report.MismatchCount);
        Assert.Contains(reports, item => item.ReportId == report.ReportId);
    }
}
