using Cntryl.Cassie.Rest;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Cntryl.Cassie.Tests.Rest
{
    public sealed class CassieRestErrorContractTests
    {
        [Theory]
        [InlineData(400)]
        [InlineData(401)]
        [InlineData(403)]
        [InlineData(404)]
        [InlineData(409)]
        [InlineData(413)]
        [InlineData(429)]
        [InlineData(499)]
        [InlineData(501)]
        [InlineData(503)]
        [InlineData(504)]
        public async Task ShouldPreserveHttpFailureGivenErrorEnvelopeWhenMutatingRequestIsRejected(int status)
        {
            // Arrange
            using ScriptedHttpHandler handler = new(async (request, cancellationToken) =>
            {
                JsonObject body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                Assert.Equal("write once", body["content"]!.GetValue<string>());
                HttpResponseMessage response = ScriptedHttpHandler.Json(/*lang=json,strict*/ "{\"error\":\"server rejected request\"}", (HttpStatusCode)status);
                if (status == 429)
                {
                    response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
                }

                return response;
            });
            using HttpClient http = new(handler) { BaseAddress = new Uri("https://cassie.example/") };
            using CassieRestClient client = new(http);
            JsonObject document = new() { ["content"] = "write once" };

            // Act
            CassieRestException error = await Assert.ThrowsAsync<CassieRestException>(async () => _ = await client.AddDocumentAsync("docs", document, TestContext.Current.CancellationToken));

            // Assert
            Assert.Equal((HttpStatusCode)status, error.StatusCode);
            Assert.Equal("server rejected request", error.Error);
            Assert.Equal(1, handler.Requests);
        }

        [Theory]
        [InlineData("")]
        [InlineData("upstream unavailable")]
        [InlineData("<html><body>bad gateway</body></html>")]
        [InlineData("{not valid json")]
        [InlineData(/*lang=json,strict*/ "{\"message\":\"different envelope\"}")]
        [InlineData(/*lang=json,strict*/ "{\"error\":17}")]
        public async Task ShouldRetainHttpStatusGivenUnexpectedErrorBodyWhenRequestFails(string body)
        {
            // Arrange
            using ScriptedHttpHandler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/plain")
            }));
            using HttpClient http = new(handler) { BaseAddress = new Uri("https://cassie.example/") };
            using CassieRestClient client = new(http);

            // Act
            CassieRestException error = await Assert.ThrowsAsync<CassieRestException>(async () => _ = await client.ListCollectionsAsync(TestContext.Current.CancellationToken));

            // Assert
            Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
            Assert.False(string.IsNullOrWhiteSpace(error.Error));
            Assert.Equal(1, handler.Requests);
        }

        [Theory]
        [InlineData("")]
        [InlineData("[not valid json")]
        [InlineData(/*lang=json,strict*/ "{\"collections\":[]}")]
        public async Task ShouldRejectMalformedSuccessGivenInvalidJsonRepresentationWhenCollectionsAreRead(string body)
        {
            // Arrange
            using ScriptedHttpHandler handler = new((_, _) => Task.FromResult(ScriptedHttpHandler.Json(body)));
            using HttpClient http = new(handler) { BaseAddress = new Uri("https://cassie.example/") };
            using CassieRestClient client = new(http);

            // Act
            Exception? error = await Record.ExceptionAsync(async () => _ = await client.ListCollectionsAsync(TestContext.Current.CancellationToken));

            // Assert
            _ = Assert.IsType<JsonException>(error, exactMatch: false);
            Assert.Equal(1, handler.Requests);
        }

        [Fact]
        public async Task ShouldKeepDimensionFailureAsServerErrorGivenWrongLengthVectorWhenDocumentIsWritten()
        {
            // Arrange
            using ScriptedHttpHandler handler = new(async (request, cancellationToken) =>
            {
                JsonObject document = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                Assert.Equal(2, document["embedding"]!.AsArray().Count);
                return ScriptedHttpHandler.Json(/*lang=json,strict*/ "{\"error\":\"vector dimension mismatch: expected 3, got 2\"}", HttpStatusCode.BadRequest);
            });
            using HttpClient http = new(handler) { BaseAddress = new Uri("https://cassie.example/") };
            using CassieRestClient client = new(http);

            // Act
            CassieRestException error = await Assert.ThrowsAsync<CassieRestException>(async () => _ = await client.AddDocumentAsync("docs", new JsonObject { ["embedding"] = new JsonArray(1, 2) }, TestContext.Current.CancellationToken));

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
            Assert.Contains("dimension mismatch", error.Error, StringComparison.Ordinal);
            Assert.Equal(1, handler.Requests);
        }
    }
}
