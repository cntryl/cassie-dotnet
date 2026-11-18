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

### Connection string keywords

Keywords are case-insensitive. An unrecognized keyword, or a value outside the
allowed range, fails when the connection opens rather than being ignored.

| Keyword | Default | Meaning |
| --- | --- | --- |
| `Host` | `localhost` | Server host name or address. |
| `Port` | `5432` | Server port, 1 to 65535. |
| `Database` | the user name | Startup database. |
| `Username` | none (required) | User name. |
| `Password` | empty | Password. |
| `SSL Mode` | `Require` | `Require`, `Prefer`, or `Disable`. |
| `SSL Root Certificate` | empty | Path to a PEM bundle of trusted roots; empty uses platform trust. |
| `Timeout` | `15` | Seconds allowed to open a connection; 0 disables the limit. |
| `Command Timeout` | `30` | Default command timeout in seconds; 0 disables it. |
| `Application Name` | `Cassie .NET` | Client application name reported to the server. |
| `Options` | empty | Startup options, such as `-c TimeZone=UTC`. |
| `Pooling` | `true` | Whether physical connections are pooled. |
| `Maximum Pool Size` | `100` | Physical connections per pool, 1 to 1000. |
| `Connection Idle Lifetime` | `300` | Seconds an idle pooled session is kept; 0 disables the limit. |
| `Connection Lifetime` | `0` | Maximum age of a pooled session in seconds; 0 disables the limit. |
| `Fetch Size` | `128` | Rows requested per portal fetch for extended queries. |

Time values accept 0 to 2147483 seconds. Connections that use identical settings
share one pool for the process. `CassieConnection.ClearPool` and
`CassieConnection.ClearAllPools` close idle pooled sessions. `CassieConnectionStringBuilder.ValidateKeywords`
runs the same keyword and range checks without opening a connection.

## Commands and readers

Use `CassieCommand` with text commands and bound input parameters. Stored
procedures use `CommandType.StoredProcedure`; the provider quotes the procedure
name and sends positional input values in a `CALL`. Output parameters and result
rows from a procedure body are not part of Cassie's contract.

The provider's own types are available without casts: `connection.CreateCommand()`
returns a `CassieCommand`, `command.Parameters` is a `CassieParameterCollection`,
and `command.Connection`, `command.Transaction`, `connection.BeginTransaction()`,
and `connection.CreateBatch()` are typed the same way. Assigning a connection or
transaction from another provider through the base-class properties throws
`InvalidCastException`. `DbProviderFactories.GetFactory(connection)` returns
`CassieProviderFactory.Instance`.

`Prepare` and `PrepareAsync` register a server statement for reuse. Changing SQL,
parameter types, or the underlying server session triggers re-preparation.
`BinaryResults` requests binary results where the provider has a matching codec;
results use text format by default.

Parameter values are sent in binary when the provider has a binary codec for the
type and in text otherwise. `CassieParameter.ParameterFormat` overrides that
choice for one parameter: `Auto` (the default), `Text`, or `Binary`. Requesting
`Binary` for a type without a binary codec fails before anything is sent.

Async connection, command, reader, transaction, and COPY methods support
cancellation. The reader drains results when it closes so the connection can be
reused. Extended queries fetch up to 128 rows per portal request. `GetStream`
and `GetTextReader` wrap values already buffered for the current row; they do not
stream individual fields directly from the socket.

`GetSchema`, `GetSchemaAsync`, `GetSchemaTable`, and `GetColumnSchema` expose the
metadata Cassie reports. Base-table origin, nullability, and key constraints
remain unknown when they are absent from the server's row description.

## Transactions

`BeginTransaction` and `BeginTransactionAsync` start a `READ COMMITTED`
transaction; `IsolationLevel.Unspecified` means the same. Other isolation levels
throw `NotSupportedException`. A connection has at most one active transaction,
and a command that names a transaction must name the connection's active one.

Savepoints are supported (`SupportsSavepoints` is `true`) through `Save`,
`Rollback(name)`, and `Release`, with async counterparts. `Commit` and
`CommitAsync` throw `CassieException` if the server reports a rollback.
Disposal gives an active transaction five seconds to roll back. If a reader
owns the connection or the server cannot confirm rollback, disposal closes the
physical connection. Returning a pooled connection also rolls back any active
transaction before reuse. `Connection` returns `null` once the transaction ends
or the connection closes.

## Contract details

The provider follows the documented `System.Data.Common` contracts. These are the
points where callers most often depend on exact behavior.

- **Synchronous members do real blocking I/O.** `Open`, `Close`, `ExecuteReader`,
  `Read`, `NextResult`, `Prepare`, and the transaction methods run on the calling
  thread and do not wait on the thread pool. A blocking call cannot observe a
  cancellation token, so a timeout or `Cancel` during a synchronous call closes
  the connection underneath it; the call then fails with the timeout or
  cancellation error.
- **Rows are buffered and decoded on demand.** The reader parses frames from a
  read buffer and decodes a column only when a getter asks for it. `GetInt16`,
  `GetInt32`, `GetInt64`, `GetBoolean`, `GetDouble`, `GetFloat`, and `GetGuid` read
  the matching column type without boxing.
- **Column ordinals.** A getter called with an ordinal outside the result throws
  `IndexOutOfRangeException`, as ADO.NET specifies. Getters throw
  `InvalidOperationException` when the reader is closed or not positioned on a
  row, and when `CommandBehavior.SequentialAccess` columns are read out of order.
- **Column names.** `GetOrdinal` matches the exact name first and then ignores
  case. An unknown name throws `IndexOutOfRangeException` naming the column, as
  ADO.NET specifies.
- **Typed getters convert leniently.** `GetInt32` on a text column parses the
  text. A value that cannot be converted throws `FormatException` or
  `OverflowException` rather than `InvalidCastException`.
- **`GetSchemaTable`** returns `null` when the current statement produced no result
  set, and throws `InvalidOperationException` once the reader is closed.
- **Parameters.** `CassieParameter.DbType` defaults to `DbType.Object`, which means
  "infer the type from the value"; ADO.NET documents `String` as the default. An
  index outside the collection throws `IndexOutOfRangeException`, and so does
  reading, assigning, or removing a parameter by a name that is not in the
  collection. Parameters are input-only.
- **`RecordsAffected`** is -1 for statements that report no row count, and the sum
  of the counts otherwise.
- **Not supported:** output parameters, XML parameters, binary `COPY`,
  `System.Transactions` enlistment, and `CommandBehavior.SchemaOnly` for batches.
  Unsupported operations throw `NotSupportedException` with a message naming the
  feature.

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

- `ExecuteReader` yields one result per command that returns a result set, in
  order. Commands that return none, such as `INSERT`, are skipped, exactly as
  for a multi-statement `CassieCommand`; their row counts are on each
  `CassieBatchCommand`. `reader.RecordsAffected` is the sum.
  `ExecuteNonQuery` returns that sum. `ExecuteScalar` returns the first column of
  the first row of the first result that has columns; the rest of the batch still
  runs and is read.
- `CassieBatchCommand.RecordsAffected` is set per command as the server reports
  it: the row count, or -1 for a completed statement without one such as
  `SELECT`. It is 0 for a command that affected no rows, failed, or was skipped.
- Every parameter of every command is validated and encoded before anything is
  sent (and before a connectionless batch opens a connection), so a bad parameter
  fails without closing the connection or its transaction.
- With one Sync, the server skips the commands after the first error. That error
  is thrown as `CassieException` when the reader reaches the failing command (or
  from the `Execute` method), after the response has been read to
  ReadyForQuery, so the connection stays usable. `CassieException.BatchCommand`
  is the command that failed. Commands that completed before
  the failure keep their `RecordsAffected`; the failing and skipped commands
  report 0. Outside an explicit transaction the server may treat the pipeline as one
  implicit transaction and undo the completed commands, so use a transaction
  when the commands must succeed or fail together.
- `Timeout`, `Cancel`, and cancellation tokens work as for commands and cover the
  whole batch. Synchronous members use blocking I/O on the calling thread.
- `Prepare` and `PrepareAsync` validate the batch (connection, transaction, and
  parameter binding) and then prepare each command as a server statement. Later
  executions bind to those statements instead of parsing the SQL again, and a
  command is prepared again when its SQL, parameter types, or session changes.
  Disposing the batch closes the statements. A batch from a data source is only
  validated, because each execution uses a new connection.
- A batch from a `DbDataSource` has no connection: each execution opens a pooled
  connection and closes it when the reader is closed. It cannot take a
  transaction. `CommandBehavior.SchemaOnly` is not supported for batches.

## Observability

The provider reports through the standard .NET diagnostics types, all named
`Cntryl.Cassie` (`CassieDiagnostics.Name`).

- **Tracing.** `CassieDiagnostics.ActivitySource` starts a client activity named
  `cassie.<operation>` for `connection.open`, `connection.close`, `query`, `batch`,
  `copy.from`, and `copy.to`. Activities carry `db.system` and `db.operation`
  tags; a failed operation sets the error status with the exception type name.
- **Metrics.** `CassieDiagnostics.Meter` publishes the counter
  `cassie.client.operations` and the histogram `cassie.client.operation.duration`
  (milliseconds), tagged with `db.system`, `db.operation`, and `outcome`
  (`success` or `error`).
- **Logging.** Set `CassieConnection.LoggerFactory` or
  `CassieDataSource.LoggerFactory` to receive connection opened and closed events
  at Information and operation failures at Warning.

SQL text, parameter values, credentials, and server error details are never
written to activities, metrics, or logs.

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

## API stability

The public API is locked. Every public type and member of `Cntryl.Cassie` and
`Cntryl.Cassie.DependencyInjection` is listed in `PublicAPI.Shipped.txt` next to
its project, and the build fails if the code and the list disagree. Adding to
the API means adding the new member to `PublicAPI.Unshipped.txt` in the same
change; removing or altering a listed member is a breaking change and shows up
as an edit to the shipped list.
