# REST client

`Cntryl.Cassie.Rest` adds a REST client alongside the ADO.NET provider. It targets the agreed `/v1/...` server contract. The earlier test server exposed `/api/v1/...`, causing six live-test failures until the server migration lands.

## Connection and authentication

```csharp
using Cntryl.Cassie.Rest;

using CassieRestClient client = new(new Uri("https://cassie.example/"));
await client.LoginAsync(username, password, cancellationToken);
IReadOnlyList<string> collections = await client.ListCollectionsAsync(cancellationToken);
CassieRestQueryResult result = await client.Admin.ExecuteQueryAsync(
    new("postgres", "SELECT 42 AS answer"), cancellationToken);
await client.LogoutAsync(cancellationToken);
```

The URI constructor owns its HTTP client, handler, and per-client cookie container. HTTPS uses platform certificate validation and checks certificate revocation. `Admin` uses the same transport and authenticated session as the collection methods.

The `CassieRestClient(HttpClient)` constructor borrows the supplied client. It requires an absolute HTTP(S) `BaseAddress`; the caller configures cookie handling and owns disposal. The wrapper preserves the supplied client's base address, default headers, timeout, and lifetime. Keep cookie containers separate for unrelated authentication sessions. Both constructors preserve a reverse-proxy base path and normalize its trailing slash internally.

Authentication uses the opaque `cassie_session` cookie. `LoginAsync` posts username/password and returns user/role metadata; the cookie handler retains the session. `GetSessionAsync` reads that metadata and `LogoutAsync` revokes the server session. Basic and bearer credentials are outside this contract. `Dispose` releases owned HTTP resources; server logout remains explicit.

## Operations and routes

Paths below are relative to the configured base address. Resource identifiers are handled as individual path segments; simple dot-qualified collection/projection names and literal `$` are preserved. Path separators, URI query/fragment controls, encoded path tricks, and `.`/`..` traversal segments are rejected before dispatch.

| Client method | HTTP route |
| --- | --- |
| `LoginAsync` / `GetSessionAsync` / `LogoutAsync` | `POST v1/auth/login`, `GET v1/auth/session`, `POST v1/auth/logout` |
| `ListCollectionsAsync` / `CreateCollectionAsync` | `GET` / `POST v1/collections` |
| `AddDocumentAsync` | `POST v1/collections/{collection}/documents` |
| `GetDocumentAsync` / `DeleteDocumentAsync` | `GET` / `DELETE v1/collections/{collection}/documents/{id}` |
| `CreateVectorIndexAsync` | `POST v1/collections/{collection}/indexes` |
| `SearchAsync` | `POST v1/collections/{collection}/search` |
| `Admin.ListDatabasesAsync` / `CreateDatabaseAsync` | `GET` / `POST v1/admin/databases` |
| `Admin.GetSchemaAsync` | `GET v1/admin/query/schema?database=...` |
| `Admin.ExecuteQueryAsync` / `ValidateQueryAsync` / `ExplainQueryAsync` | `POST v1/admin/query/execute`, `validate`, or `explain` |
| `Admin.CancelQueryAsync` | `DELETE v1/admin/query-operations/{operationId}` |
| `Admin.ExportVerificationManifestAsync` | `POST v1/admin/projections/{projection}/verification-manifests` |
| `Admin.CompareVerificationManifestsAsync` | `POST v1/admin/projection-consistency-checks` |
| `Admin.ListProjectionConsistencyReportsAsync` | `GET v1/admin/projection-consistency-reports` |
| `Admin.GetReadinessAsync` / `GetStartupAsync` / `GetLivenessAsync` | `GET readyz`, `startupz`, or `livez` |
| `Admin.GetTargetAsync` / `GetMetricsAsync` | `GET targetz` / `GET metrics` |

The four probes remain unversioned; `/metrics` is an additional unversioned diagnostics endpoint. Schema, table, role, and other supported administration statements use `ExecuteQueryAsync`; the client adds no speculative resource routes for them. Authorization is enforced by the server.

## Documents, vectors, and administration values

Collection creation takes `CassieCollectionDefinition` and `CassieRestField` values and returns the server-reported collection name. Documents use `JsonObject`; adding one returns the server-generated identifier. Deletion returns the server's Boolean acknowledgement.

`CassieVectorIndexDefinition` takes a field and string-valued options, including required `source_field`. The response reports the actual provider, model, dimensions, metric, index type, and status. `CassieVectorSearchRequest.Query` is text embedded by the server's configured provider. Documents can also carry explicit vector components as JSON arrays. Search limits and offsets are optional; a zero limit requests an empty result.

Search and SQL execution return `CassieRestQueryResult`: column metadata, positional `JsonElement` rows, and a command tag. Search preserves the actual response shape, which contains document fields and identity rather than a score/distance contract. JSON values retain their numeric and nested shapes. Explanation plans remain `JsonObject` values; manifests and consistency reports have typed version/hash/coverage fields, including unsigned counters.

All methods accept a cancellation token. A pre-canceled token prevents dispatch, and tokens cover response-body reading as well as sending. The supplied `HttpClient.Timeout` also covers the buffered HTTP body. Cancellation remains an ordinary .NET cancellation exception. Canceling an HTTP request does not promise server-side query termination: supply `CassieAdminQueryRequest.OperationId` and use `Admin.CancelQueryAsync` from the same session for explicit server cancellation. A server-returned HTTP 499 remains a `CassieRestException` unless the local request itself was canceled.

Unsuccessful responses raise `CassieRestException`, exposing `HttpRequestException.StatusCode` and the server's `Error` text. Malformed/non-JSON error bodies retain the HTTP status with a reason-phrase fallback. Requests and responses are disposed, and the library adds no automatic retry policy for mutations.

## Native AOT and qualification

Request and response serialization uses the internal source-generated `CassieRestJsonContext` and explicit `JsonTypeInfo` overloads. Documents and heterogeneous values use `JsonObject`/`JsonElement`; there is no reflection-based object mapper or additional package dependency. AOT compilation and a package-consumer run are separate evidence from live compatibility with the new routes.

The new-prefix scripted contract suite passes **57 tests**. It covers authentication cookies and session isolation, borrowed-client ownership, base paths and identifiers, exact request payloads, HTTP errors, and cancellation before dispatch and during headers/body reads. The pre-cancellation regression was red when the handler was invoked once; the explicit token preflight returns cancellation with zero dispatches.

All six live REST workflows previously passed against `/api/v1/...` on Cassie `19edf3a36fc9d2a65187d137370b6f164fcdfb2e`. Those results establish historical payload/operation semantics only. They do not qualify `/v1/...`. The six live tests remain enabled and currently expect the future server contract; against the old pinned server they fail at `/v1/auth/login` with HTTP 401. There is no old-prefix fallback, proxy, or conditional skip.

Final local validation exercised **610 tests: 604 passed, six failed, zero skipped**. All 527 non-live cases and all 77 existing pgwire live cases passed. XML readback confirmed every failure belongs to the new REST live class and occurs during login against the pre-migration server; no unrelated failure was accepted. The fresh local package restored into an isolated consumer and published as macOS ARM64 Native AOT with warnings treated as errors and no warnings. Its new-prefix REST runtime integration remains unqualified while the server routes are pending. Hosted CI, deployment, publication, and production readiness remain separate claims. See [live-test setup](../test/Cassie.Tests/Live/README.md) for required endpoints and embedding configuration.

CI runs the published `ghcr.io/cntryl/cassie:main` image. Updating that image can
bring in new server routes without weakening or skipping the live contracts.
