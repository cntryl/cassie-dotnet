using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Cntryl.Cassie.Rest;

/// <summary>Accesses Cassie's authenticated REST collection, document, vector, and administration APIs.</summary>
public sealed class CassieRestClient : IDisposable
{
    private readonly Uri _baseAddress;
    private readonly HttpClient _httpClient;
    private readonly HttpClientHandler? _ownedHandler;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    /// <summary>Creates a client with its own HTTP connection pool and session-cookie container.</summary>
    public CassieRestClient(Uri baseAddress)
    {
        _baseAddress = NormalizeBaseAddress(baseAddress);
        _ownedHandler = new HttpClientHandler
        { UseCookies = true, CookieContainer = new CookieContainer(), CheckCertificateRevocationList = true };

        try
        {
            _httpClient = new HttpClient(_ownedHandler, false);
        }
        catch
        {
            _ownedHandler.Dispose();
            throw;
        }


        _ownsHttpClient = true;
        Admin = new CassieAdminClient(this);
    }

    /// <summary>Creates a client that borrows an HTTP client with an absolute HTTP(S) BaseAddress.</summary>
    /// <remarks>
    ///     The caller owns the HTTP client and must configure session-cookie handling for login. Avoid sharing its cookie
    ///     container between unrelated sessions.
    /// </remarks>
    public CassieRestClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _baseAddress = NormalizeBaseAddress(httpClient.BaseAddress ??
                                            throw new ArgumentException("The HTTP client requires a BaseAddress.",
                                                nameof(httpClient)));
        _httpClient = httpClient;
        Admin = new CassieAdminClient(this);
    }

    /// <summary>Gets administration operations using this client's transport and authenticated session.</summary>
    public CassieAdminClient Admin { get; }

    /// <summary>Disposes the transport created by the URI constructor; a borrowed HTTP client remains usable.</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;

            if (_ownsHttpClient)
            {
                try
                {
                    _httpClient.Dispose();
                }
                finally
                {
                    _ownedHandler?.Dispose();
                }
            }
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Authenticates and receives the server-owned session cookie through the HTTP handler.</summary>
    public Task<CassieRestSession> LoginAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentNullException.ThrowIfNull(password);
        return SendAsync(HttpMethod.Post, "api/v1/auth/login",
            CreateJsonContent(new RestLoginRequest(username, password), CassieRestJsonContext.Default.RestLoginRequest),
            CassieRestJsonContext.Default.CassieRestSession, cancellationToken);
    }

    /// <summary>Gets the authenticated session's user and role.</summary>
    public Task<CassieRestSession> GetSessionAsync(CancellationToken cancellationToken = default)
    {
        return SendAsync(HttpMethod.Get, "api/v1/auth/session", null, CassieRestJsonContext.Default.CassieRestSession,
            cancellationToken);
    }

    /// <summary>Revokes the current server session and clears its cookie.</summary>
    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(HttpMethod.Post, "api/v1/auth/logout", null,
            CassieRestJsonContext.Default.RestLogoutResponse, cancellationToken).ConfigureAwait(false);
        _ = response.LoggedOut ?? throw new JsonException("Cassie returned no logout acknowledgement.");
    }

    /// <summary>Lists the collection names visible through the REST API.</summary>
    public async Task<IReadOnlyList<string>> ListCollectionsAsync(CancellationToken cancellationToken = default)
    {
        return await SendAsync(HttpMethod.Get, "api/v1/collections", null, CassieRestJsonContext.Default.StringArray,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates a collection and returns its server-reported name.</summary>
    public async Task<string> CreateCollectionAsync(
        CassieCollectionDefinition definition,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(definition);
        var response = await SendAsync(HttpMethod.Post, "api/v1/collections",
            CreateJsonContent(definition, CassieRestJsonContext.Default.CassieCollectionDefinition),
            CassieRestJsonContext.Default.RestCollectionResponse, cancellationToken).ConfigureAwait(false);
        return response.Collection ?? throw new JsonException("Cassie returned no collection name.");
    }

    /// <summary>Creates a vector index and returns its server-reported metadata.</summary>
    public Task<CassieVectorIndexResult> CreateVectorIndexAsync(
        string collection,
        CassieVectorIndexDefinition definition,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(definition);
        return SendAsync(HttpMethod.Post, CollectionPath(collection) + "/indexes",
            CreateJsonContent(definition, CassieRestJsonContext.Default.CassieVectorIndexDefinition),
            CassieRestJsonContext.Default.CassieVectorIndexResult, cancellationToken);
    }

    /// <summary>Inserts a JSON document and returns its server-generated identifier.</summary>
    public async Task<string> AddDocumentAsync(
        string collection,
        JsonObject document,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(document);
        var response = await SendAsync(HttpMethod.Post, CollectionPath(collection) + "/documents",
            CreateJsonContent(document, CassieRestJsonContext.Default.JsonObject),
            CassieRestJsonContext.Default.RestDocumentResponse, cancellationToken).ConfigureAwait(false);
        return response.Id ?? throw new JsonException("Cassie returned no document identifier.");
    }

    /// <summary>Retrieves a JSON document by its identifier.</summary>
    public Task<JsonObject> GetDocumentAsync(
        string collection,
        string id,
        CancellationToken cancellationToken = default
    )
    {
        return SendAsync(HttpMethod.Get, DocumentPath(collection, id), null, CassieRestJsonContext.Default.JsonObject,
            cancellationToken);
    }

    /// <summary>Deletes a document and returns the server's deletion acknowledgement.</summary>
    public async Task<bool> DeleteDocumentAsync(
        string collection,
        string id,
        CancellationToken cancellationToken = default
    )
    {
        var response = await SendAsync(HttpMethod.Delete, DocumentPath(collection, id), null,
            CassieRestJsonContext.Default.RestDeleteResponse, cancellationToken).ConfigureAwait(false);
        return response.Deleted ?? throw new JsonException("Cassie returned no deletion acknowledgement.");
    }

    /// <summary>Searches using the index's embedding provider and preserves the returned column metadata and JSON values.</summary>
    public Task<CassieRestQueryResult> SearchAsync(
        string collection,
        CassieVectorSearchRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync(HttpMethod.Post, CollectionPath(collection) + "/search",
            CreateJsonContent(request, CassieRestJsonContext.Default.CassieVectorSearchRequest),
            CassieRestJsonContext.Default.CassieRestQueryResult, cancellationToken);
    }

    internal static JsonContent CreateJsonContent<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        return JsonContent.Create(value, typeInfo);
    }

    internal async Task<T> SendAsync<T>(
        HttpMethod method,
        string path,
        HttpContent? content,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken
    )
    {
        using HttpRequestMessage request = new(method, new Uri(_baseAddress, path)) { Content = content };
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await SendRequestAsync(request, typeInfo, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> SendRequestAsync<T>(
        HttpRequestMessage request,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken
    )
    {
        var timeout = _httpClient.Timeout;
        using var deadline = timeout == Timeout.InfiniteTimeSpan
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline?.CancelAfter(timeout);
        var token = deadline?.Token ?? cancellationToken;

        try
        {
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            var limit = _httpClient.MaxResponseContentBufferSize;
            return response.IsSuccessStatusCode
                ? await ReadSuccessAsync(response.Content, typeInfo, limit, token).ConfigureAwait(false)
                  ?? throw new JsonException("Cassie returned a null response.")
                : throw new CassieRestException(response.StatusCode,
                    await ReadErrorAsync(response, limit, token).ConfigureAwait(false));
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The REST request was canceled.", exception, cancellationToken);
        }
        catch (OperationCanceledException exception) when (deadline?.IsCancellationRequested == true)
        {
            throw new TaskCanceledException("The REST request exceeded the configured HttpClient.Timeout.",
                new TimeoutException("The REST response deadline elapsed.", exception), token);
        }
    }

    private static async Task<T?> ReadSuccessAsync<T>(
        HttpContent content,
        JsonTypeInfo<T> typeInfo,
        long limit,
        CancellationToken cancellationToken
    )
    {
        if (content.Headers.ContentLength > limit)
        {
            throw BoundedRestResponseStream.LimitExceeded(limit);
        }

        var contentStream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var bounded = new BoundedRestResponseStream(contentStream, limit);
        var charset = content.Headers.ContentType?.CharSet;

        if (charset is not null)
        {
            Encoding encoding;

            try
            {
                encoding = Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException exception)
            {
                throw new InvalidOperationException("The REST response declares an invalid charset.", exception);
            }


            if (encoding.CodePage != Encoding.UTF8.CodePage)
            {
                var transcoded = Encoding.CreateTranscodingStream(bounded, encoding, Encoding.UTF8, leaveOpen: true);
                await using (transcoded.ConfigureAwait(false))
                {
                    return await JsonSerializer.DeserializeAsync(transcoded, typeInfo, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }


        return await JsonSerializer.DeserializeAsync(bounded, typeInfo, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadErrorAsync(
        HttpResponseMessage response,
        long limit,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await response.Content.LoadIntoBufferAsync(limit, cancellationToken).ConfigureAwait(false);
            var error = await response.Content
                .ReadFromJsonAsync(CassieRestJsonContext.Default.RestErrorResponse, cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(error?.Error))
            {
                return error.Error;
            }
        }
        catch (Exception exception) when (exception is JsonException or HttpRequestException or InvalidOperationException)
        {
            // Keep the HTTP status when the body is unavailable, oversized, or not a Cassie error.
        }

        return response.ReasonPhrase ?? $"HTTP {(int)response.StatusCode}";
    }

    private static string CollectionPath(string collection)
    {
        return "api/v1/collections/" + EscapePathSegment(collection, nameof(collection));
    }

    private static string DocumentPath(string collection, string id)
    {
        return CollectionPath(collection) + "/documents/" + EscapePathSegment(id, nameof(id));
    }

    internal static string EscapePathSegment(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        // Cassie's router uses literal identifier paths, and '$' is a valid identifier character.
        return value is "." or ".." || value.IndexOfAny(['/', '\\', '%', '?', '#']) >= 0 || value.Any(char.IsControl)
            ? throw new ArgumentException(
                "A REST resource identifier cannot contain path separators, URI controls, or traversal segments.",
                parameterName)
            : Uri.EscapeDataString(value).Replace("%24", "$", StringComparison.Ordinal);
    }

    private static Uri NormalizeBaseAddress(Uri baseAddress)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        return !baseAddress.IsAbsoluteUri || (baseAddress.Scheme != Uri.UriSchemeHttp &&
                                              baseAddress.Scheme != Uri.UriSchemeHttps)
                                          || baseAddress.Query.Length > 0 || baseAddress.Fragment.Length > 0 ||
                                          baseAddress.UserInfo.Length > 0
            ? throw new ArgumentException(
                "The REST base address must be an absolute HTTP(S) URI without credentials, a query, or a fragment.",
                nameof(baseAddress))
            : baseAddress.AbsoluteUri.EndsWith('/', StringComparison.Ordinal)
                ? baseAddress
                : new Uri(baseAddress.AbsoluteUri + "/");
    }
}
