using Cntryl.Cassie.Rest;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Cntryl.Cassie.Tests.Rest
{
    public sealed class CassieRestClientContractTests
    {
        private static readonly Uri ServiceAddress = new("https://cassie.example/proxy/");

        [Fact]
        public async Task ShouldSerializeCollectionFieldsGivenTypedDefinitionWhenCollectionIsCreated()
        {
            // Arrange
            using ScriptedHttpHandler handler = new(async (request, cancellationToken) =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal(new Uri(ServiceAddress, "v1/collections"), request.RequestUri);
                Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
                Assert.Contains(request.Headers.Accept, value => value.MediaType == "application/json");
                JsonObject body = JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
                Assert.Equal("docs", body["name"]!.GetValue<string>());
                Assert.Equal("vector documents", body["description"]!.GetValue<string>());
                Assert.Equal("embedding", body["fields"]![1]!["name"]!.GetValue<string>());
                Assert.Equal("vector(3)", body["fields"]![1]!["type"]!.GetValue<string>());
                Assert.Null(body["fields"]![1]!["data_type"]);
                return ScriptedHttpHandler.Json(/*lang=json,strict*/ "{\"collection\":\"docs\"}");
            });
            using HttpClient http = new(handler) { BaseAddress = ServiceAddress };
            using CassieRestClient client = new(http);
            CassieCollectionDefinition definition = new("docs", [new("content", "text"), new("embedding", "vector(3)")], "vector documents");

            // Act
            string collection = await client.CreateCollectionAsync(definition, TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal("docs", collection);
            Assert.Equal(1, handler.Requests);
        }

        [Fact]
        public async Task ShouldSerializeStringIndexOptionsGivenQualifiedCollectionWhenVectorIndexIsCreated()
        {
            // Arrange
            using ScriptedHttpHandler handler = new(async (request, cancellationToken) =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("https://cassie.example/proxy/v1/collections/postgres.public.docs$archive/indexes", request.RequestUri!.OriginalString);
                JsonObject body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                Assert.Equal("embedding", body["field"]!.GetValue<string>());
                Assert.Equal("vector", body["kind"]!.GetValue<string>());
                Assert.Equal("content", body["options"]!["source_field"]!.GetValue<string>());
                Assert.Equal("l2", body["options"]!["metric"]!.GetValue<string>());
                Assert.Equal("2", body["options"]!["m"]!.GetValue<string>());
                return ScriptedHttpHandler.Json(/*lang=json,strict*/ """
                    {"collection":"postgres.public.docs$archive","field":"embedding","source_field":"content","provider":"local","model":"fixture","dimensions":3,"metric":"l2","index_type":"hnsw","status":"created"}
                    """);
            });
            using HttpClient http = new(handler) { BaseAddress = ServiceAddress };
            using CassieRestClient client = new(http);
            CassieVectorIndexDefinition definition = new("embedding", new Dictionary<string, string>
            {
                ["source_field"] = "content",
                ["metric"] = "l2",
                ["index_type"] = "hnsw",
                ["m"] = "2"
            });

            // Act
            CassieVectorIndexResult index = await client.CreateVectorIndexAsync("postgres.public.docs$archive", definition, TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal("content", index.SourceField);
            Assert.Equal(3, index.Dimensions);
            Assert.Equal("hnsw", index.IndexType);
            Assert.Equal("created", index.Status);
        }

        [Fact]
        public async Task ShouldPreserveRawDocumentPayloadGivenNestedJsonAndVectorWhenDocumentIsWrittenAndRead()
        {
            // Arrange
            const string payload = """{"payload":{"MixedKey":[1,true,null]},"embedding":[1.25,2.5,3.75]}""";
            int step = 0;
            using ScriptedHttpHandler handler = new(async (request, cancellationToken) =>
            {
                int current = Interlocked.Increment(ref step);
                if (current == 1)
                {
                    Assert.Equal(HttpMethod.Post, request.Method);
                    Assert.Equal(new Uri(ServiceAddress, "v1/collections/docs/documents"), request.RequestUri);
                    JsonObject body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                    Assert.Equal(1, body["payload"]!["MixedKey"]![0]!.GetValue<int>());
                    Assert.True(body["payload"]!["MixedKey"]![1]!.GetValue<bool>());
                    Assert.Null(body["payload"]!["MixedKey"]![2]);
                    Assert.Equal(3.75f, body["embedding"]![2]!.GetValue<float>());
                    Assert.Null(body["document"]);
                    return ScriptedHttpHandler.Json(/*lang=json,strict*/ "{\"id\":\"doc-1\"}");
                }

                Assert.Equal(new Uri(ServiceAddress, "v1/collections/docs/documents/doc-1"), request.RequestUri);
                Assert.Null(request.Content);
                if (current == 2)
                {
                    Assert.Equal(HttpMethod.Get, request.Method);
                    return ScriptedHttpHandler.Json(payload);
                }

                Assert.Equal(3, current);
                Assert.Equal(HttpMethod.Delete, request.Method);
                return ScriptedHttpHandler.Json(/*lang=json,strict*/ "{\"deleted\":true}");
            });
            using HttpClient http = new(handler) { BaseAddress = ServiceAddress };
            using CassieRestClient client = new(http);
            JsonObject document = JsonNode.Parse(payload)!.AsObject();

            // Act
            string id = await client.AddDocumentAsync("docs", document, TestContext.Current.CancellationToken);
            JsonObject loaded = await client.GetDocumentAsync("docs", id, TestContext.Current.CancellationToken);
            bool deleted = await client.DeleteDocumentAsync("docs", id, TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal("doc-1", id);
            Assert.True(deleted);
            Assert.True(JsonNode.DeepEquals(document, loaded));
            Assert.Equal(3, handler.Requests);
        }

        [Fact]
        public async Task ShouldPreserveColumnsAndJsonRowsGivenTextSearchWhenSearchCompletes()
        {
            // Arrange
            using ScriptedHttpHandler handler = new(async (request, cancellationToken) =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal(new Uri(ServiceAddress, "v1/collections/docs/search"), request.RequestUri);
                JsonObject body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                Assert.Equal("embedding", body["field"]!.GetValue<string>());
                Assert.Equal("query text", body["query"]!.GetValue<string>());
                Assert.Equal("l2", body["metric"]!.GetValue<string>());
                Assert.Equal(2, body["limit"]!.GetValue<int>());
                Assert.Equal(1, body["offset"]!.GetValue<int>());
                Assert.Null(body["vector"]);
                return ScriptedHttpHandler.Json(/*lang=json,strict*/ """
                    {"columns":[
                      {"name":"_id","data_type":"text","type_oid":25,"typlen":-1,"atttypmod":-1,"format_code":0,"nullable":false},
                      {"name":"id","data_type":"text","type_oid":25,"typlen":-1,"atttypmod":-1,"format_code":0,"nullable":true},
                      {"name":"payload","data_type":"json","type_oid":114,"typlen":-1,"atttypmod":-1,"format_code":0,"nullable":true},
                      {"name":"embedding","data_type":"vector(3)","type_oid":100003,"typlen":-1,"atttypmod":-1,"format_code":0,"nullable":true}
                    ],"rows":[["doc-2","public-id",{"MixedKey":[1,true,null]},[1.25,2.5,3.75]]],"command":"SELECT"}
                    """);
            });
            using HttpClient http = new(handler) { BaseAddress = ServiceAddress };
            using CassieRestClient client = new(http);

            // Act
            CassieRestQueryResult result = await client.SearchAsync("docs", new("embedding", "query text", "l2", Limit: 2, Offset: 1), TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal("SELECT", result.Command);
            Assert.Equal(4, result.Columns.Count);
            Assert.Equal("_id", result.Columns[0].Name);
            Assert.False(result.Columns[0].IsNullable);
            Assert.Equal((short)-1, result.Columns[0].TypeLength);
            Assert.Equal(-1, result.Columns[0].TypeModifier);
            Assert.True(result.Columns[2].IsNullable);
            Assert.Equal("vector(3)", result.Columns[3].DataType);
            Assert.Equal(100003, result.Columns[3].TypeOid);
            IReadOnlyList<JsonElement> row = Assert.Single(result.Rows);
            Assert.Equal(result.Columns.Count, row.Count);
            Assert.Equal("doc-2", row[0].GetString());
            Assert.Equal("public-id", row[1].GetString());
            Assert.True(row[2].GetProperty("MixedKey")[1].GetBoolean());
            Assert.Equal(JsonValueKind.Null, row[2].GetProperty("MixedKey")[2].ValueKind);
            Assert.Equal(3.75f, row[3][2].GetSingle());
        }

        [Fact]
        public async Task ShouldForwardZeroLimitGivenEmptySearchWhenSearchIsRequested()
        {
            // Arrange
            using ScriptedHttpHandler handler = new(async (request, cancellationToken) =>
            {
                JsonObject body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                Assert.Equal(0, body["limit"]!.GetValue<int>());
                return ScriptedHttpHandler.Json(/*lang=json,strict*/ """
                    {"columns":[{"name":"id","data_type":"text","type_oid":25,"typlen":-1,"atttypmod":-1,"format_code":0,"nullable":false},{"name":"embedding","data_type":"vector(3)","type_oid":100003,"typlen":-1,"atttypmod":-1,"format_code":0,"nullable":true}],"rows":[],"command":"SELECT"}
                    """);
            });
            using HttpClient http = new(handler) { BaseAddress = ServiceAddress };
            using CassieRestClient client = new(http);

            // Act
            CassieRestQueryResult result = await client.SearchAsync("docs", new("embedding", "query text", Limit: 0), TestContext.Current.CancellationToken);

            // Assert
            Assert.Empty(result.Rows);
            Assert.Equal(2, result.Columns.Count);
            Assert.Equal(1, handler.Requests);
        }

        [Fact]
        public async Task ShouldKeepBorrowedClientUsableGivenExistingSettingsWhenRestClientIsDisposed()
        {
            // Arrange
            using ScriptedHttpHandler handler = new((request, _) =>
            {
                Assert.Equal("kept", Assert.Single(request.Headers.GetValues("X-Caller")));
                return Task.FromResult(ScriptedHttpHandler.Json("[]"));
            });
            Uri originalBase = new("https://cassie.example/proxy");
            using HttpClient http = new(handler) { BaseAddress = originalBase, Timeout = TimeSpan.FromSeconds(7) };
            http.DefaultRequestHeaders.Add("X-Caller", "kept");
            using CassieRestClient client = new(http);
            _ = await client.ListCollectionsAsync(TestContext.Current.CancellationToken);

            // Act
            client.Dispose();
            using HttpResponseMessage response = await http.GetAsync(new Uri("probe", UriKind.Relative), TestContext.Current.CancellationToken);

            // Assert
            Assert.False(handler.IsDisposed);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(originalBase, http.BaseAddress);
            Assert.Equal(TimeSpan.FromSeconds(7), http.Timeout);
            Assert.Equal("kept", Assert.Single(http.DefaultRequestHeaders.GetValues("X-Caller")));
            Assert.Empty(http.DefaultRequestHeaders.Accept);
            Assert.Equal(2, handler.Requests);
        }

        [Theory]
        [InlineData("../escape")]
        [InlineData("docs/child")]
        [InlineData("docs\\child")]
        [InlineData("docs?database=other")]
        [InlineData("docs#fragment")]
        [InlineData("docs%24encoded")]
        [InlineData(".")]
        [InlineData("..")]
        public async Task ShouldRejectUnsafePathSegmentGivenAmbiguousCollectionWhenDocumentIsRequested(string collection)
        {
            // Arrange
            using ScriptedHttpHandler handler = new((_, _) => Task.FromResult(ScriptedHttpHandler.Json("{}")));
            using HttpClient http = new(handler) { BaseAddress = ServiceAddress };
            using CassieRestClient client = new(http);

            // Act
            Exception? error = await Record.ExceptionAsync(async () => _ = await client.GetDocumentAsync(collection, "doc-1", TestContext.Current.CancellationToken));

            // Assert
            _ = Assert.IsType<ArgumentException>(error, exactMatch: false);
            Assert.Equal(0, handler.Requests);
        }
    }
}
