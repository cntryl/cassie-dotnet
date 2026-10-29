# Cassie .NET

Two ways to use Cassie from .NET, plus optional dependency injection:

- **ADO.NET** for SQL, transactions, and streaming `COPY` over pgwire.
- **REST** for vector collections, search, and administration.
- **Dependency injection** through the separate `Cassie.DependencyInjection` package.

Requires .NET 10. Packages are published to GitHub Packages.

## Install

Add the `cntryl` NuGet source and give NuGet a classic GitHub personal access
token with `read:packages`. Store credentials in your local or CI secret store;
set `NuGetPackageSourceCredentials_cntryl` to
`Username=<GitHub username>;Password=<token>`. See [GitHub's NuGet guide] and
[NuGet's credential guidance].

```sh
dotnet nuget add source https://nuget.pkg.github.com/cntryl/index.json --name cntryl
dotnet add package Cassie --version 0.1.1
```

For dependency injection, also install:

```sh
dotnet add package Cassie.DependencyInjection --version 0.1.1
```

## Run a query

Load `connectionString`, `username`, and `password` from your app configuration
or secret store.

```csharp
using System;
using Cntryl.Cassie.Connection;

await using var source = new CassieDataSource(connectionString);
await using var connection = await source.OpenConnectionAsync();
await using var command = connection.CreateCommand();
command.CommandText = "SELECT 1";

Console.WriteLine(await command.ExecuteScalarAsync());
```

TLS certificate validation is on by default. For a private CA, set
`SSL Root Certificate` in the connection string.

## Use REST

```csharp
using System;
using Cntryl.Cassie.Rest;

using var client = new CassieRestClient(new Uri("https://cassie.example/"));
await client.LoginAsync(username, password);

CassieRestQueryResult matches = await client.SearchAsync(
    "documents", new("embedding", "connection setup", Limit: 5));

CassieRestQueryResult result = await client.Admin.ExecuteQueryAsync(
    new("postgres", "SELECT 1 AS answer"));
```

Create the collection and vector index before searching. Cassie uses its
configured embedding provider to embed the search text. The REST client uses
`/v1/...` routes; run it with a server revision that supports them. See the
[REST guide] for vectors, administration, and probe endpoints.

## Dependency injection

`Cassie.DependencyInjection` registers the data source, scoped connections, and
REST clients. In ASP.NET Core startup, call:

```csharp
using System;
using Microsoft.Extensions.DependencyInjection;

builder.Services.AddCassieDataSource(connectionString);
builder.Services.AddCassieRestClient(new Uri("https://cassie.example/"));
```

## More

- [ADO.NET overview]
- [REST guide]
- [Dependency injection details]
- [Live test setup]

[GitHub's NuGet guide]: https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry
[NuGet's credential guidance]: https://learn.microsoft.com/nuget/consume-packages/consuming-packages-authenticated-feeds
[ADO.NET overview]: https://github.com/cntryl/cassie-dotnet/blob/main/docs/ado-net.md
[REST guide]: https://github.com/cntryl/cassie-dotnet/blob/main/docs/rest-client.md
[Dependency injection details]: https://github.com/cntryl/cassie-dotnet/blob/main/src/Cassie.DependencyInjection/README.md
[Live test setup]: https://github.com/cntryl/cassie-dotnet/blob/main/test/Cassie.Tests/Live/README.md
