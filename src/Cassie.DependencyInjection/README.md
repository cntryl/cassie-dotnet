# Cassie.DependencyInjection

Microsoft dependency injection registrations for the Cassie ADO.NET provider and
REST clients. Reference this package when your application uses
`IServiceCollection`; the `Cassie` package remains usable independently.

```csharp
using Microsoft.Extensions.DependencyInjection;

services.AddCassieDataSource(connectionString);
services.AddCassieRestClient(new Uri("https://cassie.example/"));
```

The application supplies `services` and `connectionString`.

| Service | Lifetime | Behavior |
| --- | --- | --- |
| `Cntryl.Cassie.Connection.CassieDataSource` | Singleton | Creates independently owned connections with the registered connection string and optional `ILoggerFactory`. |
| `Cntryl.Cassie.Connection.CassieConnection` | Scoped | Starts closed; call `OpenAsync` before use. The scope disposes it. |
| `System.Data.Common.DbDataSource` | Singleton | Resolves the Cassie data source if another provider has not registered this abstraction. |
| `System.Data.Common.DbConnection` | Scoped | Resolves the scoped Cassie connection if another provider has not registered this abstraction. |
| `Cntryl.Cassie.Rest.CassieRestClient` | Scoped | Owns an HTTP client and cookie container for that scope. The scope disposes it. |
| `Cntryl.Cassie.Rest.CassieAdminClient` | Scoped | Resolves the scoped REST client's `Admin` property, sharing its authenticated session. |

These are the default lifetimes. Existing registrations keep their lifetime and
ownership; aliases follow their typed service. Use the typed Cassie services when
your application registers multiple database providers. Registration preserves
existing services; repeated calls keep the first registration.

A data source's `CreateConnection`, `OpenConnection`, and `OpenConnectionAsync`
methods return connections owned by their caller. Disposing the source prevents
future creation and leaves existing connections, physical connection pools, and
the caller-owned logger factory alone.

REST login and logout remain explicit. Call `LoginAsync` inside the application's
scope before authenticated operations and `LogoutAsync` when the server session
should be revoked. Scope disposal releases the HTTP client; it does not log out
at the server. Keep scopes isolated between users. Each scope creates its own
cookie container rather than obtaining a pooled handler from `IHttpClientFactory`.
