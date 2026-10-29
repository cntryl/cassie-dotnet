using Cntryl.Cassie;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Security.Authentication;
using System.Text;

string connectionString = Environment.GetEnvironmentVariable("CASSIE_DOTNET_TEST_CONNECTION_STRING")
    ?? throw new InvalidOperationException("Set CASSIE_DOTNET_TEST_CONNECTION_STRING to a pinned Cassie pgwire endpoint.");
string? rejectedTlsConnectionString = Environment.GetEnvironmentVariable("CASSIE_DOTNET_TEST_REJECTED_TLS_CONNECTION_STRING");
if (rejectedTlsConnectionString is not null)
{
    using CassieConnection rejectedTlsConnection = new() { ConnectionString = rejectedTlsConnectionString };
    bool certificateRejected = false;
    try
    {
        await rejectedTlsConnection.OpenAsync();
    }
    catch (AuthenticationException)
    {
        certificateRejected = true;
    }

    if (!certificateRejected)
    {
        throw new InvalidOperationException("Cassie TLS accepted a certificate that did not match the requested host.");
    }
}

static bool MatchesArray(object? candidate, int elementTypeOid, params object?[] expected)
{
    if (candidate is not CassieArray values || values.ElementTypeOid != elementTypeOid || values.Count != expected.Length)
    {
        return false;
    }

    for (int index = 0; index < expected.Length; index++)
    {
        if (expected[index] is byte[] expectedBytes)
        {
            if (values[index] is not byte[] actualBytes || !actualBytes.SequenceEqual(expectedBytes))
            {
                return false;
            }
        }
        else if (!Equals(values[index], expected[index]))
        {
            return false;
        }
    }

    return true;
}

await using CassieConnection connection = new() { ConnectionString = connectionString };
await connection.OpenAsync();
string? expectedStartupApplicationName = Environment.GetEnvironmentVariable("CASSIE_DOTNET_TEST_STARTUP_APPLICATION_NAME");
if (expectedStartupApplicationName is not null)
{
    using CassieCommand startupApplicationName = (CassieCommand)connection.CreateCommand();
    startupApplicationName.CommandText = "SELECT current_setting('application_name')";
    if (!Equals(await startupApplicationName.ExecuteScalarAsync(), expectedStartupApplicationName))
    {
        throw new InvalidOperationException("Cassie did not apply the application_name supplied through startup Options.");
    }
}

await VerifyExtendedTextParameterFormatsAsync(connection);
await VerifyTimestampParameterFormatsAsync(connection);
await VerifyDateTimeReaderConversionsAsync(connection);
await VerifyTemporalSpecialValueConversionsAsync(connection);
await VerifyTemporalDateStyleDecodingAsync(connection);
if (string.Equals(Environment.GetEnvironmentVariable("CASSIE_DOTNET_TEST_PARAMETER_STATUS_UPDATES"), "true", StringComparison.OrdinalIgnoreCase))
{
    await VerifyHiddenDateStyleParameterStatusAsync(connection);
}

if (string.Equals(Environment.GetEnvironmentVariable("CASSIE_DOTNET_TEST_RECORD_TYPES"), "true", StringComparison.OrdinalIgnoreCase))
{
    await VerifyExperimentalResultTypeMetadataAsync(connection);
}

using (CassieCommand unknownValue = (CassieCommand)connection.CreateCommand())
{
    unknownValue.CommandText = "SELECT NULL AS value";
    await using DbDataReader reader = await unknownValue.ExecuteReaderAsync();
    if (reader.GetDataTypeName(0) != "unknown" || reader.GetFieldType(0) != typeof(string)
        || !await reader.ReadAsync() || !Equals(reader.GetValue(0), DBNull.Value))
    {
        throw new InvalidOperationException("Cassie NULL result did not retain the PostgreSQL unknown OID and CLR metadata.");
    }
}

decimal[] numericArray = [1.25m, 2.5m];

if (string.Equals(Environment.GetEnvironmentVariable("CASSIE_DOTNET_TEST_ARRAY_SHAPES"), "true", StringComparison.OrdinalIgnoreCase))
{
    await VerifyArrayShapeRoundTripAsync(connection);
}

using (CassieCommand command = (CassieCommand)connection.CreateCommand())
{
    command.CommandText = "SELECT @value::int AS value";
    CassieParameter parameter = new() { ParameterName = "value", DbType = DbType.Int32, Value = 17 };
    _ = command.Parameters.Add(parameter);
    await command.PrepareAsync();
    if (!Equals(await command.ExecuteScalarAsync(), 17))
    {
        throw new InvalidOperationException("Cassie server-prepared command returned an unexpected first value.");
    }

    parameter.Value = 29;
    if (!Equals(await command.ExecuteScalarAsync(), 29))
    {
        throw new InvalidOperationException("Cassie server-prepared command did not rebind the updated value.");
    }

    using CassieCommand deallocate = (CassieCommand)connection.CreateCommand();
    deallocate.CommandText = "DEALLOCATE ALL";
    _ = await deallocate.ExecuteNonQueryAsync();
    await connection.CloseAsync();
    await connection.OpenAsync();
    parameter.Value = 37;
    if (!Equals(await command.ExecuteScalarAsync(), 37))
    {
        throw new InvalidOperationException("Cassie server-prepared command was not recreated after reconnecting.");
    }
}

using (CassieCommand invalid = (CassieCommand)connection.CreateCommand())
{
    invalid.CommandText = "SELECT FROM";
    bool rejected = false;
    try
    {
        await invalid.PrepareAsync();
    }
    catch (CassieException exception) when (exception.SqlState == "42601")
    {
        rejected = true;
    }

    using CassieCommand recovery = (CassieCommand)connection.CreateCommand();
    recovery.CommandText = "SELECT 1";
    if (!rejected || !Equals(await recovery.ExecuteScalarAsync(), 1))
    {
        throw new InvalidOperationException("Cassie PrepareAsync did not recover the connection after a parse error.");
    }
}

using (CassieCommand createCallTable = (CassieCommand)connection.CreateCommand())
{
    createCallTable.CommandText = "CREATE TABLE cassie_dotnet_live_call_probe (title TEXT)";
    _ = await createCallTable.ExecuteNonQueryAsync();
}

using (CassieCommand createProcedure = (CassieCommand)connection.CreateCommand())
{
    createProcedure.CommandText = "CREATE PROCEDURE cassie_dotnet_live_store(title TEXT) AS \"INSERT INTO cassie_dotnet_live_call_probe (title) VALUES ($1)\"";
    _ = await createProcedure.ExecuteNonQueryAsync();
}

using (CassieCommand callProcedure = (CassieCommand)connection.CreateCommand())
{
    callProcedure.CommandText = "CALL cassie_dotnet_live_store(@title)";
    _ = callProcedure.Parameters.Add(new CassieParameter { ParameterName = "title", Value = "input-bound-call" });
    _ = await callProcedure.ExecuteNonQueryAsync();
}

using (CassieCommand callStoredProcedure = (CassieCommand)connection.CreateCommand())
{
    callStoredProcedure.CommandType = CommandType.StoredProcedure;
    callStoredProcedure.CommandText = "cassie_dotnet_live_store";
    CassieParameter titleParameter = new() { ParameterName = "title", Value = "ado-stored-procedure-call" };
    _ = callStoredProcedure.Parameters.Add(titleParameter);
    await callStoredProcedure.PrepareAsync();
    _ = await callStoredProcedure.ExecuteNonQueryAsync();
    titleParameter.Value = "ado-stored-procedure-call-updated";
    _ = await callStoredProcedure.ExecuteNonQueryAsync();
}

using (CassieCommand verifyCall = (CassieCommand)connection.CreateCommand())
{
    verifyCall.CommandText = "SELECT title FROM cassie_dotnet_live_call_probe";
    if (!Equals(await verifyCall.ExecuteScalarAsync(), "input-bound-call"))
    {
        throw new InvalidOperationException("Cassie CALL did not execute its one-statement body with the bound input parameter.");
    }
}

using (CassieCommand verifyStoredProcedureCall = (CassieCommand)connection.CreateCommand())
{
    verifyStoredProcedureCall.CommandText = "SELECT title FROM cassie_dotnet_live_call_probe WHERE title = 'ado-stored-procedure-call'";
    if (!Equals(await verifyStoredProcedureCall.ExecuteScalarAsync(), "ado-stored-procedure-call"))
    {
        throw new InvalidOperationException("CommandType.StoredProcedure did not bind its input parameters positionally.");
    }
}

using (CassieCommand verifyPreparedStoredProcedureCall = (CassieCommand)connection.CreateCommand())
{
    verifyPreparedStoredProcedureCall.CommandText = "SELECT title FROM cassie_dotnet_live_call_probe WHERE title = 'ado-stored-procedure-call-updated'";
    if (!Equals(await verifyPreparedStoredProcedureCall.ExecuteScalarAsync(), "ado-stored-procedure-call-updated"))
    {
        throw new InvalidOperationException("Prepared CommandType.StoredProcedure did not bind the changed input value.");
    }
}

using (CassieCommand dropProcedure = (CassieCommand)connection.CreateCommand())
{
    dropProcedure.CommandText = "DROP PROCEDURE cassie_dotnet_live_store";
    _ = await dropProcedure.ExecuteNonQueryAsync();
}

using (CassieCommand dropCallTable = (CassieCommand)connection.CreateCommand())
{
    dropCallTable.CommandText = "DROP TABLE cassie_dotnet_live_call_probe";
    _ = await dropCallTable.ExecuteNonQueryAsync();
}

using (CassieCommand command = (CassieCommand)connection.CreateCommand())
{
    command.CommandText = "SELECT @blob::bytea AS blob, @text::text AS text";
    _ = command.Parameters.Add(new CassieParameter { ParameterName = "blob", Value = new byte[] { 0, 1, 255 } });
    _ = command.Parameters.Add(new CassieParameter { ParameterName = "text", Value = "stream access" });
    await using DbDataReader reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
    {
        throw new InvalidOperationException("Cassie large-value stream probe returned no row.");
    }

    await using Stream stream = reader.GetStream(0);
    using TextReader textReader = reader.GetTextReader(1);
    byte[] bytes = new byte[3];
    if (await stream.ReadAsync(bytes.AsMemory()) != 3
        || !bytes.SequenceEqual(new byte[] { 0, 1, 255 })
        || await textReader.ReadToEndAsync() != "stream access")
    {
        throw new InvalidOperationException("Cassie binary/text stream access returned unexpected values.");
    }
}

using (CassieCommand command = (CassieCommand)connection.CreateCommand())
{
    command.CommandText = "SELECT 1 AS value UNION ALL SELECT 2 AS value";
    await using DbDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow);
    if (!await reader.ReadAsync() || reader.GetInt32(0) != 1 || await reader.ReadAsync())
    {
        throw new InvalidOperationException("Cassie CommandBehavior.SingleRow returned an unexpected row sequence.");
    }
}

using (CassieCommand command = (CassieCommand)connection.CreateCommand())
{
    command.CommandText = "SELECT 11 AS value; SELECT 12 AS value";
    await using DbDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult);
    if (!await reader.ReadAsync() || reader.GetInt32(0) != 11 || await reader.NextResultAsync() || reader.FieldCount != 0)
    {
        throw new InvalidOperationException("Cassie CommandBehavior.SingleResult exposed an unexpected result set.");
    }
}

using (CassieCommand command = (CassieCommand)connection.CreateCommand())
{
    command.CommandText = "SELECT 1 AS first_value, 2 AS second_value UNION ALL SELECT 3, 4";
    await using DbDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
    if (!await reader.ReadAsync() || reader.GetInt32(1) != 2)
    {
        throw new InvalidOperationException("Cassie SequentialAccess failed to read the later column.");
    }

    bool backwardsReadRejected = false;
    try
    {
        _ = reader.GetInt32(0);
    }
    catch (InvalidOperationException)
    {
        backwardsReadRejected = true;
    }

    if (!backwardsReadRejected
        || !await reader.ReadAsync()
        || reader.GetInt32(0) != 3
        || await reader.ReadAsync())
    {
        throw new InvalidOperationException("Cassie SequentialAccess did not enforce column order or reset it for the next row.");
    }
}

using (CassieCommand command = (CassieCommand)connection.CreateCommand())
{
    command.CommandText = "SELECT 13 AS value";
    if (!Equals(await command.ExecuteScalarAsync(), 13))
    {
        throw new InvalidOperationException("Cassie connection was not reusable after CommandBehavior.SingleResult.");
    }
}

await using (CassieConnection closeOnReaderConnection = new() { ConnectionString = connectionString })
{
    await closeOnReaderConnection.OpenAsync();
    using CassieCommand command = (CassieCommand)closeOnReaderConnection.CreateCommand();
    command.CommandText = "SELECT 1 AS value";
    await using DbDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.CloseConnection);
    await reader.CloseAsync();
    if (closeOnReaderConnection.State != ConnectionState.Closed)
    {
        throw new InvalidOperationException("Cassie CommandBehavior.CloseConnection left its connection open after reader disposal.");
    }
}

using (CassieCommand select = (CassieCommand)connection.CreateCommand())
using (CassieDataAdapter adapter = new(select))
{
    select.CommandText = "SELECT 7 AS id, 'seven' AS label";
    using DataTable rows = new("probe");
    _ = adapter.Fill(rows);
    if (rows.Rows.Count != 1
        || Convert.ToInt32(rows.Rows[0]["id"], System.Globalization.CultureInfo.InvariantCulture) != 7
        || !Equals(rows.Rows[0]["label"], "seven"))
    {
        throw new InvalidOperationException("Cassie DataAdapter Fill returned unexpected rows.");
    }

    using DataSet dataSet = new();
    _ = adapter.Fill(dataSet, "probe");
    DataTable dataSetRows = dataSet.Tables["probe"] ?? throw new InvalidOperationException("Cassie DataAdapter did not create the requested DataSet table.");
    if (dataSetRows.Rows.Count != 1 || !Equals(dataSetRows.Rows[0]["label"], "seven"))
    {
        throw new InvalidOperationException("Cassie DataAdapter DataSet Fill returned unexpected rows.");
    }
}

using (CassieCommand command = (CassieCommand)connection.CreateCommand())
{
    command.CommandText = "SELECT 1 AS no_rows WHERE false";
    await using DbDataReader reader = await command.ExecuteReaderAsync();
    if (reader.FieldCount != 1 || reader.HasRows || await reader.ReadAsync())
    {
        throw new InvalidOperationException("Cassie empty-result reader behavior was unexpected.");
    }
}

if (string.Equals(Environment.GetEnvironmentVariable("CASSIE_DOTNET_TEST_NAME_TYPE"), "true", StringComparison.OrdinalIgnoreCase))
{
    using (CassieCommand command = (CassieCommand)connection.CreateCommand())
    {
        command.BinaryResults = true;
        command.CommandText = "SELECT @value::name AS name, @values::name[] AS names";
        _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", Value = "catalog_name", PostgresTypeOid = 19 });
        _ = command.Parameters.Add(new CassieParameter { ParameterName = "values", Value = new string?[] { "alpha", null, "βeta", "b,eta", "say \"hi\"", "slash\\name" }, PostgresTypeOid = 1003 });
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()
            || reader.GetString(0) != "catalog_name"
            || reader.GetValue(1) is not CassieArray { ElementTypeOid: 19, Rank: 1, Count: 6 } names
            || !Equals(names[0], "alpha")
            || names[1] is not null
            || !Equals(names[2], "βeta")
            || !Equals(names[3], "b,eta")
            || !Equals(names[4], "say \"hi\"")
            || !Equals(names[5], "slash\\name"))
        {
            throw new InvalidOperationException("Cassie binary NAME and NAME[] parameter/result probe returned unexpected values.");
        }
    }

    using (CassieCommand command = (CassieCommand)connection.CreateCommand())
    {
        command.CommandText = "SELECT @values::name[] AS names";
        _ = command.Parameters.Add(new CassieParameter { ParameterName = "values", Value = new string?[] { "alpha", "b,eta", "say \"hi\"", "slash\\name", null }, PostgresTypeOid = 1003, ParameterFormat = CassieParameterFormat.Text });
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync() || reader.GetString(0) != "[\"alpha\",\"b,eta\",\"say \\\"hi\\\"\",\"slash\\\\name\",null]")
        {
            throw new InvalidOperationException("Cassie text NAME[] parameter probe returned an unexpected value.");
        }
    }

    using (CassieCommand command = (CassieCommand)connection.CreateCommand())
    {
        command.CommandText = "SELECT @value::name AS name";
        _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", Value = new string('x', 70), PostgresTypeOid = 19, ParameterFormat = CassieParameterFormat.Text });
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync() || reader.GetString(0) != new string('x', 63))
        {
            throw new InvalidOperationException("Cassie text NAME truncation parameter probe returned an unexpected value.");
        }
    }
}

using (CassieCommand command = (CassieCommand)connection.CreateCommand())
{
    command.BinaryResults = true;
    command.CommandText = "SELECT 42::int4 AS i, true AS b, '550e8400-e29b-41d4-a716-446655440001'::uuid AS u, DATE '2000-01-02' AS d";
    await using DbDataReader reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync()
        || reader.GetInt32(0) != 42
        || !reader.GetBoolean(1)
        || reader.GetGuid(2) != Guid.Parse("550e8400-e29b-41d4-a716-446655440001")
        || reader.GetValue(3) is not DateOnly { Day: 2, Month: 1, Year: 2000 })
    {
        throw new InvalidOperationException("Cassie binary result probe returned an unexpected row.");
    }
}

using (CassieCommand command = (CassieCommand)connection.CreateCommand())
{
    command.BinaryResults = true;
    command.CommandText = "SELECT @relation::regclass AS relation";
    _ = command.Parameters.Add(new CassieParameter
    {
        ParameterName = "relation",
        Value = 1259u,
        PostgresTypeOid = 2205,
        ParameterFormat = CassieParameterFormat.Binary
    });
    await using DbDataReader reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync() || reader.GetDataTypeName(0) != "regclass"
        || reader.GetFieldType(0) != typeof(uint) || !Equals(reader.GetValue(0), 1259u))
    {
        throw new InvalidOperationException("Cassie binary regclass parameter/result probe returned an unexpected value or descriptor.");
    }
}

using (CassieCommand command = (CassieCommand)connection.CreateCommand())
{
    command.BinaryResults = true;
    command.CommandText = "SELECT @vector AS vector";
    _ = command.Parameters.Add(new CassieParameter { ParameterName = "vector", Value = new CassieVector(1, 2, 3) });
    await using DbDataReader reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync()
        || reader.GetValue(0) is not CassieVector vector
        || vector.Dimension != 3
        || vector[0] != 1f
        || vector[1] != 2f
        || vector[2] != 3f)
    {
        throw new InvalidOperationException("Cassie binary vector parameter/result probe returned unexpected components.");
    }
}

if (string.Equals(Environment.GetEnvironmentVariable("CASSIE_DOTNET_TEST_ARRAYS"), "true", StringComparison.OrdinalIgnoreCase))
{
    using (CassieCommand command = (CassieCommand)connection.CreateCommand())
    {
        command.BinaryResults = true;
        command.CommandText = "SELECT @addresses AS values";
        CassieNetworkAddress[] addresses =
        [
            new(System.Net.IPAddress.Parse("192.0.2.9"), 24, false),
            new(System.Net.IPAddress.Parse("192.0.2.10"), 32, false)
        ];
        _ = command.Parameters.Add(new CassieParameter { ParameterName = "addresses", Value = addresses, ParameterFormat = CassieParameterFormat.Text });
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()
            || reader.GetValue(0) is not CassieArray { ElementTypeOid: 869, Rank: 1, Count: 2 } values
            || values[0] is not CassieNetworkAddress first
            || first.ToString() != "192.0.2.9/24"
            || values[1] is not CassieNetworkAddress second
            || second.ToString() != "192.0.2.10/32")
        {
            throw new InvalidOperationException("Cassie text network-array parameter returned unexpected values.");
        }
    }

    using (CassieCommand command = (CassieCommand)connection.CreateCommand())
    {
        command.BinaryResults = true;
        command.CommandText = "SELECT ARRAY[7,NULL,9]::int4[] AS values";
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()
            || reader.GetValue(0) is not CassieArray { Rank: 1, Count: 3 } values
            || values.Dimensions[0] != 3
            || values.LowerBounds[0] != 1
            || !Equals(values[0], 7)
            || values[1] is not null
            || !Equals(values[2], 9))
        {
            throw new InvalidOperationException("Cassie binary array result probe returned unexpected shape, bounds, or values.");
        }
    }

    using (CassieCommand command = (CassieCommand)connection.CreateCommand())
    {
        command.BinaryResults = true;
        command.CommandText = "SELECT ARRAY[1.25,NULL,-2.5]::numeric[] AS values";
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()
            || reader.GetValue(0) is not CassieArray { ElementTypeOid: 1700, Rank: 1, Count: 3 } values
            || !Equals(values[0], new CassieNumeric("1.25"))
            || values[1] is not null
            || !Equals(values[2], new CassieNumeric("-2.5")))
        {
            throw new InvalidOperationException("Cassie binary NUMERIC array result probe returned unexpected values.");
        }
    }

    using (CassieCommand command = (CassieCommand)connection.CreateCommand())
    {
        command.BinaryResults = true;
        command.CommandText = "SELECT @numbers::numeric[] AS numbers, @ids::uuid[] AS ids, @flags::bool[] AS flags, @blobs::bytea[] AS blobs, @labels::text[] AS labels";
        Guid id = Guid.Parse("550e8400-e29b-41d4-a716-446655440001");
        _ = command.Parameters.Add(new CassieParameter { ParameterName = "numbers", DbType = DbType.Decimal, Value = new decimal?[] { 1.25m, null, -2.5m } });
        _ = command.Parameters.Add(new CassieParameter { ParameterName = "ids", DbType = DbType.Guid, Value = new Guid?[] { id, null } });
        _ = command.Parameters.Add(new CassieParameter { ParameterName = "flags", DbType = DbType.Boolean, Value = new bool?[] { true, null, false } });
        _ = command.Parameters.Add(new CassieParameter { ParameterName = "blobs", DbType = DbType.Binary, Value = new byte[]?[] { [0, 1, 255], null } });
        _ = command.Parameters.Add(new CassieParameter { ParameterName = "labels", DbType = DbType.String, Value = new string?[] { "alpha", null, "beta" } });
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()
            || !MatchesArray(reader.GetValue(0), 1700, new CassieNumeric("1.25"), null, new CassieNumeric("-2.5"))
            || !MatchesArray(reader.GetValue(1), 2950, id, null)
            || !MatchesArray(reader.GetValue(2), 16, true, null, false)
            || !MatchesArray(reader.GetValue(3), 17, new byte[] { 0, 1, 255 }, null)
            || !MatchesArray(reader.GetValue(4), 25, "alpha", null, "beta"))
        {
            throw new InvalidOperationException("Cassie binary array parameter/result probe returned unexpected values.");
        }
    }

    using (CassieCommand command = (CassieCommand)connection.CreateCommand())
    {
        command.BinaryResults = true;
        command.CommandText = "SELECT @values::numeric[] AS values";
        _ = command.Parameters.Add(new CassieParameter { ParameterName = "values", Value = numericArray });
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()
            || reader.GetValue(0) is not CassieArray { ElementTypeOid: 1700, Rank: 1, Count: 2 } values
            || !Equals(values[0], new CassieNumeric("1.25"))
            || !Equals(values[1], new CassieNumeric("2.5")))
        {
            throw new InvalidOperationException("Cassie numeric-array parameter/result probe returned unexpected values.");
        }
    }

    using (CassieCommand command = (CassieCommand)connection.CreateCommand())
    {
        command.BinaryResults = true;
        command.CommandText = "SELECT @numbers::numeric[] AS numbers, @times::time[] AS times";
        TimeSpan time = new TimeSpan(1, 2, 3).Add(TimeSpan.FromTicks(4_567_890));
        _ = command.Parameters.Add(new CassieParameter { ParameterName = "numbers", Value = new ulong?[] { ulong.MaxValue, null } });
        _ = command.Parameters.Add(new CassieParameter { ParameterName = "times", Value = new TimeSpan?[] { time, null } });
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()
            || !MatchesArray(reader.GetValue(0), 1700, new CassieNumeric("18446744073709551615"), null)
            || !MatchesArray(reader.GetValue(1), 1083, TimeOnly.FromTimeSpan(time), null))
        {
            throw new InvalidOperationException("Cassie UInt64 and TimeSpan binary array parameter probes returned unexpected values.");
        }
    }

}

using (CassieCommand command = (CassieCommand)connection.CreateCommand())
{
    command.BinaryResults = true;
    command.CommandText = "SELECT 4503599627370496.5628::numeric AS value";
    await using DbDataReader reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync()
        || reader.GetValue(0) is not CassieNumeric value
        || value.Value != "4503599627370496.5628")
    {
        throw new InvalidOperationException("Cassie binary NUMERIC result probe returned an unexpected exact value.");
    }

    decimal decimalValue = await reader.GetFieldValueAsync<decimal>(0);
    if (decimalValue != 4503599627370496.5628m)
    {
        throw new InvalidOperationException("Cassie generic decimal result probe returned an unexpected value.");
    }
}

if (string.Equals(Environment.GetEnvironmentVariable("CASSIE_DOTNET_TEST_JSONPATH_TYPE"), "true", StringComparison.OrdinalIgnoreCase))
{
    using CassieCommand command = (CassieCommand)connection.CreateCommand();
    command.BinaryResults = true;
    command.CommandText = "SELECT @path::jsonpath AS path";
    _ = command.Parameters.Add(new CassieParameter { ParameterName = "path", Value = "$.foo", PostgresTypeOid = 4072 });
    await using DbDataReader reader = await command.ExecuteReaderAsync();
    string? actual = await reader.ReadAsync() ? reader.GetString(0) : null;
    if (actual != "$.\"foo\"")
    {
        throw new InvalidOperationException($"Cassie binary JSONPath parameter/result probe returned unexpected text '{actual}'.");
    }
}

using (CassieCommand command = (CassieCommand)connection.CreateCommand())
{
    command.BinaryResults = true;
    command.CommandText = "SELECT '192.0.2.9/24'::inet AS inet, '192.0.2.0/24'::cidr AS cidr, '01:23:45:67:89:ab'::macaddr AS mac, B'10101'::varbit AS bits, '1/A'::pg_lsn AS lsn";
    await using DbDataReader reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync()
        || reader.GetValue(0) is not CassieNetworkAddress inet || inet.ToString() != "192.0.2.9/24"
        || reader.GetValue(1) is not CassieNetworkAddress cidr || cidr.ToString() != "192.0.2.0/24" || !cidr.IsCidr
        || reader.GetValue(2) is not System.Net.NetworkInformation.PhysicalAddress mac || !mac.GetAddressBytes().SequenceEqual(new byte[] { 1, 0x23, 0x45, 0x67, 0x89, 0xab })
        || reader.GetValue(3) is not CassieBitString { Value: "10101", IsVarying: true }
        || reader.GetValue(4) is not CassieLsn { Value: 0x000000010000000aUL })
    {
        throw new InvalidOperationException("Cassie network, MAC, bit-string, or LSN binary results were unexpected.");
    }
}

using (CassieCommand command = (CassieCommand)connection.CreateCommand())
{
    command.BinaryResults = true;
    command.CommandText = "SELECT @inet::inet AS inet, @cidr::cidr AS cidr, @mac::macaddr AS mac, @bits::varbit AS bits, @lsn::pg_lsn AS lsn, @timetz::timetz AS timetz, @interval::interval AS interval";
    _ = command.Parameters.Add(new CassieParameter { ParameterName = "inet", Value = new CassieNetworkAddress(System.Net.IPAddress.Parse("192.0.2.9"), 24, false) });
    _ = command.Parameters.Add(new CassieParameter { ParameterName = "cidr", Value = new CassieNetworkAddress(System.Net.IPAddress.Parse("192.0.2.0"), 24, true) });
    _ = command.Parameters.Add(new CassieParameter { ParameterName = "mac", Value = System.Net.NetworkInformation.PhysicalAddress.Parse("0123456789AB") });
    _ = command.Parameters.Add(new CassieParameter { ParameterName = "bits", Value = new CassieBitString("10101", true) });
    _ = command.Parameters.Add(new CassieParameter { ParameterName = "lsn", Value = new CassieLsn(0x000000010000000aUL) });
    _ = command.Parameters.Add(new CassieParameter { ParameterName = "timetz", Value = new CassieTimeWithTimeZone(new TimeOnly(1, 2, 3), 3600) });
    _ = command.Parameters.Add(new CassieParameter { ParameterName = "interval", Value = new CassieInterval(14, -2, 1_500_000) });
    await using DbDataReader reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync()
        || reader.GetValue(0) is not CassieNetworkAddress { IsCidr: false, PrefixLength: 24 }
        || reader.GetValue(1) is not CassieNetworkAddress { IsCidr: true, PrefixLength: 24 }
        || reader.GetValue(2) is not System.Net.NetworkInformation.PhysicalAddress mac || mac.GetAddressBytes().Length != 6
        || reader.GetValue(3) is not CassieBitString { Value: "10101", IsVarying: true }
        || reader.GetValue(4) is not CassieLsn { Value: 0x000000010000000aUL }
        || reader.GetValue(5) is not CassieTimeWithTimeZone { Time: var time, OffsetSeconds: 3600 } || time != new TimeOnly(1, 2, 3)
        || reader.GetValue(6) is not CassieInterval { Months: 14, Days: -2, Microseconds: 1_500_000 })
    {
        throw new InvalidOperationException("Cassie binary extended parameter/result round trip returned unexpected values.");
    }
}

using (CassieCommand command = (CassieCommand)connection.CreateCommand())
{
    command.CommandText = "SELECT @value::numeric AS value";
    _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", Value = new CassieNumeric("9007199254740993.1255") });
    await using DbDataReader reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync() || reader.GetValue(0) is not CassieNumeric value || value.Value != "9007199254740993.1255")
    {
        throw new InvalidOperationException("Cassie binary NUMERIC parameter probe returned an unexpected exact value.");
    }
}

using (CassieCommand command = (CassieCommand)connection.CreateCommand())
{
    command.BinaryResults = true;
    command.CommandText = "SELECT @inferred::numeric AS inferred, @typed::numeric AS typed";
    _ = command.Parameters.Add(new CassieParameter { ParameterName = "inferred", Value = ulong.MaxValue });
    _ = command.Parameters.Add(new CassieParameter { ParameterName = "typed", DbType = DbType.UInt64, Value = ulong.MaxValue });
    await using DbDataReader reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
    {
        throw new InvalidOperationException("Cassie UInt64 binary NUMERIC parameter probe returned no row.");
    }

    ulong inferredUInt64 = await reader.GetFieldValueAsync<ulong>(0);
    ulong typedUInt64 = await reader.GetFieldValueAsync<ulong>(1);
    if (reader.GetValue(0) is not CassieNumeric inferred || inferred.Value != "18446744073709551615"
        || reader.GetValue(1) is not CassieNumeric typed || typed.Value != "18446744073709551615"
        || inferredUInt64 != ulong.MaxValue || typedUInt64 != ulong.MaxValue)
    {
        throw new InvalidOperationException("Cassie UInt64 binary NUMERIC parameter probe returned unexpected scalar values.");
    }
}

using (CassieCommand command = (CassieCommand)connection.CreateCommand())
{
    command.BinaryResults = true;
    command.CommandText = "SELECT @inferred::time AS inferred, @typed::time AS typed";
    TimeSpan expected = new TimeSpan(1, 2, 3).Add(TimeSpan.FromTicks(4_567_890));
    _ = command.Parameters.Add(new CassieParameter { ParameterName = "inferred", Value = expected });
    _ = command.Parameters.Add(new CassieParameter { ParameterName = "typed", DbType = DbType.Time, Value = expected });
    await using DbDataReader reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
    {
        throw new InvalidOperationException("Cassie TimeSpan binary TIME parameter probe returned no row.");
    }

    TimeSpan inferredTimeSpan = await reader.GetFieldValueAsync<TimeSpan>(0);
    TimeSpan typedTimeSpan = await reader.GetFieldValueAsync<TimeSpan>(1);
    if (reader.GetValue(0) is not TimeOnly inferred || inferred.ToTimeSpan() != expected
        || inferredTimeSpan != expected
        || reader.GetValue(1) is not TimeOnly typed || typed.ToTimeSpan() != expected
        || typedTimeSpan != expected)
    {
        throw new InvalidOperationException("Cassie TimeSpan binary TIME parameter probe returned unexpected values.");
    }
}

using (CassieCommand command = (CassieCommand)connection.CreateCommand())
{
    command.BinaryResults = true;
    command.CommandText = "SELECT @value::date AS value";
    _ = command.Parameters.Add(new CassieParameter
    {
        ParameterName = "value",
        DbType = DbType.Date,
        Value = new DateTime(2000, 1, 2, 23, 59, 59, DateTimeKind.Utc)
    });
    await using DbDataReader reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync() || reader.GetValue(0) is not DateOnly date || date != new DateOnly(2000, 1, 2))
    {
        throw new InvalidOperationException("Cassie DbType.Date DateTime parameter probe returned an unexpected value.");
    }
}

using (CassieCommand command = (CassieCommand)connection.CreateCommand())
{
    command.CommandText = "SELECT @value::int4";
    _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", Value = 123 });
    await using DbDataReader reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync() || reader.GetInt32(0) != 123)
    {
        throw new InvalidOperationException("Cassie binary parameter probe returned an unexpected row.");
    }
}

using (CassieCommand command = (CassieCommand)connection.CreateCommand())
{
    command.BinaryResults = true;
    command.CommandText = "SELECT @document AS document";
    _ = command.Parameters.Add(new CassieParameter { ParameterName = "document", Value = /*lang=json,strict*/ "{\"answer\":42}", PostgresTypeOid = 114 });
    await using DbDataReader reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync() || reader.GetString(0) != /*lang=json,strict*/ "{\"answer\":42}")
    {
        throw new InvalidOperationException("Cassie JSON OID 114 binary parameter/result probe returned an unexpected document.");
    }
}

if (string.Equals(Environment.GetEnvironmentVariable("CASSIE_DOTNET_TEST_JSONB_TYPE"), "true", StringComparison.OrdinalIgnoreCase))
{
    using CassieCommand command = (CassieCommand)connection.CreateCommand();
    command.BinaryResults = true;
    command.CommandText = "SELECT @document::jsonb AS document";
    _ = command.Parameters.Add(new CassieParameter { ParameterName = "document", Value = /*lang=json,strict*/ "{\"answer\":42}", PostgresTypeOid = 3802 });
    await using DbDataReader reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync() || reader.GetString(0) != /*lang=json,strict*/ "{\"answer\": 42}")
    {
        throw new InvalidOperationException("Cassie JSONB OID 3802 binary parameter/result probe returned an unexpected document.");
    }
}

using (CassieCommand command = (CassieCommand)connection.CreateCommand())
{
    command.CommandText = "DROP TABLE IF EXISTS cassie_dotnet_live_probe";
    _ = await command.ExecuteNonQueryAsync();
}

using (CassieCommand command = (CassieCommand)connection.CreateCommand())
{
    command.CommandText = "CREATE TABLE cassie_dotnet_live_probe (id INT4, label TEXT)";
    _ = await command.ExecuteNonQueryAsync();
}

using (CassieCommand command = (CassieCommand)connection.CreateCommand())
{
    command.CommandText = "SELECT id, label, @requested::int4 AS requested FROM cassie_dotnet_live_probe";
    _ = command.Parameters.Add(new CassieParameter { ParameterName = "requested", DbType = DbType.Int32, Value = 7 });
    await using DbDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly);
    DataTable schema = await reader.GetSchemaTableAsync(CancellationToken.None).ConfigureAwait(false)
        ?? throw new InvalidOperationException("Cassie returned no reader schema table.");
    ReadOnlyCollection<DbColumn> columns = reader.GetColumnSchema();
    string schemaMetadata = string.Join(";", schema.Rows.Cast<DataRow>().Select(row => string.Join(",", row.ItemArray.Select(value => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)))));
    if (reader.FieldCount != 3
        || reader.GetName(0) != "id"
        || reader.GetFieldType(0) != typeof(int)
        || reader.GetName(1) != "label"
        || reader.GetFieldType(1) != typeof(string)
        || reader.GetName(2) != "requested"
        || reader.GetFieldType(2) != typeof(int)
        || schema.Rows.Count != 3
        || Convert.ToInt32(schema.Rows[0]["ProviderType"], System.Globalization.CultureInfo.InvariantCulture) != 23
        || !Equals(schema.Rows[0]["DataTypeName"], "int4")
        || Convert.ToInt32(schema.Rows[0]["ColumnSize"], System.Globalization.CultureInfo.InvariantCulture) != 4
        || schema.Rows[0]["IsExpression"] is not DBNull
        || schema.Rows[2]["IsExpression"] is not DBNull
        || columns.Count != 3
        || columns[0].ColumnName != "id"
        || columns[0].DataType != typeof(int)
        || columns[0].ColumnSize != 4
        || columns[0].IsExpression is not null
        || columns[2].IsExpression is not null
        || reader.HasRows
        || await reader.ReadAsync())
    {
        throw new InvalidOperationException($"Cassie CommandBehavior.SchemaOnly returned unexpected metadata or rows. Schema: {schemaMetadata}. DbColumns: {string.Join(";", columns.Select(column => $"{column.ColumnName}/{column.DataType}/{column.ColumnSize}/{column.IsExpression}"))}.");
    }
}

using (CassieCommand command = (CassieCommand)connection.CreateCommand())
{
    command.CommandText = "INSERT INTO cassie_dotnet_live_probe VALUES (99, 'schema-only')";
    await using DbDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly);
    if (reader.FieldCount != 0 || await reader.ReadAsync())
    {
        throw new InvalidOperationException("Cassie CommandBehavior.SchemaOnly executed or described a non-returning INSERT.");
    }
}

DataTable tableSchema = await connection.GetSchemaAsync("Tables", [null, "public", "cassie_dotnet_live_probe", "BASE TABLE"], CancellationToken.None).ConfigureAwait(false);
DataTable columnSchema = await connection.GetSchemaAsync("Columns", [null, "public", "cassie_dotnet_live_probe"], CancellationToken.None).ConfigureAwait(false);
if (tableSchema.Rows.Count != 1
    || !Equals(tableSchema.Rows[0]["TABLE_NAME"], "cassie_dotnet_live_probe")
    || columnSchema.Rows.Count != 2
    || !Equals(columnSchema.Rows[0]["COLUMN_NAME"], "id")
    || !Equals(columnSchema.Rows[1]["COLUMN_NAME"], "label")
    || Convert.ToInt32(columnSchema.Rows[0]["ORDINAL_POSITION"], System.Globalization.CultureInfo.InvariantCulture) != 1)
{
    throw new InvalidOperationException("Cassie GetSchema table/column metadata returned unexpected results.");
}

using (CassieCommand command = (CassieCommand)connection.CreateCommand())
{
    command.CommandText = "COPY cassie_dotnet_live_probe FROM STDIN WITH (FORMAT CSV)";
    await using MemoryStream source = new("1,alpha\n2,beta\n"u8.ToArray());
    if (await command.CopyFromAsync(source) != 2)
    {
        throw new InvalidOperationException("Cassie COPY FROM returned an unexpected row count.");
    }
}

using (CassieCommand command = (CassieCommand)connection.CreateCommand())
{
    command.CommandText = "COPY (SELECT id, label FROM cassie_dotnet_live_probe ORDER BY id) TO STDOUT WITH (FORMAT CSV)";
    await using MemoryStream destination = new();
    int copied = await command.CopyToAsync(destination);
    string copiedText = Encoding.UTF8.GetString(destination.ToArray());
    if (copied != 2 || copiedText != "1,alpha\n2,beta\n")
    {
        throw new InvalidOperationException($"Cassie COPY TO returned unexpected bytes or row count ({copied}: {copiedText}).");
    }
}

VerifyDataAdapterUpdate(connection);
await VerifyPortalPagingAsync(connection);

await using (DbTransaction transaction = await connection.BeginTransactionAsync())
{
    using CassieCommand command = (CassieCommand)connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = "INSERT INTO cassie_dotnet_live_probe VALUES (3, 'gamma')";
    _ = await command.ExecuteNonQueryAsync();
    await transaction.RollbackAsync();
}

using (CassieCommand command = (CassieCommand)connection.CreateCommand())
{
    command.CommandText = "SELECT count(*) FROM cassie_dotnet_live_probe";
    object? count = await command.ExecuteScalarAsync();
    if (!Equals(count, 3L))
    {
        throw new InvalidOperationException("Cassie transaction rollback did not remove the inserted row.");
    }
}

await VerifyDatabaseImageRoundTripAsync(connection, connectionString);
await VerifyPoolingAsync(connectionString);
await VerifyPoolingStartupOptionsAsync(connectionString);
await VerifyActiveQueryCancellationAsync(connection);

static async Task VerifyExtendedTextParameterFormatsAsync(CassieConnection connection)
{
    CassieTimeWithTimeZone timeWithTimeZone = new(new TimeOnly(3, 4, 5).Add(TimeSpan.FromTicks(60_070)), 7200);
    (object Value, object Expected)[] cases =
    [
        (new CassieNetworkAddress(System.Net.IPAddress.Parse("192.0.2.9"), 24, false), "192.0.2.9/24"),
        (new CassieNetworkAddress(System.Net.IPAddress.Parse("192.0.2.0"), 24, true), "192.0.2.0/24"),
        (System.Net.NetworkInformation.PhysicalAddress.Parse("08002b010203"), "08:00:2b:01:02:03"),
        (System.Net.NetworkInformation.PhysicalAddress.Parse("08002b0102030405"), "08:00:2b:01:02:03:04:05"),
        (new CassieBitString("10101", false), "10101"),
        (new CassieBitString("10101", true), "10101"),
        (new CassieLsn(0x000000010000000aUL), "1/A"),
        (new CassieInterval(1, 2, 11_045_006_007), "1 mon 2 days 03:04:05.006007"),
        (timeWithTimeZone, timeWithTimeZone),
        (new CassieVector(1, 2, 3), "[1,2,3]")
    ];

    foreach ((object value, object expected) in cases)
    {
        using CassieCommand command = (CassieCommand)connection.CreateCommand();
        command.CommandText = "SELECT @value";
        _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", Value = value, ParameterFormat = CassieParameterFormat.Text });
        object? actual = await command.ExecuteScalarAsync().ConfigureAwait(false);
        if (!Equals(actual, expected))
        {
            throw new InvalidOperationException("Cassie text parameter round trip returned an unexpected value.");
        }
    }
}

static async Task VerifyTimestampParameterFormatsAsync(CassieConnection connection)
{
    DateTime expected = new DateTime(2000, 1, 2, 1, 2, 3, 456, DateTimeKind.Unspecified).AddTicks(7_890);
    DateTime localValue = DateTime.SpecifyKind(expected, DateTimeKind.Local);
    using (CassieCommand command = (CassieCommand)connection.CreateCommand())
    {
        command.BinaryResults = true;
        command.CommandText = "SELECT @value::timestamp AS value";
        _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", Value = localValue, ParameterFormat = CassieParameterFormat.Binary });
        await using DbDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false) || !Equals(reader.GetValue(0), expected))
        {
            throw new InvalidOperationException("Cassie binary timestamp parameter did not preserve the DateTime wall clock.");
        }
    }

    using (CassieCommand command = (CassieCommand)connection.CreateCommand())
    {
        command.CommandText = "SELECT @value::timestamp AS value";
        _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", Value = DateTime.SpecifyKind(expected, DateTimeKind.Utc), ParameterFormat = CassieParameterFormat.Text });
        await using DbDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false) || !Equals(reader.GetValue(0), expected))
        {
            throw new InvalidOperationException("Cassie text timestamp parameter did not preserve the DateTime wall clock.");
        }
    }

}

static async Task VerifyDateTimeReaderConversionsAsync(CassieConnection connection)
{
    DateTime expectedDate = new(2000, 1, 2, 0, 0, 0, DateTimeKind.Unspecified);
    DateTime expectedTimestamp = new(2000, 1, 2, 1, 2, 3, 4, DateTimeKind.Unspecified);
    DateTime expectedTimestampTz = new(2000, 1, 2, 1, 2, 3, 4, DateTimeKind.Utc);

    foreach (bool binaryResults in new[] { false, true })
    {
        using CassieCommand command = (CassieCommand)connection.CreateCommand();
        command.CommandText = "SELECT DATE '2000-01-02' AS date_value, TIMESTAMP '2000-01-02 01:02:03.004' AS timestamp_value, TIMESTAMPTZ '2000-01-02 01:02:03.004+00:00' AS timestamptz_value";
        command.BinaryResults = binaryResults;
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()
            || reader.GetDateTime(0) != expectedDate
            || await reader.GetFieldValueAsync<DateTime>(0).ConfigureAwait(false) != expectedDate
            || reader.GetDateTime(1) != expectedTimestamp
            || await reader.GetFieldValueAsync<DateTime>(1).ConfigureAwait(false) != expectedTimestamp
            || reader.GetDateTime(2) != expectedTimestampTz
            || await reader.GetFieldValueAsync<DateTime>(2).ConfigureAwait(false) != expectedTimestampTz
            || reader.GetDateTime(0).Kind != DateTimeKind.Unspecified
            || reader.GetDateTime(1).Kind != DateTimeKind.Unspecified
            || reader.GetDateTime(2).Kind != DateTimeKind.Utc)
        {
            throw new InvalidOperationException($"Cassie {(binaryResults ? "binary" : "text")} date/time reader getters returned an unexpected value or DateTime kind.");
        }
    }
}

static async Task VerifyTemporalSpecialValueConversionsAsync(CassieConnection connection)
{
    CassieTemporalValue[] expectedValues =
    [
        CassieTemporalValue.FromDateDays(1_000_000_000),
        CassieTemporalValue.FromTimeMicroseconds(86_400_000_000),
        CassieTemporalValue.FromTimeWithTimeZoneMicroseconds(86_400_000_000, 3600),
        CassieTemporalValue.FromTimestampMicroseconds(300_000_000_000_000_000),
        CassieTemporalValue.FromTimestampWithTimeZoneMicroseconds(long.MaxValue)
    ];
    int[] typeOids = [1082, 1083, 1266, 1114, 1184];
    foreach (bool binaryResults in new[] { false, true })
    {
        using CassieCommand command = (CassieCommand)connection.CreateCommand();
        command.BinaryResults = binaryResults;
        command.CommandText = "SELECT @value0::date AS value0, @value1::time AS value1, @value2::timetz AS value2, @value3::timestamp AS value3, @value4::timestamptz AS value4";
        for (int index = 0; index < expectedValues.Length; index++)
        {
            _ = command.Parameters.Add(new CassieParameter
            {
                ParameterName = $"value{index}",
                Value = expectedValues[index],
                PostgresTypeOid = typeOids[index],
                ParameterFormat = CassieParameterFormat.Binary
            });
        }

        await using DbDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false))
        {
            throw new InvalidOperationException("Cassie returned no temporal special-value row.");
        }

        for (int index = 0; index < expectedValues.Length; index++)
        {
            CassieTemporalValue actual = await reader.GetFieldValueAsync<CassieTemporalValue>(index).ConfigureAwait(false);
            CassieTemporalValue expected = expectedValues[index];
            bool validTextValue = actual.Kind == expected.Kind && actual.OffsetSeconds == expected.OffsetSeconds
                && (expected.IsInfinity
                    ? actual.IsInfinity && actual.Value == expected.Value
                    : expected.Kind is CassieTemporalKind.Time or CassieTemporalKind.TimeWithTimeZone
                        ? actual.HasRawValue && actual.Value == expected.Value
                        : !actual.HasRawValue && actual.TextValue is not null);
            if (binaryResults ? actual != expected : !validTextValue)
            {
                throw new InvalidOperationException($"Cassie {(binaryResults ? "binary" : "text")} temporal special-value conversion failed for OID {typeOids[index]}.");
            }
        }
    }
}

static async Task VerifyTemporalDateStyleDecodingAsync(CassieConnection connection)
{
    using (CassieCommand setDateStyle = (CassieCommand)connection.CreateCommand())
    {
        setDateStyle.CommandText = "SET DateStyle = 'SQL, DMY'";
        _ = await setDateStyle.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    DateOnly expectedDate = new(2000, 1, 2);
    DateTime expectedTimestamp = new DateTime(2000, 1, 2, 3, 4, 5, DateTimeKind.Unspecified).AddTicks(60);
    using (CassieCommand query = (CassieCommand)connection.CreateCommand())
    {
        query.CommandText = "SELECT DATE '2000-01-02' AS date_value, TIMESTAMP '2000-01-02 03:04:05.000006' AS timestamp_value";
        await using DbDataReader reader = await query.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false)
            || reader.GetValue(0) is not DateOnly date || date != expectedDate
            || reader.GetValue(1) is not DateTime timestamp || timestamp != expectedTimestamp)
        {
            throw new InvalidOperationException("Cassie SQL, DMY temporal text did not decode to the expected CLR values.");
        }
    }

    using CassieCommand resetDateStyle = (CassieCommand)connection.CreateCommand();
    resetDateStyle.CommandText = "SET DateStyle = 'ISO, MDY'";
    _ = await resetDateStyle.ExecuteNonQueryAsync().ConfigureAwait(false);
}

static async Task VerifyHiddenDateStyleParameterStatusAsync(CassieConnection connection)
{
    using (CassieCommand drop = (CassieCommand)connection.CreateCommand())
    {
        drop.CommandText = "DROP VIEW IF EXISTS cassie_dotnet_datestyle_status_view";
        _ = await drop.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    using (CassieCommand create = (CassieCommand)connection.CreateCommand())
    {
        create.CommandText = "CREATE VIEW cassie_dotnet_datestyle_status_view AS SELECT set_config('datestyle', 'SQL, DMY', false) AS changed";
        _ = await create.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    using (CassieCommand queryView = (CassieCommand)connection.CreateCommand())
    {
        queryView.CommandText = "SELECT changed, DATE '2000-01-02' AS value FROM cassie_dotnet_datestyle_status_view";
        await using DbDataReader reader = await queryView.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false)
            || !Equals(reader.GetValue(0), "SQL, DMY")
            || reader.GetValue(1) is not DateOnly date || date != new DateOnly(2000, 1, 2))
        {
            throw new InvalidOperationException("Cassie did not preserve statement-start DateStyle while executing a setting-changing view.");
        }
    }

    using (CassieCommand queryAfterView = (CassieCommand)connection.CreateCommand())
    {
        queryAfterView.CommandText = "SELECT DATE '2000-01-02' AS value";
        if (!Equals(await queryAfterView.ExecuteScalarAsync().ConfigureAwait(false), new DateOnly(2000, 1, 2)))
        {
            throw new InvalidOperationException("Cassie ParameterStatus from a stored view did not update DateStyle for the next query.");
        }
    }

    using (CassieCommand reset = (CassieCommand)connection.CreateCommand())
    {
        reset.CommandText = "SET DateStyle = 'ISO, MDY'";
        _ = await reset.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    using CassieCommand cleanup = (CassieCommand)connection.CreateCommand();
    cleanup.CommandText = "DROP VIEW cassie_dotnet_datestyle_status_view";
    _ = await cleanup.ExecuteNonQueryAsync().ConfigureAwait(false);
}

static async Task VerifyActiveQueryCancellationAsync(CassieConnection connection)
{
    using CassieCommand slowQuery = (CassieCommand)connection.CreateCommand();
    slowQuery.CommandText = "SELECT pg_sleep(10)";
    slowQuery.CommandTimeout = 5;
    Task<object?> execution = slowQuery.ExecuteScalarAsync();
    await Task.Delay(200).ConfigureAwait(false);
    slowQuery.Cancel();

    bool cancelled = false;
    try
    {
        _ = await execution.ConfigureAwait(false);
    }
    catch (CassieException exception) when (exception.SqlState == "57014")
    {
        cancelled = true;
    }

    using CassieCommand recovery = (CassieCommand)connection.CreateCommand();
    recovery.CommandText = "SELECT 73::int4";
    if (!cancelled || !Equals(await recovery.ExecuteScalarAsync().ConfigureAwait(false), 73))
    {
        throw new InvalidOperationException("Cassie query cancellation did not return SQLSTATE 57014 and leave the session reusable.");
    }
}

static async Task VerifyPortalPagingAsync(CassieConnection connection)
{
    using (CassieCommand drop = (CassieCommand)connection.CreateCommand())
    {
        drop.CommandText = "DROP TABLE IF EXISTS cassie_dotnet_portal_probe";
        _ = await drop.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    using (CassieCommand create = (CassieCommand)connection.CreateCommand())
    {
        create.CommandText = "CREATE TABLE cassie_dotnet_portal_probe (id INT4)";
        _ = await create.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    string csv = string.Join('\n', Enumerable.Range(1, 260)) + "\n";
    using (CassieCommand copy = (CassieCommand)connection.CreateCommand())
    {
        await using MemoryStream source = new(Encoding.UTF8.GetBytes(csv));
        copy.CommandText = "COPY cassie_dotnet_portal_probe FROM STDIN WITH (FORMAT CSV)";
        if (await copy.CopyFromAsync(source).ConfigureAwait(false) != 260)
        {
            throw new InvalidOperationException("Cassie portal paging probe failed to stage 260 rows.");
        }
    }

    using (CassieCommand select = (CassieCommand)connection.CreateCommand())
    {
        select.CommandText = "SELECT id, @marker::int4 AS marker FROM cassie_dotnet_portal_probe ORDER BY id";
        _ = select.Parameters.Add(new CassieParameter { ParameterName = "marker", Value = 7 });
        await using DbDataReader reader = await select.ExecuteReaderAsync().ConfigureAwait(false);
        int expectedId = 1;
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (reader.GetInt32(0) != expectedId || reader.GetInt32(1) != 7)
            {
                throw new InvalidOperationException("Cassie portal paging returned an unexpected row or lost a bound parameter.");
            }

            expectedId++;
        }

        if (expectedId != 261)
        {
            throw new InvalidOperationException($"Cassie portal paging returned {expectedId - 1} rows instead of 260.");
        }
    }

    using (CassieCommand drop = (CassieCommand)connection.CreateCommand())
    {
        drop.CommandText = "DROP TABLE cassie_dotnet_portal_probe";
        _ = await drop.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}

[UnconditionalSuppressMessage("Security", "CA2100", Justification = "The source database is validated as a simple ASCII identifier and the unique restore database is generated locally; database identifiers cannot be bound as query parameters.")]
static async Task VerifyDatabaseImageRoundTripAsync(CassieConnection connection, string connectionString)
{
    CassieConnectionStringBuilder sourceSettings = new(connectionString);
    if (!IsSimpleDatabaseName(sourceSettings.Database))
    {
        throw new InvalidOperationException("The database image probe requires a simple Cassie database name.");
    }

    string restoredDatabase = "cassie_dotnet_restore_" + Guid.NewGuid().ToString("N");

    using MemoryStream image = new();
    using (CassieCommand backup = (CassieCommand)connection.CreateCommand())
    {
        backup.CommandText = $"BACKUP DATABASE {sourceSettings.Database} TO STDOUT";
        if (await backup.CopyToAsync(image).ConfigureAwait(false) != 0 || image.Length == 0)
        {
            throw new InvalidOperationException("Cassie database backup returned no image or an unexpected row count.");
        }
    }

    image.Position = 0;
    using (CassieCommand restore = (CassieCommand)connection.CreateCommand())
    {
        restore.CommandText = $"RESTORE DATABASE {restoredDatabase} FROM STDIN";
        _ = await restore.CopyFromAsync(image).ConfigureAwait(false);
    }

    CassieConnectionStringBuilder restoredSettings = new(connectionString)
    {
        Database = restoredDatabase,
        Pooling = false
    };
    await using CassieConnection restoredConnection = new() { ConnectionString = restoredSettings.ConnectionString };
    await restoredConnection.OpenAsync().ConfigureAwait(false);
    using CassieCommand verify = (CassieCommand)restoredConnection.CreateCommand();
    verify.CommandText = "SELECT id, label FROM cassie_dotnet_live_probe ORDER BY id";
    await using DbDataReader reader = await verify.ExecuteReaderAsync().ConfigureAwait(false);
    (int Id, string Label)[] expected = [(1, "alpha"), (2, "beta"), (3, "gamma")];
    foreach ((int expectedId, string expectedLabel) in expected)
    {
        if (!await reader.ReadAsync().ConfigureAwait(false)
            || reader.GetInt32(0) != expectedId
            || reader.GetString(1) != expectedLabel)
        {
            throw new InvalidOperationException("Cassie database image restore did not preserve the selected table rows.");
        }
    }

    if (await reader.ReadAsync().ConfigureAwait(false))
    {
        throw new InvalidOperationException("Cassie database image restore returned unexpected extra table rows.");
    }
}

static bool IsSimpleDatabaseName(string value)
{
    if (value.Length == 0 || !(char.IsAsciiLetter(value[0]) || value[0] == '_'))
    {
        return false;
    }

    for (int index = 1; index < value.Length; index++)
    {
        if (!(char.IsAsciiLetterOrDigit(value[index]) || value[index] == '_'))
        {
            return false;
        }
    }

    return true;
}

[UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The opt-in probe constructs a fixed two-dimensional Int32 array with explicit lower bounds; the generic CassieArray runtime path is separately Native AOT qualified.")]
static async Task VerifyArrayShapeRoundTripAsync(CassieConnection connection)
{
    CassieArray shapedArray;
    using (CassieCommand selectArray = (CassieCommand)connection.CreateCommand())
    {
        CassieArray sourceArray = new(23, [2, 2], [0, 4], [1, 2, 3, 4]);
        selectArray.CommandText = "SELECT @values::int[] AS values";
        selectArray.BinaryResults = true;
        _ = selectArray.Parameters.Add(new CassieParameter { ParameterName = "values", Value = sourceArray, ParameterFormat = CassieParameterFormat.Text });
        await using DbDataReader reader = await selectArray.ExecuteReaderAsync();
        if (!await reader.ReadAsync() || reader.GetValue(0) is not CassieArray value
            || !value.Dimensions.SequenceEqual([2, 2]) || !value.LowerBounds.SequenceEqual([0, 4])
            || !value.SequenceEqual([1, 2, 3, 4]))
        {
            throw new InvalidOperationException("Cassie did not return the expected multidimensional array shape.");
        }

        shapedArray = value;
    }

    using (CassieCommand roundTripArray = (CassieCommand)connection.CreateCommand())
    {
        roundTripArray.CommandText = "SELECT @values::int[]::text AS values";
        _ = roundTripArray.Parameters.Add(new CassieParameter { ParameterName = "values", Value = shapedArray });
        if (!Equals(await roundTripArray.ExecuteScalarAsync(), "[0:1][4:5]={{1,2},{3,4}}"))
        {
            throw new InvalidOperationException("Cassie array parameter binding did not preserve dimensions, lower bounds, and values.");
        }
    }

    using CassieCommand binaryArray = (CassieCommand)connection.CreateCommand();
    Array clrArray = Array.CreateInstance(typeof(int), [2, 2], [0, 4]);
    clrArray.SetValue(5, 0, 4);
    clrArray.SetValue(6, 0, 5);
    clrArray.SetValue(7, 1, 4);
    clrArray.SetValue(8, 1, 5);
    binaryArray.CommandText = "SELECT @values::int[]::text AS values";
    _ = binaryArray.Parameters.Add(new CassieParameter { ParameterName = "values", Value = clrArray });
    if (!Equals(await binaryArray.ExecuteScalarAsync(), "[1:2][4:5]={{5,6},{7,8}}"))
    {
        throw new InvalidOperationException("Cassie binary array input did not preserve dimensions, lower bounds, and values.");
    }
}

[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The live qualification schema is fixed and CassieDataReader reports only CLR types rooted by its built-in PostgreSQL OID map; this exercises the framework DataAdapter.Update path against that known schema.")]
static void VerifyDataAdapterUpdate(CassieConnection connection)
{
    using DataTable updates = new("updates");
    using CassieDataAdapter adapter = new();
    using CassieCommand insert = (CassieCommand)connection.CreateCommand();
    _ = updates.Columns.Add("id", typeof(int));
    _ = updates.Columns.Add("label", typeof(string));
    DataRow row = updates.Rows.Add(3, "gamma");
    insert.CommandText = "INSERT INTO cassie_dotnet_live_probe (id, label) VALUES (@id, @label)";
    _ = insert.Parameters.Add(new CassieParameter { ParameterName = "id", SourceColumn = "id", DbType = DbType.Int32 });
    _ = insert.Parameters.Add(new CassieParameter { ParameterName = "label", SourceColumn = "label", DbType = DbType.String });
    adapter.InsertCommand = insert;
    if (adapter.Update(updates) != 1 || row.RowState != DataRowState.Unchanged)
    {
        throw new InvalidOperationException("Cassie DataAdapter Update did not insert and accept the changed row.");
    }
}

static async Task VerifyPoolingAsync(string baseConnectionString)
{
    CassieConnectionStringBuilder settings = new(baseConnectionString)
    {
        Pooling = true,
        MaxPoolSize = 1,
        ApplicationName = "Cassie .NET qualification",
        Options = string.Empty
    };
    string pooledConnectionString = settings.ConnectionString;

    await using (CassieConnection first = new() { ConnectionString = pooledConnectionString })
    {
        await first.OpenAsync();
        using CassieCommand poison = (CassieCommand)first.CreateCommand();
        poison.CommandText = "SET application_name = 'poisoned'";
        _ = await poison.ExecuteNonQueryAsync();
        await first.CloseAsync();
    }

    await using (CassieConnection second = new() { ConnectionString = pooledConnectionString })
    {
        await second.OpenAsync();
        using CassieCommand show = (CassieCommand)second.CreateCommand();
        show.CommandText = "SHOW application_name";
        if (!Equals(await show.ExecuteScalarAsync(), "Cassie .NET qualification"))
        {
            throw new InvalidOperationException("Pooled Cassie session retained modified session state.");
        }

        await second.CloseAsync();
        CassieConnection.ClearPool(second);
    }

    await using CassieConnection holder = new() { ConnectionString = pooledConnectionString };
    await using CassieConnection waiter = new() { ConnectionString = pooledConnectionString };
    await holder.OpenAsync();
    using CancellationTokenSource waitCancellation = new(TimeSpan.FromMilliseconds(100));
    bool canceled = false;
    try
    {
        await waiter.OpenAsync(waitCancellation.Token);
    }
    catch (OperationCanceledException) when (waitCancellation.IsCancellationRequested)
    {
        canceled = true;
    }

    if (!canceled || waiter.State != ConnectionState.Closed)
    {
        throw new InvalidOperationException("Cassie pool wait did not honor cancellation.");
    }

    CassieConnection.ClearPool(holder);
    await holder.CloseAsync();
    await waiter.OpenAsync();
    await waiter.CloseAsync();
    CassieConnection.ClearPool(waiter);
}

static async Task VerifyPoolingStartupOptionsAsync(string baseConnectionString)
{
    CassieConnectionStringBuilder firstSettings = new(baseConnectionString)
    {
        Pooling = true,
        MaxPoolSize = 1,
        Options = "-c application_name=options_pool_first"
    };
    CassieConnectionStringBuilder secondSettings = new(baseConnectionString)
    {
        Pooling = true,
        MaxPoolSize = 1,
        Options = "-c application_name=options_pool_second"
    };
    await using CassieConnection first = new() { ConnectionString = firstSettings.ConnectionString };
    await using CassieConnection second = new() { ConnectionString = secondSettings.ConnectionString };

    try
    {
        await first.OpenAsync();
        using CassieCommand showFirst = (CassieCommand)first.CreateCommand();
        showFirst.CommandText = "SELECT current_setting('application_name')";
        if (!Equals(await showFirst.ExecuteScalarAsync(), "options_pool_first"))
        {
            throw new InvalidOperationException("Cassie did not apply startup Options to the initial pooled session.");
        }

        await first.CloseAsync();
        await second.OpenAsync();
        using CassieCommand showSecond = (CassieCommand)second.CreateCommand();
        showSecond.CommandText = "SELECT current_setting('application_name')";
        if (!Equals(await showSecond.ExecuteScalarAsync(), "options_pool_second"))
        {
            throw new InvalidOperationException("Cassie pooled a physical session with different startup Options.");
        }

        await second.CloseAsync();
    }
    finally
    {
        if (first.State == ConnectionState.Open)
        {
            await first.CloseAsync();
        }

        if (second.State == ConnectionState.Open)
        {
            await second.CloseAsync();
        }

        CassieConnection.ClearPool(first);
        CassieConnection.ClearPool(second);
    }
}

static async Task VerifyExperimentalResultTypeMetadataAsync(CassieConnection connection)
{
    using (CassieCommand sleep = (CassieCommand)connection.CreateCommand())
    {
        sleep.CommandText = "SELECT pg_sleep(0.0) AS waited";
        await using DbDataReader reader = await sleep.ExecuteReaderAsync();
        if (reader.GetDataTypeName(0) != "void" || reader.GetFieldType(0) != typeof(string)
            || !await reader.ReadAsync() || !Equals(reader.GetValue(0), string.Empty))
        {
            throw new InvalidOperationException("Cassie pg_sleep did not expose its void result with the documented text carrier.");
        }
    }

    using (CassieCommand binarySleep = (CassieCommand)connection.CreateCommand())
    {
        binarySleep.CommandText = "SELECT pg_sleep(@seconds) AS waited";
        binarySleep.BinaryResults = true;
        _ = binarySleep.Parameters.Add(new CassieParameter { ParameterName = "seconds", Value = 0d, PostgresTypeOid = 701 });
        await binarySleep.PrepareAsync();
        await using DbDataReader reader = await binarySleep.ExecuteReaderAsync();
        if (reader.GetDataTypeName(0) != "void" || reader.GetFieldType(0) != typeof(string)
            || !await reader.ReadAsync() || !Equals(reader.GetValue(0), string.Empty))
        {
            throw new InvalidOperationException("Cassie prepared binary pg_sleep did not decode the zero-length void result.");
        }
    }

    using (CassieCommand search = (CassieCommand)connection.CreateCommand())
    {
        search.CommandText = "WITH RECURSIVE walk(node) AS (SELECT 1::BIGINT UNION ALL SELECT node + 1 FROM walk WHERE node < 3) SEARCH BREADTH FIRST BY node SET sequence SELECT sequence FROM walk ORDER BY sequence";
        await using DbDataReader reader = await search.ExecuteReaderAsync();
        if (reader.GetDataTypeName(0) != "record" || reader.GetFieldType(0) != typeof(string)
            || !await reader.ReadAsync() || reader.GetValue(0) is not string)
        {
            throw new InvalidOperationException("Cassie SEARCH output did not expose its record result with the documented text carrier.");
        }
    }

    using (CassieCommand setup = (CassieCommand)connection.CreateCommand())
    {
        setup.CommandText = "CREATE TABLE cassie_dotnet_wire_cycle_edges (source BIGINT, target BIGINT)";
        _ = await setup.ExecuteNonQueryAsync();
        setup.CommandText = "INSERT INTO cassie_dotnet_wire_cycle_edges VALUES (1, 2), (2, 1)";
        _ = await setup.ExecuteNonQueryAsync();
    }

    using (CassieCommand cycle = (CassieCommand)connection.CreateCommand())
    {
        cycle.CommandText = "WITH RECURSIVE walk(node) AS (SELECT 1::BIGINT UNION ALL SELECT edge.target FROM cassie_dotnet_wire_cycle_edges AS edge JOIN walk AS parent ON edge.source = parent.node) CYCLE node SET is_cycle USING path SELECT path FROM walk ORDER BY node, is_cycle";
        await using DbDataReader reader = await cycle.ExecuteReaderAsync();
        if (reader.GetDataTypeName(0) != "record[]" || reader.GetFieldType(0) != typeof(string)
            || !await reader.ReadAsync() || reader.GetValue(0) is not string)
        {
            throw new InvalidOperationException("Cassie CYCLE output did not expose its record[] result with the documented text carrier.");
        }
    }
}
