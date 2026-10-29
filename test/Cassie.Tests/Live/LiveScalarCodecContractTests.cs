using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Types;
using System.Data;
using System.Net;
using System.Net.NetworkInformation;
using Xunit;

namespace Cntryl.Cassie.Tests.Live
{
    [Collection(LiveEndpoint.CollectionName)]
    public sealed class LiveScalarCodecContractTests : IAsyncLifetime
    {
        private readonly LiveSession _live = new();

        public ValueTask InitializeAsync()
        {
            return _live.InitializeAsync();
        }

        public ValueTask DisposeAsync()
        {
            return _live.DisposeAsync();
        }

        private static readonly CassieTimeWithTimeZone TimeWithTimeZone = new(new TimeOnly(3, 4, 5).Add(TimeSpan.FromTicks(60_070)), 7200);

        public static TheoryData<string> TextParameterCases =>
        [
            "inet", "cidr", "mac", "mac8", "varbit-fixed", "varbit-varying", "lsn", "interval", "timetz", "vector"
        ];

        private static (object Value, object Expected) TextParameterCase(string name)
        {
            return name switch
            {
                "inet" => (new CassieNetworkAddress(IPAddress.Parse("192.0.2.9"), 24, false), "192.0.2.9/24"),
                "cidr" => (new CassieNetworkAddress(IPAddress.Parse("192.0.2.0"), 24, true), "192.0.2.0/24"),
                "mac" => (PhysicalAddress.Parse("08002b010203"), "08:00:2b:01:02:03"),
                "mac8" => (PhysicalAddress.Parse("08002b0102030405"), "08:00:2b:01:02:03:04:05"),
                "varbit-fixed" => (new CassieBitString("10101", false), "10101"),
                "varbit-varying" => (new CassieBitString("10101", true), "10101"),
                "lsn" => (new CassieLsn(0x000000010000000aUL), "1/A"),
                "interval" => (new CassieInterval(1, 2, 11_045_006_007), "1 mon 2 days 03:04:05.006007"),
                "timetz" => (TimeWithTimeZone, TimeWithTimeZone),
                "vector" => (new CassieVector(1, 2, 3), "[1,2,3]"),
                _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown text parameter case.")
            };
        }

        [Theory]
        [MemberData(nameof(TextParameterCases))]
        public async Task ShouldEchoValueGivenTextFormatParameterWhenExtendedTypeIsBound(string caseName)
        {
            // Arrange
            (object value, object expected) = TextParameterCase(caseName);
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = "SELECT @value";
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", Value = value, ParameterFormat = CassieParameterFormat.Text });

            // Act
            object? actual = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(expected, actual);
        }

        [Fact]
        public async Task ShouldPreserveWallClockGivenLocalDateTimeWhenTimestampBindsAsBinary()
        {
            // Arrange
            DateTime expected = new DateTime(2000, 1, 2, 1, 2, 3, 456, DateTimeKind.Unspecified).AddTicks(7_890);
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = "SELECT @value::timestamp AS value";
            command.BinaryResults = true;
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", Value = DateTime.SpecifyKind(expected, DateTimeKind.Local), ParameterFormat = CassieParameterFormat.Binary });

            // Act
            await using System.Data.Common.DbDataReader reader = await LiveSession.ReadFirstRowAsync(command);

            // Assert
            Assert.Equal(expected, reader.GetValue(0));
        }

        [Fact]
        public async Task ShouldPreserveWallClockGivenUtcDateTimeWhenTimestampBindsAsText()
        {
            // Arrange
            DateTime expected = new DateTime(2000, 1, 2, 1, 2, 3, 456, DateTimeKind.Unspecified).AddTicks(7_890);
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = "SELECT @value::timestamp AS value";
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", Value = DateTime.SpecifyKind(expected, DateTimeKind.Utc), ParameterFormat = CassieParameterFormat.Text });

            // Act
            await using System.Data.Common.DbDataReader reader = await LiveSession.ReadFirstRowAsync(command);

            // Assert
            Assert.Equal(expected, reader.GetValue(0));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ShouldReturnDateTimeKindsGivenTemporalColumnsWhenResultFormatVaries(bool binaryResults)
        {
            // Arrange
            DateTime expectedDate = new(2000, 1, 2, 0, 0, 0, DateTimeKind.Unspecified);
            DateTime expectedTimestamp = new(2000, 1, 2, 1, 2, 3, 4, DateTimeKind.Unspecified);
            DateTime expectedTimestampTz = new(2000, 1, 2, 1, 2, 3, 4, DateTimeKind.Utc);
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = "SELECT DATE '2000-01-02' AS date_value, TIMESTAMP '2000-01-02 01:02:03.004' AS timestamp_value, TIMESTAMPTZ '2000-01-02 01:02:03.004+00:00' AS timestamptz_value";
            command.BinaryResults = binaryResults;

            // Act
            await using System.Data.Common.DbDataReader reader = await LiveSession.ReadFirstRowAsync(command);

            // Assert
            Assert.Equal(expectedDate, reader.GetDateTime(0));
            Assert.Equal(expectedDate, await reader.GetFieldValueAsync<DateTime>(0, TestContext.Current.CancellationToken));
            Assert.Equal(expectedTimestamp, reader.GetDateTime(1));
            Assert.Equal(expectedTimestamp, await reader.GetFieldValueAsync<DateTime>(1, TestContext.Current.CancellationToken));
            Assert.Equal(expectedTimestampTz, reader.GetDateTime(2));
            Assert.Equal(expectedTimestampTz, await reader.GetFieldValueAsync<DateTime>(2, TestContext.Current.CancellationToken));
            Assert.Equal(DateTimeKind.Unspecified, reader.GetDateTime(0).Kind);
            Assert.Equal(DateTimeKind.Unspecified, reader.GetDateTime(1).Kind);
            Assert.Equal(DateTimeKind.Utc, reader.GetDateTime(2).Kind);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ShouldTagUnrepresentableValuesGivenTemporalSpecialsWhenResultFormatVaries(bool binaryResults)
        {
            // Arrange
            CassieTemporalValue[] expectedValues =
            [
                CassieTemporalValue.FromDateDays(1_000_000_000),
                CassieTemporalValue.FromTimeMicroseconds(86_400_000_000),
                CassieTemporalValue.FromTimeWithTimeZoneMicroseconds(86_400_000_000, 3600),
                CassieTemporalValue.FromTimestampMicroseconds(300_000_000_000_000_000),
                CassieTemporalValue.FromTimestampWithTimeZoneMicroseconds(long.MaxValue)
            ];
            int[] typeOids = [1082, 1083, 1266, 1114, 1184];
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = "SELECT @value0::date AS value0, @value1::time AS value1, @value2::timetz AS value2, @value3::timestamp AS value3, @value4::timestamptz AS value4";
            command.BinaryResults = binaryResults;
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

            // Act
            await using System.Data.Common.DbDataReader reader = await LiveSession.ReadFirstRowAsync(command);

            // Assert
            for (int index = 0; index < expectedValues.Length; index++)
            {
                CassieTemporalValue actual = await reader.GetFieldValueAsync<CassieTemporalValue>(index, TestContext.Current.CancellationToken);
                CassieTemporalValue expected = expectedValues[index];
                if (binaryResults)
                {
                    Assert.Equal(expected, actual);
                    continue;
                }

                Assert.Equal(expected.Kind, actual.Kind);
                Assert.Equal(expected.OffsetSeconds, actual.OffsetSeconds);
                if (expected.IsInfinity)
                {
                    Assert.True(actual.IsInfinity, $"OID {typeOids[index]} lost its infinity marker.");
                    Assert.Equal(expected.Value, actual.Value);
                }
                else if (expected.Kind is CassieTemporalKind.Time or CassieTemporalKind.TimeWithTimeZone)
                {
                    Assert.True(actual.HasRawValue, $"OID {typeOids[index]} lost its raw value.");
                    Assert.Equal(expected.Value, actual.Value);
                }
                else
                {
                    Assert.False(actual.HasRawValue);
                    Assert.NotNull(actual.TextValue);
                }
            }
        }

        [Fact]
        public async Task ShouldDecodeTextTemporalsGivenSqlDmyDateStyleWhenSessionSettingChanges()
        {
            // Arrange
            using CassieCommand setDateStyle = _live.CreateCommand();
            setDateStyle.CommandText = "SET DateStyle = 'SQL, DMY'";
            _ = await setDateStyle.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            using CassieCommand query = _live.CreateCommand();
            query.CommandText = "SELECT DATE '2000-01-02' AS date_value, TIMESTAMP '2000-01-02 03:04:05.000006' AS timestamp_value";

            // Act
            await using System.Data.Common.DbDataReader reader = await LiveSession.ReadFirstRowAsync(query);

            // Assert
            Assert.Equal(new DateOnly(2000, 1, 2), reader.GetValue(0));
            Assert.Equal(new DateTime(2000, 1, 2, 3, 4, 5, DateTimeKind.Unspecified).AddTicks(60), reader.GetValue(1));
        }

        [Fact]
        public async Task ShouldRefreshDateStyleGivenSettingChangedInsideViewWhenNextQueryRuns()
        {
            // Arrange
            LiveFeature.Require("PARAMETER_STATUS_UPDATES");
            await _live.CreateDateStyleViewAsync();
            using CassieCommand queryView = _live.CreateCommand();
            queryView.CommandText = $"SELECT changed, DATE '2000-01-02' AS value FROM {LiveSession.DateStyleView}";

            // Act
            object? duringView;
            await using (System.Data.Common.DbDataReader reader = await LiveSession.ReadFirstRowAsync(queryView))
            {
                duringView = reader.GetValue(1);
                Assert.Equal("SQL, DMY", reader.GetValue(0));
            }

            using CassieCommand queryAfterView = _live.CreateCommand();
            queryAfterView.CommandText = "SELECT DATE '2000-01-02' AS value";
            object? afterView = await queryAfterView.ExecuteScalarAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(new DateOnly(2000, 1, 2), duringView);
            Assert.Equal(new DateOnly(2000, 1, 2), afterView);
        }

        [Fact]
        public async Task ShouldExposeUnknownMetadataGivenUntypedNullWhenQueryReturnsNull()
        {
            // Arrange
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = "SELECT NULL AS value";

            // Act
            await using System.Data.Common.DbDataReader reader = await LiveSession.ReadFirstRowAsync(command);

            // Assert
            Assert.Equal("unknown", reader.GetDataTypeName(0));
            Assert.Equal(typeof(string), reader.GetFieldType(0));
            Assert.Equal(DBNull.Value, reader.GetValue(0));
        }

        [Fact]
        public async Task ShouldReadTypedValuesGivenBinaryResultsWhenScalarsAreSelected()
        {
            // Arrange
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = "SELECT 42::int4 AS i, true AS b, '550e8400-e29b-41d4-a716-446655440001'::uuid AS u, DATE '2000-01-02' AS d";
            command.BinaryResults = true;

            // Act
            await using System.Data.Common.DbDataReader reader = await LiveSession.ReadFirstRowAsync(command);

            // Assert
            Assert.Equal(42, reader.GetInt32(0));
            Assert.True(reader.GetBoolean(1));
            Assert.Equal(Guid.Parse("550e8400-e29b-41d4-a716-446655440001"), reader.GetGuid(2));
            Assert.Equal(new DateOnly(2000, 1, 2), reader.GetValue(3));
        }

        [Fact]
        public async Task ShouldRoundTripRegclassGivenBinaryOidParameterWhenResultIsBinary()
        {
            // Arrange
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = "SELECT @relation::regclass AS relation";
            command.BinaryResults = true;
            _ = command.Parameters.Add(new CassieParameter
            {
                ParameterName = "relation",
                Value = 1259u,
                PostgresTypeOid = 2205,
                ParameterFormat = CassieParameterFormat.Binary
            });

            // Act
            await using System.Data.Common.DbDataReader reader = await LiveSession.ReadFirstRowAsync(command);

            // Assert
            Assert.Equal("regclass", reader.GetDataTypeName(0));
            Assert.Equal(typeof(uint), reader.GetFieldType(0));
            Assert.Equal(1259u, reader.GetValue(0));
        }

        [Fact]
        public async Task ShouldRoundTripVectorGivenBinaryParameterWhenResultIsBinary()
        {
            // Arrange
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = "SELECT @vector AS vector";
            command.BinaryResults = true;
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "vector", Value = new CassieVector(1, 2, 3) });

            // Act
            await using System.Data.Common.DbDataReader reader = await LiveSession.ReadFirstRowAsync(command);

            // Assert
            CassieVector vector = Assert.IsType<CassieVector>(reader.GetValue(0));
            Assert.Equal(3, vector.Dimension);
            Assert.Equal(1f, vector[0]);
            Assert.Equal(2f, vector[1]);
            Assert.Equal(3f, vector[2]);
        }

        [Fact]
        public async Task ShouldDecodeNetworkTypesGivenBinaryResultsWhenLiteralsAreSelected()
        {
            // Arrange
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = "SELECT '192.0.2.9/24'::inet AS inet, '192.0.2.0/24'::cidr AS cidr, '01:23:45:67:89:ab'::macaddr AS mac, B'10101'::varbit AS bits, '1/A'::pg_lsn AS lsn";
            command.BinaryResults = true;

            // Act
            await using System.Data.Common.DbDataReader reader = await LiveSession.ReadFirstRowAsync(command);

            // Assert
            CassieNetworkAddress inet = Assert.IsType<CassieNetworkAddress>(reader.GetValue(0));
            Assert.Equal("192.0.2.9/24", inet.ToString());
            CassieNetworkAddress cidr = Assert.IsType<CassieNetworkAddress>(reader.GetValue(1));
            Assert.Equal("192.0.2.0/24", cidr.ToString());
            Assert.True(cidr.IsCidr);
            PhysicalAddress mac = Assert.IsType<PhysicalAddress>(reader.GetValue(2));
            Assert.Equal(new byte[] { 1, 0x23, 0x45, 0x67, 0x89, 0xab }, mac.GetAddressBytes());
            CassieBitString bits = Assert.IsType<CassieBitString>(reader.GetValue(3));
            Assert.Equal("10101", bits.Value);
            Assert.True(bits.IsVarying);
            CassieLsn lsn = Assert.IsType<CassieLsn>(reader.GetValue(4));
            Assert.Equal(0x000000010000000aUL, lsn.Value);
        }

        [Fact]
        public async Task ShouldRoundTripExtendedTypesGivenBinaryParametersWhenResultsAreBinary()
        {
            // Arrange
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = "SELECT @inet::inet AS inet, @cidr::cidr AS cidr, @mac::macaddr AS mac, @bits::varbit AS bits, @lsn::pg_lsn AS lsn, @timetz::timetz AS timetz, @interval::interval AS interval";
            command.BinaryResults = true;
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "inet", Value = new CassieNetworkAddress(IPAddress.Parse("192.0.2.9"), 24, false) });
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "cidr", Value = new CassieNetworkAddress(IPAddress.Parse("192.0.2.0"), 24, true) });
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "mac", Value = PhysicalAddress.Parse("0123456789AB") });
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "bits", Value = new CassieBitString("10101", true) });
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "lsn", Value = new CassieLsn(0x000000010000000aUL) });
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "timetz", Value = new CassieTimeWithTimeZone(new TimeOnly(1, 2, 3), 3600) });
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "interval", Value = new CassieInterval(14, -2, 1_500_000) });

            // Act
            await using System.Data.Common.DbDataReader reader = await LiveSession.ReadFirstRowAsync(command);

            // Assert
            Assert.Equal(new CassieNetworkAddress(IPAddress.Parse("192.0.2.9"), 24, false), reader.GetValue(0));
            Assert.Equal(new CassieNetworkAddress(IPAddress.Parse("192.0.2.0"), 24, true), reader.GetValue(1));
            Assert.Equal(6, Assert.IsType<PhysicalAddress>(reader.GetValue(2)).GetAddressBytes().Length);
            Assert.Equal(new CassieBitString("10101", true), reader.GetValue(3));
            Assert.Equal(new CassieLsn(0x000000010000000aUL), reader.GetValue(4));
            Assert.Equal(new CassieTimeWithTimeZone(new TimeOnly(1, 2, 3), 3600), reader.GetValue(5));
            Assert.Equal(new CassieInterval(14, -2, 1_500_000), reader.GetValue(6));
        }

        [Fact]
        public async Task ShouldPreserveExactValueGivenNumericParameterWhenPrecisionExceedsDouble()
        {
            // Arrange
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = "SELECT @value::numeric AS value";
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", Value = new CassieNumeric("9007199254740993.1255") });

            // Act
            await using System.Data.Common.DbDataReader reader = await LiveSession.ReadFirstRowAsync(command);

            // Assert
            Assert.Equal("9007199254740993.1255", Assert.IsType<CassieNumeric>(reader.GetValue(0)).Value);
        }

        [Fact]
        public async Task ShouldPreserveExactValueGivenBinaryNumericResultWhenReadAsDecimal()
        {
            // Arrange
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = "SELECT 4503599627370496.5628::numeric AS value";
            command.BinaryResults = true;

            // Act
            await using System.Data.Common.DbDataReader reader = await LiveSession.ReadFirstRowAsync(command);

            // Assert
            Assert.Equal("4503599627370496.5628", Assert.IsType<CassieNumeric>(reader.GetValue(0)).Value);
            Assert.Equal(4503599627370496.5628m, await reader.GetFieldValueAsync<decimal>(0, TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task ShouldRoundTripMaximumValueGivenUInt64ParameterWhenBoundAsNumeric()
        {
            // Arrange
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = "SELECT @inferred::numeric AS inferred, @typed::numeric AS typed";
            command.BinaryResults = true;
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "inferred", Value = ulong.MaxValue });
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "typed", DbType = DbType.UInt64, Value = ulong.MaxValue });

            // Act
            await using System.Data.Common.DbDataReader reader = await LiveSession.ReadFirstRowAsync(command);

            // Assert
            Assert.Equal("18446744073709551615", Assert.IsType<CassieNumeric>(reader.GetValue(0)).Value);
            Assert.Equal("18446744073709551615", Assert.IsType<CassieNumeric>(reader.GetValue(1)).Value);
            Assert.Equal(ulong.MaxValue, await reader.GetFieldValueAsync<ulong>(0, TestContext.Current.CancellationToken));
            Assert.Equal(ulong.MaxValue, await reader.GetFieldValueAsync<ulong>(1, TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task ShouldRoundTripTimeOfDayGivenTimeSpanParameterWhenBoundAsTime()
        {
            // Arrange
            TimeSpan expected = new TimeSpan(1, 2, 3).Add(TimeSpan.FromTicks(4_567_890));
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = "SELECT @inferred::time AS inferred, @typed::time AS typed";
            command.BinaryResults = true;
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "inferred", Value = expected });
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "typed", DbType = DbType.Time, Value = expected });

            // Act
            await using System.Data.Common.DbDataReader reader = await LiveSession.ReadFirstRowAsync(command);

            // Assert
            Assert.Equal(expected, Assert.IsType<TimeOnly>(reader.GetValue(0)).ToTimeSpan());
            Assert.Equal(expected, await reader.GetFieldValueAsync<TimeSpan>(0, TestContext.Current.CancellationToken));
            Assert.Equal(expected, Assert.IsType<TimeOnly>(reader.GetValue(1)).ToTimeSpan());
            Assert.Equal(expected, await reader.GetFieldValueAsync<TimeSpan>(1, TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task ShouldSendCalendarDateGivenDateTimeParameterWhenDbTypeIsDate()
        {
            // Arrange
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = "SELECT @value::date AS value";
            command.BinaryResults = true;
            _ = command.Parameters.Add(new CassieParameter
            {
                ParameterName = "value",
                DbType = DbType.Date,
                Value = new DateTime(2000, 1, 2, 23, 59, 59, DateTimeKind.Utc)
            });

            // Act
            await using System.Data.Common.DbDataReader reader = await LiveSession.ReadFirstRowAsync(command);

            // Assert
            Assert.Equal(new DateOnly(2000, 1, 2), reader.GetValue(0));
        }

        [Fact]
        public async Task ShouldReadInt32GivenBinaryParameterWhenTextResultsAreRequested()
        {
            // Arrange
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = "SELECT @value::int4";
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", Value = 123 });

            // Act
            await using System.Data.Common.DbDataReader reader = await LiveSession.ReadFirstRowAsync(command);

            // Assert
            Assert.Equal(123, reader.GetInt32(0));
        }

        [Fact]
        public async Task ShouldRoundTripDocumentGivenJsonOidParameterWhenResultIsBinary()
        {
            // Arrange
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = "SELECT @document AS document";
            command.BinaryResults = true;
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "document", Value = /*lang=json,strict*/ "{\"answer\":42}", PostgresTypeOid = 114 });

            // Act
            await using System.Data.Common.DbDataReader reader = await LiveSession.ReadFirstRowAsync(command);

            // Assert
            Assert.Equal(/*lang=json,strict*/ "{\"answer\":42}", reader.GetString(0));
        }

        [Fact]
        public async Task ShouldRoundTripDocumentGivenJsonbOidParameterWhenServerRegistersDistinctJsonb()
        {
            // Arrange
            LiveFeature.Require("JSONB_TYPE");
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = "SELECT @document::jsonb AS document";
            command.BinaryResults = true;
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "document", Value = /*lang=json,strict*/ "{\"answer\":42}", PostgresTypeOid = 3802 });

            // Act
            await using System.Data.Common.DbDataReader reader = await LiveSession.ReadFirstRowAsync(command);

            // Assert
            Assert.Equal(/*lang=json,strict*/ "{\"answer\": 42}", reader.GetString(0));
        }

        [Fact]
        public async Task ShouldNormalizePathGivenJsonPathParameterWhenServerRegistersJsonPath()
        {
            // Arrange
            LiveFeature.Require("JSONPATH_TYPE");
            using CassieCommand command = _live.CreateCommand();
            command.CommandText = "SELECT @path::jsonpath AS path";
            command.BinaryResults = true;
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "path", Value = "$.foo", PostgresTypeOid = 4072 });

            // Act
            await using System.Data.Common.DbDataReader reader = await LiveSession.ReadFirstRowAsync(command);

            // Assert
            Assert.Equal("$.\"foo\"", reader.GetString(0));
        }
    }
}
