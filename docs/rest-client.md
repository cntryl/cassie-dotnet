# REST client

`Cntryl.Cassie.Rest` adds a REST client alongside the ADO.NET provider. It targets the `/api/v1/...` routes the Cassie server serves, the same routes the server's own administration UI uses. Health probes and `/metrics` are unversioned at the root.

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
| `LoginAsync` / `GetSessionAsync` / `LogoutAsync` | `POST api/v1/auth/login`, `GET api/v1/auth/session`, `POST api/v1/auth/logout` |
| `ListCollectionsAsync` / `CreateCollectionAsync` | `GET` / `POST api/v1/collections` |
| `AddDocumentAsync` | `POST api/v1/collections/{collection}/documents` |
| `GetDocumentAsync` / `DeleteDocumentAsync` | `GET` / `DELETE api/v1/collections/{collection}/documents/{id}` |
| `CreateVectorIndexAsync` | `POST api/v1/collections/{collection}/indexes` |
| `SearchAsync` | `POST api/v1/collections/{collection}/search` |
| `Admin.ListDatabasesAsync` / `CreateDatabaseAsync` | `GET` / `POST api/v1/admin/databases` |
| `Admin.GetSchemaAsync` | `GET api/v1/admin/query/schema?database=...` |
| `Admin.ExecuteQueryAsync` / `ValidateQueryAsync` / `ExplainQueryAsync` | `POST api/v1/admin/query/execute`, `validate`, or `explain` |
| `Admin.CancelQueryAsync` | `DELETE api/v1/admin/query-operations/{operationId}` |
| `Admin.ExportVerificationManifestAsync` | `POST api/v1/admin/projections/{projection}/verification-manifests` |
| `Admin.CompareVerificationManifestsAsync` | `POST api/v1/admin/projection-consistency-checks` |
| `Admin.ListProjectionConsistencyReportsAsync` | `GET api/v1/admin/projection-consistency-reports` |
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

The scripted contract suite covers authentication cookies and session isolation, borrowed-client ownership, base paths and identifiers, exact request payloads, HTTP errors, and cancellation before dispatch and during headers/body reads.

An earlier revision of this client targeted a planned `/v1/...` prefix that no published server implemented, so the six live REST workflows failed at login with HTTP 401 (the server answers 401 to any route it does not know). The client now uses `/api/v1/...`, and all six pass against `ghcr.io/cntryl/cassie:latest`. If the server later moves its REST prefix, the route constants in `CassieRestClient` and `CassieAdminClient` are the only places to change. See [live-test setup](../test/Cassie.Tests/Live/README.md) for required endpoints and embedding configuration.

CI runs the published `ghcr.io/cntryl/cassie:latest` image, so the live REST and pgwire
contracts run against the current server on every push.
