# Live tests

The `Live*ContractTests` classes exercise the provider against a real Cassie
pgwire endpoint. They share one xUnit collection, so they run one at a time, and
each test owns a dedicated non-pooled connection. Schema object names are fixed
SQL constants and reused by this serialized collection; run only one test process
against a disposable server. Teardown drops only objects that the test created,
through a separate connection to its original database, and reports cleanup
failures.

Use disposable servers. Run the database image test on a separate fresh server:
it restores a database that the current server cannot drop, and later DDL on the
same server can fail with `transactional DDL cannot access multiple databases`.
That test has the `Isolation=Server` trait. It drops its source table after taking
the backup and leaves the restored database to be removed with the server.

| Variable | Effect |
| --- | --- |
| `CASSIE_DOTNET_TEST_CONNECTION_STRING` | Endpoint to test. Without it every live test is skipped. |
| `CASSIE_DOTNET_REQUIRE_LIVE=true` | Fail instead of skip when the endpoint is missing. CI sets this. |
| `CASSIE_DOTNET_TEST_ARRAYS=true` | One-dimensional array parameter and result tests. |
| `CASSIE_DOTNET_TEST_ARRAY_SHAPES=true` | Multidimensional and lower-bound array tests. |
| `CASSIE_DOTNET_TEST_NAME_TYPE=true` | `name` and `name[]` tests. |
| `CASSIE_DOTNET_TEST_JSONB_TYPE=true` | Distinct JSONB OID 3802 test. |
| `CASSIE_DOTNET_TEST_JSONPATH_TYPE=true` | JSONPath type test. |
| `CASSIE_DOTNET_TEST_RECORD_TYPES=true` | `void`, `record` and `record[]` metadata tests. |
| `CASSIE_DOTNET_TEST_PARAMETER_STATUS_UPDATES=true` | Hidden `DateStyle` change through a view. |
| `CASSIE_DOTNET_TEST_STARTUP_APPLICATION_NAME` | Expected `application_name` when the connection string sets `Options`. |
| `CASSIE_DOTNET_TEST_REJECTED_TLS_CONNECTION_STRING` | TLS endpoint addressed by a mismatched host name. |

```sh
CASSIE_DOTNET_TEST_CONNECTION_STRING="Host=127.0.0.1;Port=5432;Database=postgres;Username=root;Password=...;SSL Mode=Disable" \
  dotnet test --project test/Cassie.Tests/Cassie.Tests.csproj --filter-namespace Cntryl.Cassie.Tests.Live --filter-not-trait Isolation=Server
```

Start another fresh disposable server, set the connection string to its endpoint,
and run only the database image test:

```sh
dotnet test --project test/Cassie.Tests/Cassie.Tests.csproj --filter-trait Isolation=Server --minimum-expected-tests 1
```

For TLS, use `SSL Mode=Require;SSL Root Certificate=/path/to/ca.pem` with a host
name that matches the certificate.
