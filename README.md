# Cassie .NET

Cassie .NET is the .NET 11 client for [Cassie](https://github.com/cntryl/cassie).
Use ADO.NET for SQL, transactions, and streaming COPY over pgwire. Use the REST
client to manage collections, search vectors, and call administrative endpoints.

## Install

Packages are available from the Cntryl GitHub Packages feed. Configure
[GitHub Packages authentication](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry)
with a classic personal access token that has `read:packages`.

```sh
dotnet nuget add source https://nuget.pkg.github.com/cntryl/index.json --name cntryl
dotnet add package Cntryl.Cassie
```

Add `Cntryl.Cassie.DependencyInjection` to register a data source, scoped
connections, or REST clients through `IServiceCollection`.

## Quick start

Supply the Cassie connection string from your application configuration.

```csharp
using Cntryl.Cassie.Connection;

await using var source = new CassieDataSource(connectionString);
await using var connection = await source.OpenConnectionAsync();
await using var command = connection.CreateCommand();
command.CommandText = "SELECT 1";

Console.WriteLine(await command.ExecuteScalarAsync());
```

For REST, supply the server URI and credentials from your configuration. Create
the collection and its vector index before searching.

```csharp
using Cntryl.Cassie.Rest;

using var client = new CassieRestClient(new Uri("https://cassie.example/"));
await client.LoginAsync(username, password);

var matches = await client.SearchAsync(
    "documents", new("embedding", "connection setup", Limit: 5));
```

For short-lived credentials such as Cassie AWS IAM tokens, omit `Password` and pass a callback to
`new CassieDataSource(connectionString, TimeProvider.System, passwordProvider)`. The callback runs
for each new physical connection, can run concurrently, and receives the connection open cancellation
token. Pool reuse skips the callback. Use certificate-verified TLS and configure the callback to obtain
the credential from your application's identity provider.

## Guides

| Task | Guide |
| --- | --- |
| Configure TLS, execute SQL, or stream COPY | [ADO.NET provider](docs/ado-net.md) |
| Search collections or call administrative endpoints | [REST client](docs/rest-client.md) |
| Register clients with dependency injection | [Dependency injection](src/Cassie.DependencyInjection/README.md) |
| Run against a disposable Cassie server | [Live tests](test/Cassie.Tests/Live/README.md) |

## Development

Run the solution tests with the .NET 11 SDK and authenticated access to the
Cntryl package feed. The live tests guide shows how to enable server coverage.

```sh
dotnet test --solution Cassie.slnx -c Release --filter-not-trait Isolation=Server
```

See [Contributing](CONTRIBUTING.md) for repository conventions.
