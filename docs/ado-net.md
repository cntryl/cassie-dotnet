# ADO.NET support

`Cassie` implements the standard ADO.NET connection, command, parameter, reader,
transaction, and data source APIs for Cassie's pgwire interface. It targets
Cassie's documented SQL and type contract; it is not a general PostgreSQL
replacement.

## Connections

Create a `CassieDataSource` for a shared connection string, or create a
`CassieConnection` directly. Data source connections are caller-owned. Disposing
the data source stops new connections; it leaves existing connections, shared
connection pools, and its optional `ILoggerFactory` alone.

TLS certificate and host-name validation are on by default. Set
`SSL Root Certificate` when using a private CA. `SSL Mode=Prefer` allows a
cleartext fallback if the server declines TLS; `SSL Mode=Disable` opts out.
Cassie's current authentication contract uses a cleartext password inside the
TLS connection; SCRAM is not supported.

Pooling is on by default. `Maximum Pool Size` defaults to 100. Returned sessions
are reset with `DISCARD ALL`; set `Pooling=false` to disable pooling. `Options`
passes startup settings such as `-c TimeZone=UTC` to Cassie.

`ChangeDatabase` reconnects with a different startup database and requires an
open connection with no active reader or transaction. When the database is
omitted, the username is used as the startup database.

## Commands and readers

Use `CassieCommand` with text commands and bound input parameters. Stored
procedures use `CommandType.StoredProcedure`; the provider quotes the procedure
name and sends positional input values in a `CALL`. Output parameters and result
rows from a procedure body are not part of Cassie's contract.

`Prepare` and `PrepareAsync` register a server statement for reuse. Changing SQL,
parameter types, or the underlying server session triggers re-preparation.
`BinaryResults` requests binary results where the provider has a matching codec;
results use text format by default.

Async connection, command, reader, transaction, and COPY methods support
cancellation. The reader drains results when it closes so the connection can be
reused. Extended queries fetch up to 128 rows per portal request. `GetStream`
and `GetTextReader` wrap values already buffered for the current row; they do not
stream individual fields directly from the socket.

`GetSchema`, `GetSchemaAsync`, `GetSchemaTable`, and `GetColumnSchema` expose the
metadata Cassie reports. Base-table origin, nullability, and key constraints
remain unknown when they are absent from the server's row description.

## Batches

`CassieBatch` (`connection.CreateBatch()`, `CassieProviderFactory.CreateBatch()`,
or `CassieDataSource.CreateBatch()`) runs several commands in one network round
trip. Each command is sent as Parse, Bind, Describe, and Execute, and one Sync
ends the pipeline, so the batch is written once and the responses are read
afterwards. Parameters bind as they do for `CassieCommand`. Batch commands are
not paged: each streams its whole result.

```csharp
await using CassieBatch batch = new(connection, transaction);
batch.BatchCommands.Add("INSERT INTO ledger (name) VALUES (@name)")
    .Parameters.Add(new CassieParameter { ParameterName = "name", Value = "alpha" });
batch.BatchCommands.Add("SELECT count(*) FROM ledger");
await using DbDataReader reader = await batch.ExecuteReaderAsync();
do { while (await reader.ReadAsync()) { /* rows of this command */ } }
while (await reader.NextResultAsync());
```

- `ExecuteReader` yields one result per command, including commands that return
  no rows (zero columns), in order. `reader.RecordsAffected` is the sum.
  `ExecuteNonQuery` returns that sum. `ExecuteScalar` returns the first column of
  the first row of the first result that has columns; the rest of the batch still
  runs and is read.
- `CassieBatchCommand.RecordsAffected` is set per command as the server reports
  it: -1 until then, and -1 for statements without a row count such as `SELECT`.
- Every parameter of every command is validated and encoded before anything is
  sent (and before a connectionless batch opens a connection), so a bad parameter
  fails without closing the connection or its transaction.
- With one Sync, the server skips the commands after the first error. That error
  is thrown as `CassieException` when the reader reaches the failing command (or
  from the `Execute` method), after the response has been read to
  ReadyForQuery, so the connection stays usable. Commands that completed before
  the failure keep their `RecordsAffected`; the failing and skipped commands stay
  at -1. Outside an explicit transaction the server may treat the pipeline as one
  implicit transaction and undo the completed commands, so use a transaction
  when the commands must succeed or fail together.
- `Timeout`, `Cancel`, and cancellation tokens work as for commands and cover the
  whole batch. Synchronous members use blocking I/O on the calling thread.
- `Prepare` and `PrepareAsync` only validate the batch (connection, transaction,
  and parameter binding); they send nothing and do not prepare server
  statements. Every command is parsed with the unnamed statement on each execution.
- A batch from a `DbDataSource` has no connection: each execution opens a pooled
  connection and closes it when the reader is closed. It cannot take a
  transaction. `CommandBehavior.SchemaOnly` is not supported for batches.

## Values and COPY

The provider uses binary parameters when a matching codec exists and otherwise
uses text. Supported values include common scalar types, UUID, date/time types,
network and MAC addresses, bit strings, intervals, LSNs, vectors, and registered
arrays. `CassieNumeric` preserves exact `NUMERIC` values. `CassieArray` preserves
PostgreSQL array rank, dimensions, lower bounds, and element type for binary
results; text-format array results remain strings. See the type and value
contracts in the tests for detailed mappings and edge cases.

`CopyFromAsync` and `CopyToAsync` stream raw `COPY` data. COPY does not accept
command parameters. The provider also supports `DbDataAdapter` table filling;
`Update` requires caller-supplied modification commands and uses reflection-based
ADO.NET behavior that may need trimming annotations.

## Product scope

Cassie is a single-node read-model database. Its supported SQL, type, catalog,
and client workflows follow the Cassie product contract. Distributed SQL, native
PostgreSQL extension loading, general stored-program execution, triggers,
replication, and unsupported PostgreSQL forms are outside that contract.

`CassieDiagnostics.ActivitySource` and `CassieDiagnostics.Meter` publish
connection, query, and streaming COPY activity and duration data. Setting
`CassieConnection.LoggerFactory` enables lifecycle and failure logs; logs omit
SQL text, parameter values, credentials, and server error details.

For vectors, REST administration, and authenticated session setup, see the
[REST guide](rest-client.md). For runnable integration-test configuration, see
the [live test setup](../test/Cassie.Tests/Live/README.md).
