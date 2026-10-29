using System.Data;
using System.Data.Common;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Cntryl.Cassie.Tests
{
    public sealed class ExtendedQueryContractTests
    {
        private static readonly byte[] BinaryInt42 = [0, 0, 0, 42];
        private static readonly CassieVector Vector123 = new(1, 2, 3);
        private static readonly byte[] BinaryVector123 = [0, 3, 0, 0, 0x3f, 0x80, 0, 0, 0x40, 0, 0, 0, 0x40, 0x40, 0, 0];

        [Fact]
        public Task ShouldRejectInvalidNegativeDataRowLengthGivenServerFrameWhenReaderReads()
        {
            // Arrange
            byte[] malformedRow = [0, 1, 0xff, 0xff, 0xff, 0xfe];

            // Act
            Task read = AssertMalformedDataRowAsync(malformedRow);

            // Assert
            return read;
        }

        [Fact]
        public Task ShouldRejectTrailingDataRowBytesGivenServerFrameWhenReaderReads()
        {
            // Arrange
            byte[] malformedRow = [0, 1, 0, 0, 0, 1, (byte)'x', 0xff];

            // Act
            Task read = AssertMalformedDataRowAsync(malformedRow);

            // Assert
            return read;
        }

        [Fact]
        public Task ShouldRejectNegativeRowDescriptionColumnCountGivenServerFrameWhenReaderExecutes()
        {
            // Arrange
            byte[] malformedDescription = [0xff, 0xff];

            // Act
            Task execute = AssertMalformedRowDescriptionAsync(malformedDescription);

            // Assert
            return execute;
        }

        [Fact]
        public Task ShouldRejectTrailingRowDescriptionBytesGivenServerFrameWhenReaderExecutes()
        {
            // Arrange
            byte[] malformedDescription = [.. CreateRowDescriptionPayload(), 0xff];

            // Act
            Task execute = AssertMalformedRowDescriptionAsync(malformedDescription);

            // Assert
            return execute;
        }

        [Fact]
        public Task ShouldRejectUnsupportedRowDescriptionFormatGivenServerFrameWhenReaderExecutes()
        {
            // Arrange
            byte[] malformedDescription = CreateRowDescriptionPayload(formatCode: 2);

            // Act
            Task execute = AssertMalformedRowDescriptionAsync(malformedDescription);

            // Assert
            return execute;
        }

        [Fact]
        public async Task ShouldEncodeUuidArrayParameterGivenClrArrayWhenReaderExecutes()
        {
            // Arrange
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            byte[] expectedValue =
            [
                0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 11, 134, 0, 0, 0, 2, 0, 0, 0, 1,
                0, 0, 0, 16, 0x55, 0x0e, 0x84, 0x00, 0xe2, 0x9b, 0x41, 0xd4, 0xa7, 0x16, 0x44, 0x66, 0x55, 0x44, 0x00, 0x01,
                0xff, 0xff, 0xff, 0xff
            ];
            Task peer = Task.Run(async () => await ServeOneBoundQueryAsync(listener, 2951, expectedValue, 1), TestContext.Current.CancellationToken);
            using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;Password=secret;SSL Mode=Disable;Timeout=3;Pooling=false"
            };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using DbCommand command = connection.CreateCommand();
            command.CommandText = "SELECT @ids";
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "ids", Value = new Guid?[] { Guid.Parse("550e8400-e29b-41d4-a716-446655440001"), null } });

            // Act
            DbDataReader reader;
            try
            {
                reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            }
            catch
            {
                await peer;
                throw;
            }

            await using (reader)
            {
                bool hasRow = await reader.ReadAsync(TestContext.Current.CancellationToken);

                // Assert
                Assert.True(hasRow);
                await reader.DisposeAsync();
            }
            await connection.CloseAsync();
            await peer;
        }

        [Fact]
        public Task ShouldEncodeUuidParameterGivenGuidWhenReaderExecutes()
        {
            // Arrange
            Guid value = Guid.Parse("550e8400-e29b-41d4-a716-446655440001");
            byte[] encoded = [0x55, 0x0e, 0x84, 0x00, 0xe2, 0x9b, 0x41, 0xd4, 0xa7, 0x16, 0x44, 0x66, 0x55, 0x44, 0x00, 0x01];

            // Act
            Task binding = AssertBoundParameterAsync(value, 2950, encoded);

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldEncodeBinaryInt2ParameterGivenByteValueWhenReaderExecutes()
        {
            // Arrange
            byte value = byte.MaxValue;
            byte[] encoded = [0x00, 0xff];

            // Act
            Task binding = AssertBoundParameterAsync(value, 21, encoded);

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldEncodeBinaryInt2ParameterGivenSByteMinimumWhenReaderExecutes()
        {
            // Arrange
            sbyte value = sbyte.MinValue;
            byte[] encoded = [0xff, 0x80];

            // Act
            Task binding = AssertBoundParameterAsync(value, 21, encoded);

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldEncodeBinaryInt4ParameterGivenUInt16MaximumWhenReaderExecutes()
        {
            // Arrange
            ushort value = ushort.MaxValue;
            byte[] encoded = [0x00, 0x00, 0xff, 0xff];

            // Act
            Task binding = AssertBoundParameterAsync(value, 23, encoded);

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldEncodeByteaParameterGivenByteArrayWhenReaderExecutes()
        {
            // Arrange
            byte[] value = [0x00, 0x7f, 0xff];

            // Act
            Task binding = AssertBoundParameterAsync(value, 17, value);

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldEncodeDateParameterGivenDateOnlyWhenReaderExecutes()
        {
            // Arrange
            byte[] encoded = [0, 0, 0, 1];

            // Act
            Task binding = AssertBoundParameterAsync(new DateOnly(2000, 1, 2), 1082, encoded);

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldEncodeBinaryDateParameterGivenDbTypeDateAndDateTimeWhenReaderExecutes()
        {
            // Arrange
            DateTime value = new(2000, 1, 2, 23, 59, 59, DateTimeKind.Utc);
            byte[] encoded = [0, 0, 0, 1];

            // Act
            Task binding = AssertBoundParameterAsync(value, 1082, encoded, parameterDbType: DbType.Date);

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldEncodeTextDateParameterGivenDbTypeDateAndDateTimeWhenReaderExecutes()
        {
            // Arrange
            DateTime value = new(2000, 1, 2, 23, 59, 59, DateTimeKind.Utc);

            // Act
            Task binding = AssertBoundParameterAsync(value, 1082, "2000-01-02"u8.ToArray(),
                parameterFormat: 0,
                parameterEncoding: CassieParameterFormat.Text,
                parameterDbType: DbType.Date);

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldEncodeBinaryTimestampParameterGivenLocalDateTimeAsWallClockWhenReaderExecutes()
        {
            // Arrange
            DateTime value = new DateTime(2000, 1, 2, 1, 2, 3, 456, DateTimeKind.Local).AddTicks(7_899);
            byte[] encoded = new byte[sizeof(long)];
            BinaryPrimitives.WriteInt64BigEndian(encoded, 90_123_456_789L);

            // Act
            Task binding = AssertBoundParameterAsync(value, 1114, encoded,
                parameterEncoding: CassieParameterFormat.Binary);

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldEncodeTextTimestampParameterGivenUtcDateTimeAsWallClockWhenReaderExecutes()
        {
            // Arrange
            DateTime value = new DateTime(2000, 1, 2, 1, 2, 3, 456, DateTimeKind.Utc).AddTicks(7_899);

            // Act
            Task binding = AssertBoundParameterAsync(value, 1114,
                "2000-01-02 01:02:03.456789"u8.ToArray(),
                parameterFormat: 0,
                parameterEncoding: CassieParameterFormat.Text);

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldEncodeBinaryTimestampArrayGivenLocalDateTimeAsWallClockWhenReaderExecutes()
        {
            // Arrange
            DateTime[] values = [new DateTime(2000, 1, 2, 1, 2, 3, 456, DateTimeKind.Local).AddTicks(7_899)];
            using MemoryStream expected = new();
            WriteInt32(expected, 1);
            WriteInt32(expected, 0);
            WriteInt32(expected, 1114);
            WriteInt32(expected, 1);
            WriteInt32(expected, 1);
            WriteInt32(expected, sizeof(long));
            WriteInt64(expected, 90_123_456_789L);

            // Act
            Task binding = AssertBoundParameterAsync(values, 1115, expected.ToArray(),
                parameterEncoding: CassieParameterFormat.Binary);

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldEncodeTextTimestampArrayGivenUtcDateTimeAsWallClockWhenReaderExecutes()
        {
            // Arrange
            DateTime[] values = [new DateTime(2000, 1, 2, 1, 2, 3, 456, DateTimeKind.Utc).AddTicks(7_899)];

            // Act
            Task binding = AssertBoundParameterAsync(values, 1115,
                "[\"2000-01-02 01:02:03.456789\"]"u8.ToArray(),
                parameterFormat: 0,
                parameterEncoding: CassieParameterFormat.Text);

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldEncodeBinaryTimeParameterGivenTimeSpanWhenReaderExecutes()
        {
            // Arrange
            TimeSpan value = new TimeSpan(1, 2, 3).Add(TimeSpan.FromTicks(4_567_890));
            byte[] encoded = [0x00, 0x00, 0x00, 0x00, 0xdd, 0xef, 0x71, 0x15];

            // Act
            Task binding = AssertBoundParameterAsync(value, 1083, encoded);

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldEncodeBinaryTimeParameterGivenDbTypeTimeAndTimeSpanWhenReaderExecutes()
        {
            // Arrange
            TimeSpan value = new TimeSpan(1, 2, 3).Add(TimeSpan.FromTicks(4_567_890));
            byte[] encoded = [0x00, 0x00, 0x00, 0x00, 0xdd, 0xef, 0x71, 0x15];

            // Act
            Task binding = AssertBoundParameterAsync(value, 1083, encoded, parameterDbType: DbType.Time);

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldEncodeJsonbParameterGivenExplicitOidWhenReaderExecutes()
        {
            // Arrange
            const string json = /*lang=json,strict*/ "{\"answer\":42}";
            byte[] encoded = [1, .. Encoding.UTF8.GetBytes(json)];

            // Act
            Task binding = AssertBoundParameterAsync(json, 3802, encoded, parameterOid: 3802);

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldEncodeBinaryJsonPathParameterGivenExplicitOidWhenReaderExecutes()
        {
            // Arrange
            const string value = "$.foo";
            byte[] encoded = [1, .. Encoding.UTF8.GetBytes(value)];

            // Act
            Task binding = AssertBoundParameterAsync(value, 25, encoded, parameterOid: 4072);

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldEncodeBinaryExactNumericParameterGivenCassieNumericWhenReaderExecutes()
        {
            // Arrange
            CassieNumeric value = new("9007199254740993.1255");
            byte[] encoded = [0x00, 0x05, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x23, 0x2f, 0x07, 0xc8, 0x15, 0x62, 0x03, 0xe1, 0x04, 0xe7];

            // Act
            Task binding = AssertBoundParameterAsync(value, 1700, encoded);

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldEncodeBinaryNumericParameterGivenExponentWhenReaderExecutes()
        {
            // Arrange
            CassieNumeric value = new("1e-2");
            byte[] encoded = [0x00, 0x01, 0xff, 0xff, 0x00, 0x00, 0x00, 0x02, 0x00, 0x64];

            // Act
            Task binding = AssertBoundParameterAsync(value, 1700, encoded);

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldEncodeBinaryNumericParameterGivenUInt64MaximumWhenReaderExecutes()
        {
            // Arrange
            ulong value = ulong.MaxValue;
            byte[] encoded = [0x00, 0x05, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x07, 0x34, 0x1a, 0x58, 0x02, 0xe1, 0x03, 0xbb, 0x06, 0x4f];

            // Act
            Task binding = AssertBoundParameterAsync(value, 1700, encoded);

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldEncodeBinaryNumericArrayGivenUInt64ValuesWhenReaderExecutes()
        {
            // Arrange
            ulong?[] values = [ulong.MaxValue, null];
            using MemoryStream encoded = new();
            WriteInt32(encoded, 1);
            WriteInt32(encoded, 1);
            WriteInt32(encoded, 1700);
            WriteInt32(encoded, 2);
            WriteInt32(encoded, 1);
            WriteInt32(encoded, 18);
            encoded.Write([0x00, 0x05, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x07, 0x34, 0x1a, 0x58, 0x02, 0xe1, 0x03, 0xbb, 0x06, 0x4f]);
            WriteInt32(encoded, -1);

            // Act
            Task binding = AssertBoundParameterAsync(values, 1231, encoded.ToArray());

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldBindNumericArrayGivenDecimalValuesWhenReaderExecutes()
        {
            // Arrange
            decimal[] values = [1.25m, 2.5m];
            byte[] encoded = "[1.25,2.5]"u8.ToArray();

            // Act
            Task binding = AssertBoundParameterAsync(values, 1231, encoded, parameterFormat: 0, parameterEncoding: CassieParameterFormat.Text);

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldMapClrArrayToPostgresBoundsGivenZeroBasedDimensionWhenReaderExecutes()
        {
            // Arrange
            Array values = Array.CreateInstance(typeof(int?), [2, 2], [0, -1]);
            values.SetValue(1, 0, -1);
            values.SetValue(null, 0, 0);
            values.SetValue(3, 1, -1);
            values.SetValue(4, 1, 0);
            using MemoryStream encoded = new();
            WriteInt32(encoded, 2);
            WriteInt32(encoded, 1);
            WriteInt32(encoded, 23);
            WriteInt32(encoded, 2);
            WriteInt32(encoded, 1);
            WriteInt32(encoded, 2);
            WriteInt32(encoded, -1);
            WriteInt32(encoded, 4);
            WriteInt32(encoded, 1);
            WriteInt32(encoded, -1);
            WriteInt32(encoded, 4);
            WriteInt32(encoded, 3);
            WriteInt32(encoded, 4);
            WriteInt32(encoded, 4);

            // Act
            Task binding = AssertBoundParameterAsync(values, 1007, encoded.ToArray());

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldPreserveArrayShapeInTextParameterFormatGivenNonDefaultBoundsWhenReaderExecutes()
        {
            // Arrange
            CassieArray values = new(25, [2, 2], [0, 1], ["a", "NULL", null, "x,y"]);

            // Act
            Task binding = AssertBoundParameterAsync(values, 1009, "[0:1][1:2]={{a,\"NULL\"},{NULL,\"x,y\"}}"u8.ToArray(), parameterFormat: 0, parameterEncoding: CassieParameterFormat.Text);

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldEncodeMultidimensionalIntegerArrayTextGivenNonDefaultBoundsWhenReaderExecutes()
        {
            // Arrange
            Array values = Array.CreateInstance(typeof(int), [2, 2], [0, 4]);
            values.SetValue(1, 0, 4);
            values.SetValue(2, 0, 5);
            values.SetValue(3, 1, 4);
            values.SetValue(4, 1, 5);

            // Act
            Task binding = AssertBoundParameterAsync(values, 1007, "[1:2][4:5]={{1,2},{3,4}}"u8.ToArray(), parameterFormat: 0, parameterEncoding: CassieParameterFormat.Text);

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldPreserveArrayShapeGivenExplicitMultidimensionalCassieArrayWhenReaderExecutes()
        {
            // Arrange
            CassieArray values = new(23, [2, 2], [0, -1], [1, null, 3, 4]);
            using MemoryStream encoded = new();
            WriteInt32(encoded, 2);
            WriteInt32(encoded, 1);
            WriteInt32(encoded, 23);
            WriteInt32(encoded, 2);
            WriteInt32(encoded, 0);
            WriteInt32(encoded, 2);
            WriteInt32(encoded, -1);
            WriteInt32(encoded, 4);
            WriteInt32(encoded, 1);
            WriteInt32(encoded, -1);
            WriteInt32(encoded, 4);
            WriteInt32(encoded, 3);
            WriteInt32(encoded, 4);
            WriteInt32(encoded, 4);

            // Act
            Task binding = AssertBoundParameterAsync(values, 1007, encoded.ToArray());

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldEncodeBinaryInetParameterGivenNetworkAddressWhenReaderExecutes()
        {
            // Arrange
            CassieNetworkAddress address = new(IPAddress.Parse("192.0.2.9"), 24, false);
            byte[] encoded = [2, 24, 0, 4, 192, 0, 2, 9];

            // Act
            Task binding = AssertBoundParameterAsync(address, 869, encoded);

            // Assert
            return binding;
        }

        [Fact]
        public async Task ShouldEncodeSupportedExtendedTextParametersGivenTypedValuesWhenReaderExecutes()
        {
            // Arrange
            CassieTimeWithTimeZone timeWithTimeZone = new(new TimeOnly(3, 4, 5).Add(TimeSpan.FromTicks(60_070)), 7200);
            CassieInterval interval = new(1, 2, 11_045_006_007);
            TimeSpan time = new TimeSpan(1, 2, 3).Add(TimeSpan.FromTicks(4_567_890));
            (object Value, int Oid, string Text)[] cases =
            [
                (new CassieNetworkAddress(IPAddress.Parse("192.0.2.9"), 24, false), 869, "192.0.2.9/24"),
                (new CassieNetworkAddress(IPAddress.Parse("192.0.2.0"), 24, true), 650, "192.0.2.0/24"),
                (System.Net.NetworkInformation.PhysicalAddress.Parse("08002b010203"), 829, "08:00:2b:01:02:03"),
                (System.Net.NetworkInformation.PhysicalAddress.Parse("08002b0102030405"), 774, "08:00:2b:01:02:03:04:05"),
                (new CassieBitString("10101", false), 1560, "10101"),
                (new CassieBitString("10101", true), 1562, "10101"),
                (new CassieLsn(0x000000010000000aUL), 3220, "1/A"),
                (interval, 1186, "1 months 2 days 11045006007 microseconds"),
                (time, 1083, "01:02:03.456789"),
                (timeWithTimeZone, 1266, "03:04:05.006007+02:00"),
                (new CassieVector(1, 2, 3), 100003, "[1,2,3]")
            ];

            // Act
            foreach ((object value, int oid, string text) in cases)
            {
                await AssertBoundParameterAsync(value, oid, Encoding.UTF8.GetBytes(text),
                    parameterFormat: 0, parameterEncoding: CassieParameterFormat.Text);
            }

            // Assert
        }

        [Fact]
        public Task ShouldEncodeExtendedTextArrayGivenClrNetworkArrayWhenReaderExecutes()
        {
            // Arrange
            CassieNetworkAddress[] values = [new(IPAddress.Parse("192.0.2.9"), 24, false), new(IPAddress.Parse("192.0.2.10"), 32, false)];
            byte[] encoded = "[\"192.0.2.9/24\",\"192.0.2.10/32\"]"u8.ToArray();

            // Act
            // Assert
            return AssertBoundParameterAsync(values, 1041, encoded, parameterFormat: 0, parameterEncoding: CassieParameterFormat.Text);
        }

        [Fact]
        public Task ShouldEncodeExtendedTextArrayGivenExplicitCassieArrayWhenReaderExecutes()
        {
            // Arrange
            CassieArray values = new(869, [1], [1], [new CassieNetworkAddress(IPAddress.Parse("192.0.2.9"), 24, false)]);
            byte[] encoded = "{192.0.2.9/24}"u8.ToArray();

            // Act
            // Assert
            return AssertBoundParameterAsync(values, 1041, encoded, parameterFormat: 0, parameterEncoding: CassieParameterFormat.Text);
        }

        [Fact]
        public Task ShouldEncodeBinaryBitStringParameterGivenVarbitWhenReaderExecutes()
        {
            // Arrange
            CassieBitString value = new("10101", true);
            byte[] encoded = [0, 0, 0, 5, 0xa8];

            // Act
            Task binding = AssertBoundParameterAsync(value, 1562, encoded);

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldEncodeBinaryLsnParameterGivenPositionWhenReaderExecutes()
        {
            // Arrange
            CassieLsn value = new(0x000000010000000aUL);
            byte[] encoded = [0, 0, 0, 1, 0, 0, 0, 10];

            // Act
            Task binding = AssertBoundParameterAsync(value, 3220, encoded);

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldEncodeBinaryVectorParameterGivenTypedComponentsWhenReaderExecutes()
        {
            // Arrange

            // Act
            Task binding = AssertBoundParameterAsync(Vector123, 100003, BinaryVector123);

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldEncodeBinaryTimeTzParameterGivenTimeAndOffsetWhenReaderExecutes()
        {
            // Arrange
            CassieTimeWithTimeZone value = new(new TimeOnly(1, 2, 3), 3600);
            using MemoryStream expected = new();
            WriteInt64(expected, value.Time.Ticks / 10);
            WriteInt32(expected, -3600);
            Task binding = AssertBoundParameterAsync(value, 1266, expected.ToArray());

            // Act
            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldEncodeBinaryIntervalParameterGivenCalendarComponentsWhenReaderExecutes()
        {
            // Arrange
            CassieInterval value = new(14, -2, 1_500_000);
            using MemoryStream expected = new();
            WriteInt64(expected, value.Microseconds);
            WriteInt32(expected, value.Days);
            WriteInt32(expected, value.Months);
            Task binding = AssertBoundParameterAsync(value, 1186, expected.ToArray());

            // Act
            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldDecodeBinaryIntegerResultGivenBinaryResultsWhenReaderExecutes()
        {
            // Arrange
            byte[] value = [0, 0, 0, 42];

            // Act
            Task binding = AssertBoundParameterAsync(42, 23, value, resultFormat: 1);

            // Assert
            return binding;
        }

        [Fact]
        public async Task ShouldDecodeSupportedScalarBinaryResultsGivenCanonicalFramesWhenReaderExecutes()
        {
            // Arrange
            using MemoryStream time = new();
            WriteInt64(time, 3_723_004_000);
            using MemoryStream timestamp = new();
            WriteInt64(timestamp, 90_123_004_000);
            byte[] json = /*lang=json,strict*/ "{\"ready\":true}"u8.ToArray();
            byte[] jsonb = [1, .. json];
            (int Oid, byte[] Payload, object Expected, Type FieldType)[] cases =
            [
                (16, [1], true, typeof(bool)),
                (18, "x"u8.ToArray(), "x", typeof(string)),
                (19, "cassie"u8.ToArray(), "cassie", typeof(string)),
                (20, [0, 0, 0, 0, 0, 0, 0, 42], 42L, typeof(long)),
                (21, [0, 42], (short)42, typeof(short)),
                (25, "text"u8.ToArray(), "text", typeof(string)),
                (700, [0x3f, 0xc0, 0, 0], 1.5f, typeof(float)),
                (701, [0x3f, 0xf8, 0, 0, 0, 0, 0, 0], 1.5d, typeof(double)),
                (1042, "fixed"u8.ToArray(), "fixed", typeof(string)),
                (1043, "var"u8.ToArray(), "var", typeof(string)),
                (1082, [0, 0, 0, 1], new DateOnly(2000, 1, 2), typeof(DateOnly)),
                (1083, time.ToArray(), new TimeOnly(1, 2, 3, 4), typeof(TimeOnly)),
                (1114, timestamp.ToArray(), new DateTime(2000, 1, 2, 1, 2, 3, 4, DateTimeKind.Unspecified), typeof(DateTime)),
                (114, json, Encoding.UTF8.GetString(json), typeof(string)),
                (1184, timestamp.ToArray(), new DateTimeOffset(2000, 1, 2, 1, 2, 3, 4, TimeSpan.Zero), typeof(DateTimeOffset)),
                (3802, jsonb, Encoding.UTF8.GetString(json), typeof(string))
            ];

            // Act
            foreach ((int oid, byte[] payload, object expected, Type fieldType) in cases)
            {
                await AssertBoundParameterAsync(42, 23, BinaryInt42, resultFormat: 1, resultOid: oid,
                    resultValue: expected, encodedResult: payload, resultFieldType: fieldType);
            }

            // Assert
        }

        [Fact]
        public Task ShouldReadBinaryDateAsDateTimeGivenAdoNetGetterWhenReaderExecutes()
        {
            // Arrange
            DateTime expected = new(2000, 1, 2, 0, 0, 0, DateTimeKind.Unspecified);

            // Act
            Task read = AssertBoundParameterAsync(42, 23, BinaryInt42, resultFormat: 1, resultOid: 1082,
                encodedResult: [0, 0, 0, 1], resultFieldType: typeof(DateOnly), verifyResult: reader =>
                {
                    DateTime byGetter = reader.GetDateTime(0);
                    DateTime byGenericGetter = reader.GetFieldValue<DateTime>(0);
                    Assert.Equal(expected, byGetter);
                    Assert.Equal(DateTimeKind.Unspecified, byGetter.Kind);
                    Assert.Equal(expected, byGenericGetter);
                    Assert.Equal(DateTimeKind.Unspecified, byGenericGetter.Kind);
                    Assert.Equal(CassieTemporalValue.FromDateDays(1), reader.GetFieldValue<CassieTemporalValue>(0));
                });

            // Assert
            return read;
        }

        [Fact]
        public Task ShouldReadBinaryTimestampTzAsUtcDateTimeGivenAdoNetGetterWhenReaderExecutes()
        {
            // Arrange
            DateTime expected = new(2000, 1, 2, 1, 2, 3, 4, DateTimeKind.Utc);
            using MemoryStream timestamp = new();
            WriteInt64(timestamp, 90_123_004_000);

            // Act
            Task read = AssertBoundParameterAsync(42, 23, BinaryInt42, resultFormat: 1, resultOid: 1184,
                encodedResult: timestamp.ToArray(), resultFieldType: typeof(DateTimeOffset), verifyResult: reader =>
                {
                    DateTime byGetter = reader.GetDateTime(0);
                    DateTime byGenericGetter = reader.GetFieldValue<DateTime>(0);
                    Assert.Equal(expected, byGetter);
                    Assert.Equal(DateTimeKind.Utc, byGetter.Kind);
                    Assert.Equal(expected, byGenericGetter);
                    Assert.Equal(DateTimeKind.Utc, byGenericGetter.Kind);
                });

            // Assert
            return read;
        }

        [Fact]
        public async Task ShouldReadBinaryTemporalValuesOutsideClrRangeGivenCassieWireValuesWhenReaderExecutes()
        {
            // Arrange
            (int Oid, byte[] Payload, CassieTemporalKind Kind, long Value, int OffsetSeconds, Type FieldType)[] cases =
            [
                (1082, [0x7f, 0xff, 0xff, 0xff], CassieTemporalKind.Date, int.MaxValue, 0, typeof(DateOnly)),
                (1082, [0x3b, 0x9a, 0xca, 0x00], CassieTemporalKind.Date, 1_000_000_000, 0, typeof(DateOnly)),
                (1083, [0x00, 0x00, 0x00, 0x14, 0x1d, 0xd7, 0x60, 0x00], CassieTemporalKind.Time, 86_400_000_000, 0, typeof(TimeOnly)),
                (1266, [0x00, 0x00, 0x00, 0x14, 0x1d, 0xd7, 0x60, 0x00, 0xff, 0xff, 0xf1, 0xf0], CassieTemporalKind.TimeWithTimeZone, 86_400_000_000, 3600, typeof(CassieTimeWithTimeZone)),
                (1114, [0x80, 0, 0, 0, 0, 0, 0, 0], CassieTemporalKind.Timestamp, long.MinValue, 0, typeof(DateTime)),
                (1184, [0x7f, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff], CassieTemporalKind.TimestampWithTimeZone, long.MaxValue, 0, typeof(DateTimeOffset))
            ];

            // Act
            foreach ((int oid, byte[] payload, CassieTemporalKind kind, long rawValue, int offsetSeconds, Type fieldType) in cases)
            {
                await AssertBoundParameterAsync(42, 23, BinaryInt42, resultFormat: 1, resultOid: oid,
                    encodedResult: payload, resultFieldType: fieldType, verifyResult: reader =>
                    {
                        CassieTemporalValue value = Assert.IsType<CassieTemporalValue>(reader.GetValue(0));
                        Assert.Equal(kind, value.Kind);
                        Assert.Equal(rawValue, value.Value);
                        Assert.Equal(offsetSeconds, value.OffsetSeconds);
                        Assert.Equal(value, reader.GetFieldValue<CassieTemporalValue>(0));
                    });
            }

            // Assert
        }

        [Fact]
        public async Task ShouldReadTextTemporalSpecialsGivenCassieInfinityAndExtendedDateWhenReaderExecutes()
        {
            // Arrange
            (int Oid, string Text, CassieTemporalKind Kind, long? Value, Type FieldType)[] cases =
            [
                (1082, "infinity", CassieTemporalKind.Date, int.MaxValue, typeof(DateOnly)),
                (1083, "24:00:00", CassieTemporalKind.Time, 86_400_000_000, typeof(TimeOnly)),
                (1266, "24:00:00+01:00", CassieTemporalKind.TimeWithTimeZone, 86_400_000_000, typeof(CassieTimeWithTimeZone)),
                (1114, "-infinity", CassieTemporalKind.Timestamp, long.MinValue, typeof(DateTime)),
                (1184, "infinity", CassieTemporalKind.TimestampWithTimeZone, long.MaxValue, typeof(DateTimeOffset)),
                (1082, "10000-01-01", CassieTemporalKind.Date, null, typeof(DateOnly)),
                (1114, "10000-01-01 00:00:00", CassieTemporalKind.Timestamp, null, typeof(DateTime))
            ];

            // Act
            foreach ((int oid, string text, CassieTemporalKind kind, long? rawValue, Type fieldType) in cases)
            {
                await AssertBoundParameterAsync(42, 23, BinaryInt42, resultOid: oid, encodedResult: Encoding.UTF8.GetBytes(text),
                    resultFieldType: fieldType, verifyResult: reader =>
                    {
                        CassieTemporalValue value = Assert.IsType<CassieTemporalValue>(reader.GetValue(0));
                        Assert.Equal(kind, value.Kind);
                        if (rawValue is long expectedRawValue)
                        {
                            Assert.True(value.HasRawValue);
                            Assert.Equal(expectedRawValue, value.Value);
                        }
                        else
                        {
                            Assert.False(value.HasRawValue);
                            Assert.Equal(text, value.TextValue);
                        }
                    });
            }

            // Assert
        }

        [Fact]
        public Task ShouldDecodeTextTemporalValuesGivenCassieDateStyleWhenReaderExecutes()
        {
            // Arrange
            DateOnly expectedDate = new(2000, 1, 2);
            DateTime expectedTimestamp = new DateTime(2000, 1, 2, 3, 4, 5, DateTimeKind.Unspecified).AddTicks(60);
            DateTimeOffset expectedTimestampWithTimeZone = new DateTimeOffset(2000, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)).AddTicks(60);

            // Act
            Task[] reads =
            [
                AssertBoundParameterAsync(42, 23, BinaryInt42, resultOid: 1082, encodedResult: "02/01/2000"u8.ToArray(), resultValue: expectedDate, resultFieldType: typeof(DateOnly), startupDateStyle: "SQL, DMY"),
                AssertBoundParameterAsync(42, 23, BinaryInt42, resultOid: 1082, encodedResult: "02.01.2000"u8.ToArray(), resultValue: expectedDate, resultFieldType: typeof(DateOnly), startupDateStyle: "German, DMY"),
                AssertBoundParameterAsync(42, 23, BinaryInt42, resultOid: 1082, encodedResult: "02-01-2000"u8.ToArray(), resultValue: expectedDate, resultFieldType: typeof(DateOnly), startupDateStyle: "Postgres, DMY"),
                AssertBoundParameterAsync(42, 23, BinaryInt42, resultOid: 1114, encodedResult: "02/01/2000 03:04:05.000006"u8.ToArray(), resultValue: expectedTimestamp, resultFieldType: typeof(DateTime), startupDateStyle: "SQL, DMY"),
                AssertBoundParameterAsync(42, 23, BinaryInt42, resultOid: 1114, encodedResult: "Sun 02 Jan 03:04:05.000006 2000"u8.ToArray(), resultValue: expectedTimestamp, resultFieldType: typeof(DateTime), startupDateStyle: "Postgres, DMY"),
                AssertBoundParameterAsync(42, 23, BinaryInt42, resultOid: 1184, encodedResult: "02/01/2000 03:04:05.000006+02"u8.ToArray(), resultValue: expectedTimestampWithTimeZone, resultFieldType: typeof(DateTimeOffset), startupDateStyle: "SQL, DMY")
            ];

            // Assert
            return Task.WhenAll(reads);
        }

        [Fact]
        public Task ShouldPreserveNamedTimeZoneTextGivenTextTimestamptzWhenOffsetIsNotPresent()
        {
            // Arrange
            const string text = "02/01/2000 03:04:05 CET";

            // Act
            Task read = AssertBoundParameterAsync(42, 23, BinaryInt42, resultOid: 1184, encodedResult: Encoding.UTF8.GetBytes(text),
                resultFieldType: typeof(DateTimeOffset), verifyResult: reader =>
                {
                    CassieTemporalValue value = Assert.IsType<CassieTemporalValue>(reader.GetValue(0));
                    Assert.Equal(CassieTemporalKind.TimestampWithTimeZone, value.Kind);
                    Assert.Equal(text, value.TextValue);
                }, startupDateStyle: "SQL, DMY");

            // Assert
            return read;
        }

        [Fact]
        public async Task ShouldRefreshDateStyleAfterSetGivenLaterTextQueryWhenReaderExecutes()
        {
            // Arrange
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task peer = Task.Run(async () =>
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
                NetworkStream stream = client.GetStream();
                byte[] startup = new byte[8];
                await stream.ReadExactlyAsync(startup, TestContext.Current.CancellationToken);
                await ReadStartupPacketAsync(stream, startup);
                await WriteFrameAsync(stream, (byte)'R', BitConverter.GetBytes(IPAddress.HostToNetworkOrder(0)));
                using (MemoryStream status = new())
                {
                    WriteCString(status, "DateStyle");
                    WriteCString(status, "ISO, MDY");
                    await WriteFrameAsync(stream, (byte)'S', status.ToArray());
                }

                await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
                byte[] set = await ReadFrontendFrameAsync(stream).WaitAsync(TestContext.Current.CancellationToken);
                Assert.Equal((byte)'Q', set[0]);
                Assert.Equal("SET DateStyle = 'SQL, DMY'\0", Encoding.UTF8.GetString(set, 1, set.Length - 1));
                await WriteFrameAsync(stream, (byte)'C', "SET\0"u8.ToArray());
                await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);

                byte[] show = await ReadFrontendFrameAsync(stream).WaitAsync(TestContext.Current.CancellationToken);
                Assert.Equal((byte)'Q', show[0]);
                Assert.Equal("SHOW DateStyle\0", Encoding.UTF8.GetString(show, 1, show.Length - 1));
                using (MemoryStream description = new())
                {
                    WriteInt16(description, 1);
                    WriteCString(description, "DateStyle");
                    description.Write(new byte[4]);
                    WriteInt16(description, 0);
                    WriteInt32(description, 25);
                    WriteInt16(description, -1);
                    WriteInt32(description, -1);
                    WriteInt16(description, 0);
                    await WriteFrameAsync(stream, (byte)'T', description.ToArray());
                }

                using (MemoryStream row = new())
                {
                    WriteInt16(row, 1);
                    WriteInt32(row, "SQL, DMY"u8.Length);
                    row.Write("SQL, DMY"u8);
                    await WriteFrameAsync(stream, (byte)'D', row.ToArray());
                }

                await WriteFrameAsync(stream, (byte)'C', "SHOW\0"u8.ToArray());
                await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);

                byte[] select = await ReadFrontendFrameAsync(stream).WaitAsync(TestContext.Current.CancellationToken);
                Assert.Equal((byte)'Q', select[0]);
                Assert.Equal("SELECT DATE '2000-01-02'\0", Encoding.UTF8.GetString(select, 1, select.Length - 1));
                using (MemoryStream description = new())
                {
                    WriteInt16(description, 1);
                    WriteCString(description, "date");
                    description.Write(new byte[4]);
                    WriteInt16(description, 0);
                    WriteInt32(description, 1082);
                    WriteInt16(description, 4);
                    WriteInt32(description, -1);
                    WriteInt16(description, 0);
                    await WriteFrameAsync(stream, (byte)'T', description.ToArray());
                }

                using (MemoryStream row = new())
                {
                    WriteInt16(row, 1);
                    WriteInt32(row, "02/01/2000"u8.Length);
                    row.Write("02/01/2000"u8);
                    await WriteFrameAsync(stream, (byte)'D', row.ToArray());
                }

                await WriteFrameAsync(stream, (byte)'C', "SELECT 1\0"u8.ToArray());
                await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
                byte[] terminate = await ReadFrontendFrameAsync(stream).WaitAsync(TestContext.Current.CancellationToken);
                Assert.Equal((byte)'X', terminate[0]);
            }, TestContext.Current.CancellationToken);

            await using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;SSL Mode=Disable;Timeout=3;Pooling=false"
            };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using CassieCommand command = (CassieCommand)connection.CreateCommand();

            // Act
            command.CommandText = "SET DateStyle = 'SQL, DMY'";
            _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            command.CommandText = "SELECT DATE '2000-01-02'";
            await using DbDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));

            // Assert
            Assert.Equal(new DateOnly(2000, 1, 2), reader.GetValue(0));
            await reader.DisposeAsync();
            await connection.CloseAsync();
            await peer;
        }

        [Fact]
        public async Task ShouldDecodeLaterTextQueryGivenParameterStatusAfterSetWhenReaderExecutes()
        {
            // Arrange
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task peer = Task.Run(async () =>
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
                NetworkStream stream = client.GetStream();
                byte[] startup = new byte[8];
                await stream.ReadExactlyAsync(startup, TestContext.Current.CancellationToken);
                await ReadStartupPacketAsync(stream, startup);
                await WriteFrameAsync(stream, (byte)'R', BitConverter.GetBytes(IPAddress.HostToNetworkOrder(0)));
                using (MemoryStream initialStatus = new())
                {
                    WriteCString(initialStatus, "DateStyle");
                    WriteCString(initialStatus, "ISO, MDY");
                    await WriteFrameAsync(stream, (byte)'S', initialStatus.ToArray());
                }

                await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
                byte[] set = await ReadFrontendFrameAsync(stream).WaitAsync(TestContext.Current.CancellationToken);
                Assert.Equal((byte)'Q', set[0]);
                Assert.Equal("SET DateStyle = 'SQL, DMY'\0", Encoding.UTF8.GetString(set, 1, set.Length - 1));
                await WriteFrameAsync(stream, (byte)'C', "SET\0"u8.ToArray());
                using (MemoryStream changedStatus = new())
                {
                    WriteCString(changedStatus, "DateStyle");
                    WriteCString(changedStatus, "SQL, DMY");
                    await WriteFrameAsync(stream, (byte)'S', changedStatus.ToArray());
                }

                await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
                byte[] select = await ReadFrontendFrameAsync(stream).WaitAsync(TestContext.Current.CancellationToken);
                Assert.Equal((byte)'Q', select[0]);
                Assert.Equal("SELECT DATE '2000-01-02'\0", Encoding.UTF8.GetString(select, 1, select.Length - 1));
                using (MemoryStream description = new())
                {
                    WriteInt16(description, 1);
                    WriteCString(description, "date");
                    description.Write(new byte[4]);
                    WriteInt16(description, 0);
                    WriteInt32(description, 1082);
                    WriteInt16(description, 4);
                    WriteInt32(description, -1);
                    WriteInt16(description, 0);
                    await WriteFrameAsync(stream, (byte)'T', description.ToArray());
                }

                using (MemoryStream row = new())
                {
                    WriteInt16(row, 1);
                    WriteInt32(row, "02/01/2000"u8.Length);
                    row.Write("02/01/2000"u8);
                    await WriteFrameAsync(stream, (byte)'D', row.ToArray());
                }

                await WriteFrameAsync(stream, (byte)'C', "SELECT 1\0"u8.ToArray());
                await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
                byte[] terminate = await ReadFrontendFrameAsync(stream).WaitAsync(TestContext.Current.CancellationToken);
                Assert.Equal((byte)'X', terminate[0]);
            }, TestContext.Current.CancellationToken);

            await using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;SSL Mode=Disable;Timeout=3;Pooling=false"
            };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using CassieCommand command = (CassieCommand)connection.CreateCommand();

            // Act
            command.CommandText = "SET DateStyle = 'SQL, DMY'";
            _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            command.CommandText = "SELECT DATE '2000-01-02'";
            await using DbDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));

            // Assert
            Assert.Equal(new DateOnly(2000, 1, 2), reader.GetValue(0));
            await reader.DisposeAsync();
            await connection.CloseAsync();
            await peer;
        }

        [Fact]
        public Task ShouldEncodeTaggedTemporalParametersGivenExtendedAndInfiniteValuesWhenReaderExecutes()
        {
            // Arrange
            CassieTemporalValue[] values =
            [
                CassieTemporalValue.FromDateDays(int.MaxValue),
                CassieTemporalValue.FromTimeMicroseconds(86_400_000_000),
                CassieTemporalValue.FromTimeWithTimeZoneMicroseconds(86_400_000_000, 3600),
                CassieTemporalValue.FromTimestampMicroseconds(long.MinValue),
                CassieTemporalValue.FromTimestampWithTimeZoneMicroseconds(long.MaxValue)
            ];
            int[] oids = [1082, 1083, 1266, 1114, 1184];
            byte[][] encodedValues =
            [
                [0x7f, 0xff, 0xff, 0xff],
                [0x00, 0x00, 0x00, 0x14, 0x1d, 0xd7, 0x60, 0x00],
                [0x00, 0x00, 0x00, 0x14, 0x1d, 0xd7, 0x60, 0x00, 0xff, 0xff, 0xf1, 0xf0],
                [0x80, 0, 0, 0, 0, 0, 0, 0],
                [0x7f, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff]
            ];

            // Act
            // Assert
            return Task.WhenAll(values.Select((value, index) => AssertBoundParameterAsync(value, oids[index], encodedValues[index])));
        }

        [Fact]
        public Task ShouldEncodeTaggedTemporalTextGivenRawAndPreservedValuesWhenReaderExecutes()
        {
            // Arrange
            CassieTemporalValue dateValue = CassieTemporalValue.FromDateDays(1);
            CassieTemporalValue preservedValue = CassieTemporalValue.FromText(CassieTemporalKind.Date, "10000-01-01");
            CassieTemporalValue timeWithTimeZoneValue = CassieTemporalValue.FromTimeWithTimeZoneMicroseconds(86_400_000_000, 3600);

            // Act
            // Assert
            return Task.WhenAll(
                AssertBoundParameterAsync(dateValue, 1082, "2000-01-02"u8.ToArray(), parameterFormat: 0, parameterEncoding: CassieParameterFormat.Text),
                AssertBoundParameterAsync(preservedValue, 1082, "10000-01-01"u8.ToArray(), parameterFormat: 0),
                AssertBoundParameterAsync(timeWithTimeZoneValue, 1266, "24:00:00+01:00"u8.ToArray(), parameterFormat: 0, parameterEncoding: CassieParameterFormat.Text));
        }

        [Fact]
        public Task ShouldEncodeTaggedTemporalArrayGivenExtendedDateWhenReaderExecutes()
        {
            // Arrange
            CassieTemporalValue[] value = [CassieTemporalValue.FromDateDays(int.MaxValue)];
            byte[] encoded = [
                0, 0, 0, 1,
                0, 0, 0, 0,
                0, 0, 4, 58,
                0, 0, 0, 1,
                0, 0, 0, 1,
                0, 0, 0, 4,
                0x7f, 0xff, 0xff, 0xff
            ];

            // Act
            Task binding = AssertBoundParameterAsync(value, 1182, encoded);

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldDecodeTaggedTemporalArrayGivenInfinityElementWhenReaderExecutes()
        {
            // Arrange
            byte[] result = [
                0, 0, 0, 1,
                0, 0, 0, 0,
                0, 0, 4, 58,
                0, 0, 0, 1,
                0, 0, 0, 1,
                0, 0, 0, 4,
                0x7f, 0xff, 0xff, 0xff
            ];

            // Act
            Task read = AssertBoundParameterAsync(42, 23, BinaryInt42, resultFormat: 1, resultOid: 1182,
                encodedResult: result, resultFieldType: typeof(CassieArray), verifyResult: reader =>
                {
                    CassieArray values = Assert.IsType<CassieArray>(reader.GetValue(0));
                    Assert.Equal(1082, values.ElementTypeOid);
                    Assert.Equal(CassieTemporalValue.FromDateDays(int.MaxValue), values.GetValue(1));
                });

            // Assert
            return read;
        }

        [Fact]
        public async Task ShouldDecodeSupportedScalarTextResultsGivenCanonicalValuesWhenReaderExecutes()
        {
            // Arrange
            byte[] json = /*lang=json,strict*/ "{\"ready\":true}"u8.ToArray();
            Guid uuid = Guid.Parse("550e8400-e29b-41d4-a716-446655440001");
            (int Oid, string Payload, object Expected, Type FieldType)[] cases =
            [
                (16, "t", true, typeof(bool)),
                (16, "f", false, typeof(bool)),
                (18, "x", "x", typeof(string)),
                (19, "cassie", "cassie", typeof(string)),
                (20, "42", 42L, typeof(long)),
                (21, "42", (short)42, typeof(short)),
                (23, "42", 42, typeof(int)),
                (24, "sum", "sum", typeof(string)),
                (25, "text", "text", typeof(string)),
                (26, "42", 42u, typeof(uint)),
                (705, "unknown-value", "unknown-value", typeof(string)),
                (650, "192.0.2.0/24", "192.0.2.0/24", typeof(string)),
                (700, "1.5", 1.5f, typeof(float)),
                (701, "1.5", 1.5d, typeof(double)),
                (774, "00:11:22:33:44:55:66:77", "00:11:22:33:44:55:66:77", typeof(string)),
                (829, "00:11:22:33:44:55", "00:11:22:33:44:55", typeof(string)),
                (869, "192.0.2.9/24", "192.0.2.9/24", typeof(string)),
                (1042, "fixed", "fixed", typeof(string)),
                (1043, "var", "var", typeof(string)),
                (1082, "2000-01-02", new DateOnly(2000, 1, 2), typeof(DateOnly)),
                (1083, "01:02:03.004", new TimeOnly(1, 2, 3, 4), typeof(TimeOnly)),
                (1114, "2000-01-02 01:02:03.004", new DateTime(2000, 1, 2, 1, 2, 3, 4, DateTimeKind.Unspecified), typeof(DateTime)),
                (114, Encoding.UTF8.GetString(json), Encoding.UTF8.GetString(json), typeof(string)),
                (1184, "2000-01-02 01:02:03.004+00:00", new DateTimeOffset(2000, 1, 2, 1, 2, 3, 4, TimeSpan.Zero), typeof(DateTimeOffset)),
                (1186, "1 day 02:03:04", "1 day 02:03:04", typeof(string)),
                (1266, "01:02:03.004+00", new CassieTimeWithTimeZone(new TimeOnly(1, 2, 3, 4), 0), typeof(CassieTimeWithTimeZone)),
                (1560, "101", "101", typeof(string)),
                (1562, "101", "101", typeof(string)),
                (1700, "9007199254740993.1255", new CassieNumeric("9007199254740993.1255"), typeof(CassieNumeric)),
                (2205, "public.items", "public.items", typeof(string)),
                (2950, uuid.ToString("D"), uuid, typeof(Guid)),
                (3220, "16/B374D848", "16/B374D848", typeof(string)),
                (3802, Encoding.UTF8.GetString(json), Encoding.UTF8.GetString(json), typeof(string)),
                (4072, "$.ready", "$.ready", typeof(string)),
                (100003, "[1,2,3]", "[1,2,3]", typeof(string)),
                (1007, "[1,2]", "[1,2]", typeof(string)),
                (1003, "[\"cassie\"]", "[\"cassie\"]", typeof(string)),
                (2249, "record-value", "record-value", typeof(string)),
                (2278, string.Empty, string.Empty, typeof(string)),
                (2287, "[record-value]", "[record-value]", typeof(string))
            ];

            // Act
            foreach ((int oid, string payload, object expected, Type fieldType) in cases)
            {
                await AssertBoundParameterAsync(42, 23, BinaryInt42, resultOid: oid,
                    resultValue: expected, encodedResult: Encoding.UTF8.GetBytes(payload), resultFieldType: fieldType);
            }

            // Assert
        }

        [Fact]
        public Task ShouldRejectInvalidBooleanTextResultGivenServerFrameWhenReaderReads()
        {
            // Arrange
            byte[] malformedValue = "not-a-boolean"u8.ToArray();

            // Act
            Task read = AssertBoundParameterAsync(42, 23, BinaryInt42, resultOid: 16,
                encodedResult: malformedValue, resultFieldType: typeof(bool), verifyResult: reader =>
                    Assert.Throws<FormatException>(() => reader.GetValue(0)));

            // Assert
            return read;
        }

        [Fact]
        public Task ShouldDecodeBinaryByteArrayResultGivenByteaWhenReaderExecutes()
        {
            // Arrange
            byte[] encoded = [0xde, 0xad, 0xbe, 0xef];

            // Act
            // Assert
            return AssertBoundParameterAsync(42, 23, BinaryInt42, resultFormat: 1, resultOid: 17,
                encodedResult: encoded, resultFieldType: typeof(byte[]), verifyResult: reader =>
                    Assert.Equal(encoded, Assert.IsType<byte[]>(reader.GetValue(0))));
        }

        [Fact]
        public Task ShouldDecodeTextByteArrayResultGivenHexPayloadWhenReaderExecutes()
        {
            // Arrange
            byte[] expected = [0xde, 0xad, 0xbe, 0xef];

            // Act
            // Assert
            return AssertBoundParameterAsync(42, 23, BinaryInt42, resultOid: 17,
                encodedResult: "\\xDEADBEEF"u8.ToArray(), resultFieldType: typeof(byte[]), verifyResult: reader =>
                    Assert.Equal(expected, Assert.IsType<byte[]>(reader.GetValue(0))));
        }

        [Fact]
        public Task ShouldDescribeUnknownTypeGivenNullExpressionWhenReaderExecutes()
        {
            // Arrange
            byte[] value = [0, 0, 0, 42];

            // Act
            Task binding = AssertBoundParameterAsync(42, 23, value, resultOid: 705,
                resultTypeName: "unknown", resultFieldType: typeof(string));

            // Assert
            return binding;
        }

        [Theory]
        [InlineData(26, "oid")]
        [InlineData(24, "regproc")]
        [InlineData(2202, "regprocedure")]
        [InlineData(2203, "regoper")]
        [InlineData(2204, "regoperator")]
        [InlineData(2205, "regclass")]
        [InlineData(2206, "regtype")]
        [InlineData(3734, "regconfig")]
        [InlineData(3769, "regdictionary")]
        [InlineData(4089, "regnamespace")]
        [InlineData(4096, "regrole")]
        [InlineData(4191, "regcollation")]
        public Task ShouldBindAndDecodeBinaryObjectIdentifierGivenOidWhenReaderExecutes(int typeOid, string typeName)
        {
            // Arrange
            byte[] encoded = [0, 0, 0, 42];

            // Act
            Task binding = AssertBoundParameterAsync(42u, typeOid, encoded, resultFormat: 1, resultOid: typeOid,
                resultValue: 42u, encodedResult: encoded, parameterOid: typeOid, resultTypeName: typeName, resultFieldType: typeof(uint));

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldRejectMalformedBinaryObjectIdentifierGivenWrongLengthWhenReaderReads()
        {
            // Arrange
            byte[] encoded = [0, 0, 42];

            // Act
            Task binding = AssertBoundParameterAsync(42u, 2205, [0, 0, 0, 42], resultFormat: 1, resultOid: 2205,
                encodedResult: encoded, parameterOid: 2205, verifyResult: reader =>
                    Assert.Throws<FormatException>(() => reader.GetValue(0)));

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldDecodeBinaryUuidResultGivenBinaryResultsWhenReaderExecutes()
        {
            // Arrange
            Guid value = Guid.Parse("550e8400-e29b-41d4-a716-446655440001");
            byte[] encoded = [0x55, 0x0e, 0x84, 0x00, 0xe2, 0x9b, 0x41, 0xd4, 0xa7, 0x16, 0x44, 0x66, 0x55, 0x44, 0x00, 0x01];

            // Act
            Task binding = AssertBoundParameterAsync(value, 2950, encoded, resultFormat: 1, resultOid: 2950, resultValue: value, encodedResult: encoded);

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldDecodeBinaryJsonPathResultGivenVersionedTextFrameWhenReaderExecutes()
        {
            // Arrange
            const string value = "$.foo";
            byte[] encoded = [1, .. Encoding.UTF8.GetBytes(value)];

            // Act
            Task binding = AssertBoundParameterAsync(42, 23, BinaryInt42, resultFormat: 1, resultOid: 4072, resultValue: value, encodedResult: encoded);

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldDecodeBinaryExactNumericResultGivenBinaryResultsWhenReaderExecutes()
        {
            // Arrange
            byte[] encoded = [0x00, 0x05, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x11, 0x97, 0x17, 0x6c, 0x0a, 0xb1, 0x01, 0xf0, 0x15, 0xfc];
            CassieNumeric expected = new("4503599627370496.5628");

            // Act
            // Assert
            return AssertBoundParameterAsync(42, 23, BinaryInt42, resultFormat: 1, resultOid: 1700, resultValue: expected, encodedResult: encoded);
        }

        [Fact]
        public Task ShouldDecodeBinaryNumericSpecialResultGivenNaNWhenReaderExecutes()
        {
            // Arrange
            byte[] encoded = [0, 0, 0, 0, 0xc0, 0, 0, 0];
            CassieNumeric expected = new("NaN");

            // Act
            // Assert
            return AssertBoundParameterAsync(42, 23, BinaryInt42, resultFormat: 1, resultOid: 1700, resultValue: expected, encodedResult: encoded);
        }

        [Fact]
        public Task ShouldDecodeBinaryInetResultGivenHostAddressAndPrefixWhenReaderExecutes()
        {
            // Arrange
            byte[] encoded = [2, 24, 0, 4, 192, 0, 2, 9];

            // Act
            // Assert
            return AssertBoundParameterAsync(42, 23, BinaryInt42, resultFormat: 1, resultOid: 869, encodedResult: encoded, verifyResult: reader =>
            {
                Assert.Equal(typeof(CassieNetworkAddress), reader.GetFieldType(0));
                CassieNetworkAddress address = Assert.IsType<CassieNetworkAddress>(reader.GetValue(0));
                Assert.Equal("192.0.2.9", address.Address.ToString());
                Assert.Equal(24, address.PrefixLength);
                Assert.False(address.IsCidr);
            });
        }

        [Fact]
        public Task ShouldExposeTextNetworkResultsAsStringsGivenNonBinaryFormatWhenReaderExecutes()
        {
            // Arrange

            // Act

            // Assert
            return AssertBoundParameterAsync(42, 23, BinaryInt42, resultFormat: 0, resultOid: 869, encodedResult: "192.0.2.0/24"u8.ToArray(), verifyResult: reader =>
            {
                Assert.Equal("192.0.2.0/24", reader.GetValue(0));
                Assert.Equal("192.0.2.0/24", reader.GetString(0));
            }, resultFieldType: typeof(string));
        }

        [Fact]
        public Task ShouldReportVoidDataTypeNameGivenVoidResultWhenReaderExecutes()
        {
            // Arrange

            // Act

            // Assert
            return AssertBoundParameterAsync(42, 23, BinaryInt42, resultOid: 2278, resultTypeName: "void", resultFieldType: typeof(string));
        }

        [Fact]
        public Task ShouldDecodeBinaryVoidResultGivenZeroLengthPayloadWhenReaderReads()
        {
            // Arrange
            byte[] encoded = [];

            // Act
            Task binding = AssertBoundParameterAsync(42, 23, BinaryInt42, resultFormat: 1, resultOid: 2278,
                resultValue: string.Empty, encodedResult: encoded, resultFieldType: typeof(string));

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldRejectMalformedBinaryVoidResultGivenNonemptyPayloadWhenReaderReads()
        {
            // Arrange
            byte[] encoded = [0];

            // Act
            Task binding = AssertBoundParameterAsync(42, 23, BinaryInt42, resultFormat: 1, resultOid: 2278,
                encodedResult: encoded, verifyResult: reader => _ = Assert.Throws<FormatException>(() => reader.GetValue(0)),
                resultFieldType: typeof(string));

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldReportRecordDataTypeNameGivenRecordResultWhenReaderExecutes()
        {
            // Arrange

            // Act

            // Assert
            return AssertBoundParameterAsync(42, 23, BinaryInt42, resultOid: 2249, resultTypeName: "record", resultFieldType: typeof(string));
        }

        [Fact]
        public Task ShouldReportRecordArrayDataTypeNameGivenRecordArrayResultWhenReaderExecutes()
        {
            // Arrange

            // Act

            // Assert
            return AssertBoundParameterAsync(42, 23, BinaryInt42, resultOid: 2287, resultTypeName: "record[]", resultFieldType: typeof(string));
        }

        [Fact]
        public Task ShouldDecodeBinaryMacAddressResultGivenSixByteFrameWhenReaderExecutes()
        {
            // Arrange
            byte[] encoded = [0x01, 0x23, 0x45, 0x67, 0x89, 0xab];

            // Act
            // Assert
            return AssertBoundParameterAsync(42, 23, BinaryInt42, resultFormat: 1, resultOid: 829, encodedResult: encoded, verifyResult: reader =>
            {
                System.Net.NetworkInformation.PhysicalAddress address = Assert.IsType<System.Net.NetworkInformation.PhysicalAddress>(reader.GetValue(0));
                Assert.Equal(encoded, address.GetAddressBytes());
            });
        }

        [Fact]
        public Task ShouldDecodeBinaryMacAddressResultGivenEightByteFrameWhenReaderExecutes()
        {
            // Arrange
            byte[] encoded = [0x01, 0x23, 0x45, 0x67, 0x89, 0xab, 0xcd, 0xef];

            // Act
            // Assert
            return AssertBoundParameterAsync(42, 23, BinaryInt42, resultFormat: 1, resultOid: 774, encodedResult: encoded, verifyResult: reader =>
            {
                System.Net.NetworkInformation.PhysicalAddress address = Assert.IsType<System.Net.NetworkInformation.PhysicalAddress>(reader.GetValue(0));
                Assert.Equal(encoded, address.GetAddressBytes());
            });
        }

        [Fact]
        public Task ShouldDecodeBinaryCidrResultGivenCanonicalNetworkPayloadWhenReaderExecutes()
        {
            // Arrange
            byte[] encoded = [2, 24, 1, 4, 192, 0, 2, 0];

            // Act
            // Assert
            return AssertBoundParameterAsync(42, 23, BinaryInt42, resultFormat: 1, resultOid: 650, encodedResult: encoded, verifyResult: reader =>
            {
                CassieNetworkAddress address = Assert.IsType<CassieNetworkAddress>(reader.GetValue(0));
                Assert.Equal("192.0.2.0", address.Address.ToString());
                Assert.Equal(24, address.PrefixLength);
                Assert.True(address.IsCidr);
            });
        }

        [Fact]
        public Task ShouldDecodeBinaryVarbitResultGivenNonByteAlignedLengthWhenReaderExecutes()
        {
            // Arrange
            byte[] encoded = [0, 0, 0, 5, 0xa8];

            // Act
            // Assert
            return AssertBoundParameterAsync(42, 23, BinaryInt42, resultFormat: 1, resultOid: 1562, encodedResult: encoded, verifyResult: reader =>
            {
                CassieBitString bits = Assert.IsType<CassieBitString>(reader.GetValue(0));
                Assert.Equal("10101", bits.Value);
                Assert.True(bits.IsVarying);
            });
        }

        [Fact]
        public Task ShouldDecodeBinaryFixedBitResultGivenNonByteAlignedLengthWhenReaderExecutes()
        {
            // Arrange
            byte[] encoded = [0, 0, 0, 3, 0xa0];

            // Act
            // Assert
            return AssertBoundParameterAsync(42, 23, BinaryInt42, resultFormat: 1, resultOid: 1560, encodedResult: encoded, verifyResult: reader =>
            {
                CassieBitString bits = Assert.IsType<CassieBitString>(reader.GetValue(0));
                Assert.Equal("101", bits.Value);
                Assert.False(bits.IsVarying);
            });
        }

        [Fact]
        public Task ShouldDecodeBinaryLsnResultGivenUnsignedEightBytePositionWhenReaderExecutes()
        {
            // Arrange
            byte[] encoded = [0, 0, 0, 1, 0, 0, 0, 10];

            // Act
            // Assert
            return AssertBoundParameterAsync(42, 23, BinaryInt42, resultFormat: 1, resultOid: 3220, encodedResult: encoded, verifyResult: reader =>
            {
                CassieLsn lsn = Assert.IsType<CassieLsn>(reader.GetValue(0));
                Assert.Equal(0x000000010000000aUL, lsn.Value);
                Assert.Equal("1/A", lsn.ToString());
            });
        }

        [Fact]
        public Task ShouldDecodeBinaryTimeTzResultGivenTimeAndWestOffsetWhenReaderExecutes()
        {
            // Arrange
            using MemoryStream encoded = new();
            WriteInt64(encoded, 3_723_000_000);
            WriteInt32(encoded, -3600);

            // Act
            // Assert
            return AssertBoundParameterAsync(42, 23, BinaryInt42, resultFormat: 1, resultOid: 1266, encodedResult: encoded.ToArray(), verifyResult: reader =>
            {
                CassieTimeWithTimeZone value = Assert.IsType<CassieTimeWithTimeZone>(reader.GetValue(0));
                Assert.Equal(new TimeOnly(1, 2, 3), value.Time);
                Assert.Equal(3600, value.OffsetSeconds);
            });
        }

        [Fact]
        public Task ShouldReadBinaryTimeResultAsTimeSpanGivenGenericFieldGetterWhenReaderExecutes()
        {
            // Arrange
            TimeSpan expected = new TimeSpan(1, 2, 3).Add(TimeSpan.FromTicks(4_567_890));
            byte[] encoded = [0x00, 0x00, 0x00, 0x00, 0xdd, 0xef, 0x71, 0x15];

            // Act
            // Assert
            return AssertBoundParameterAsync(42, 23, BinaryInt42,
                resultFormat: 1,
                resultOid: 1083,
                encodedResult: encoded,
                verifyResult: reader => Assert.Equal(expected, reader.GetFieldValue<TimeSpan>(0)));
        }

        [Fact]
        public Task ShouldReadExactNumericAsUInt64GivenGenericFieldGetterWhenReaderExecutes()
        {
            // Arrange
            byte[] encoded = [0x00, 0x05, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x07, 0x34, 0x1a, 0x58, 0x02, 0xe1, 0x03, 0xbb, 0x06, 0x4f];

            // Act
            // Assert
            return AssertBoundParameterAsync(42, 23, BinaryInt42,
                resultFormat: 1,
                resultOid: 1700,
                encodedResult: encoded,
                verifyResult: reader =>
                {
                    Assert.Equal(ulong.MaxValue, reader.GetFieldValue<ulong>(0));
                    Assert.Equal(new CassieNumeric("18446744073709551615"), reader.GetValue(0));
                });
        }

        [Fact]
        public Task ShouldReadTextNumericAsUInt64GivenGenericFieldGetterWhenReaderExecutes()
        {
            // Arrange
            byte[] encoded = "18446744073709551615"u8.ToArray();

            // Act
            // Assert
            return AssertBoundParameterAsync(42, 23, BinaryInt42,
                resultFormat: 0,
                resultOid: 1700,
                encodedResult: encoded,
                verifyResult: reader =>
                {
                    Assert.Equal(ulong.MaxValue, reader.GetFieldValue<ulong>(0));
                    Assert.Equal(new CassieNumeric("18446744073709551615"), reader.GetValue(0));
                });
        }

        [Fact]
        public Task ShouldRejectFractionalNumericAsUInt64GivenGenericFieldGetterWhenReaderExecutes()
        {
            // Arrange
            byte[] encoded = [0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x13, 0x88];

            // Act
            // Assert
            return AssertBoundParameterAsync(42, 23, BinaryInt42,
                resultFormat: 1,
                resultOid: 1700,
                encodedResult: encoded,
                verifyResult: reader => Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<ulong>(0)));
        }

        [Fact]
        public Task ShouldRejectNumericAboveUInt64RangeGivenGenericFieldGetterWhenReaderExecutes()
        {
            // Arrange
            byte[] encoded = [0x00, 0x05, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x07, 0x34, 0x1a, 0x58, 0x02, 0xe1, 0x03, 0xbb, 0x06, 0x50];

            // Act
            // Assert
            return AssertBoundParameterAsync(42, 23, BinaryInt42,
                resultFormat: 1,
                resultOid: 1700,
                encodedResult: encoded,
                verifyResult: reader => Assert.Throws<OverflowException>(() => reader.GetFieldValue<ulong>(0)));
        }

        [Fact]
        public Task ShouldDecodeBinaryIntervalResultGivenIndependentComponentsWhenReaderExecutes()
        {
            // Arrange
            using MemoryStream encoded = new();
            WriteInt64(encoded, 1_500_000);
            WriteInt32(encoded, -2);
            WriteInt32(encoded, 14);

            // Act
            // Assert
            return AssertBoundParameterAsync(42, 23, BinaryInt42, resultFormat: 1, resultOid: 1186, encodedResult: encoded.ToArray(), verifyResult: reader =>
            {
                CassieInterval value = Assert.IsType<CassieInterval>(reader.GetValue(0));
                Assert.Equal(14, value.Months);
                Assert.Equal(-2, value.Days);
                Assert.Equal(1_500_000, value.Microseconds);
            });
        }

        [Fact]
        public Task ShouldDecodeBinaryVectorResultGivenDimensionedFloat4PayloadWhenReaderExecutes()
        {
            // Arrange

            // Act
            // Assert
            return AssertBoundParameterAsync(42, 23, BinaryInt42, resultFormat: 1, resultOid: 100003, encodedResult: BinaryVector123, verifyResult: reader =>
            {
                Assert.Equal(typeof(CassieVector), reader.GetFieldType(0));
                CassieVector value = Assert.IsType<CassieVector>(reader.GetValue(0));
                Assert.Equal(3, value.Dimension);
                Assert.Equal(1f, value[0]);
                Assert.Equal(2f, value[1]);
                Assert.Equal(3f, value[2]);
            });
        }

        [Fact]
        public Task ShouldDecodeBinaryNumericArrayResultGivenNullAndExactValuesWhenReaderExecutes()
        {
            // Arrange
            using MemoryStream encoded = new();
            WriteInt32(encoded, 1);
            WriteInt32(encoded, 1);
            WriteInt32(encoded, 1700);
            WriteInt32(encoded, 3);
            WriteInt32(encoded, 1);
            byte[] positive = [0, 2, 0, 0, 0, 0, 0, 2, 0, 1, 9, 0xc4];
            byte[] negative = [0, 2, 0, 0, 0x40, 0, 0, 1, 0, 2, 0x13, 0x88];
            WriteInt32(encoded, positive.Length);
            encoded.Write(positive);
            WriteInt32(encoded, -1);
            WriteInt32(encoded, negative.Length);
            encoded.Write(negative);

            // Act
            // Assert
            return AssertBoundParameterAsync(42, 23, BinaryInt42, resultFormat: 1, resultOid: 1231, encodedResult: encoded.ToArray(), verifyResult: reader =>
            {
                CassieArray values = Assert.IsType<CassieArray>(reader.GetValue(0));
                Assert.Equal(1700, values.ElementTypeOid);
                Assert.Equal(3, values.Dimensions[0]);
                Assert.Equal("1.25", Assert.IsType<CassieNumeric>(values[0]).Value);
                Assert.Null(values[1]);
                Assert.Equal("-2.5", Assert.IsType<CassieNumeric>(values[2]).Value);
            });
        }

        [Fact]
        public Task ShouldPreserveBinaryArrayShapeAndBoundsGivenMultidimensionalResultWhenReaderExecutes()
        {
            // Arrange
            using MemoryStream encoded = new();
            WriteInt32(encoded, 2); // rank
            WriteInt32(encoded, 1); // contains null
            WriteInt32(encoded, 23); // int4 element OID
            WriteInt32(encoded, 2);
            WriteInt32(encoded, 0);
            WriteInt32(encoded, 2);
            WriteInt32(encoded, -1);
            WriteInt32(encoded, 4); WriteInt32(encoded, 1);
            WriteInt32(encoded, -1);
            WriteInt32(encoded, 4); WriteInt32(encoded, 3);
            WriteInt32(encoded, 4); WriteInt32(encoded, 4);

            // Act
            // Assert
            return AssertBoundParameterAsync(42, 23, BinaryInt42, resultFormat: 1, resultOid: 1007,
                encodedResult: encoded.ToArray(), verifyResult: reader =>
                {
                    CassieArray array = Assert.IsType<CassieArray>(reader.GetValue(0));
                    Assert.Equal(23, array.ElementTypeOid);
                    Assert.Equal(2, array.Dimensions[0]);
                    Assert.Equal(2, array.Dimensions[1]);
                    Assert.Equal(0, array.LowerBounds[0]);
                    Assert.Equal(-1, array.LowerBounds[1]);
                    Assert.Equal(1, array.GetValue(0, -1));
                    Assert.Null(array.GetValue(0, 0));
                    Assert.Equal(3, array.GetValue(1, -1));
                    Assert.Equal(4, array.GetValue(1, 0));
                });
        }

        [Fact]
        public Task ShouldDecodeEmptyBinaryArrayGivenRankZeroResultWhenReaderExecutes()
        {
            // Arrange
            using MemoryStream encoded = new();
            WriteInt32(encoded, 0);
            WriteInt32(encoded, 0);
            WriteInt32(encoded, 23);

            // Act
            // Assert
            return AssertBoundParameterAsync(42, 23, BinaryInt42, resultFormat: 1, resultOid: 1007,
                encodedResult: encoded.ToArray(), verifyResult: reader =>
                {
                    CassieArray array = Assert.IsType<CassieArray>(reader.GetValue(0));
                    Assert.Equal(0, array.Rank);
                    Assert.Empty(array);
                });
        }

        [Fact]
        public Task ShouldRejectEmptyBinaryArrayWithNullFlagGivenServerResultWhenReaderReads()
        {
            // Arrange
            using MemoryStream encoded = new();
            WriteInt32(encoded, 0);
            WriteInt32(encoded, 1);
            WriteInt32(encoded, 23);

            // Act
            // Assert
            return AssertBoundParameterAsync(42, 23, BinaryInt42, resultFormat: 1, resultOid: 1007,
                encodedResult: encoded.ToArray(), verifyResult: reader =>
                {
                    _ = Assert.Throws<FormatException>(() => reader.GetValue(0));
                });
        }

        [Fact]
        public void ShouldRejectZeroLengthDimensionGivenCassieArrayWhenConstructed()
        {
            // Arrange
            // Act
            // Assert
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => new CassieArray(23, [0], [1], []));
        }

        [Fact]
        public void ShouldRejectOverflowingUpperBoundGivenCassieArrayWhenConstructed()
        {
            // Arrange
            // Act
            // Assert
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => new CassieArray(23, [2], [int.MaxValue], [1, 2]));
        }

        [Fact]
        public void ShouldPreserveMaxInt32UpperBoundGivenCassieArrayWhenConvertedToClrArray()
        {
            // Arrange
            CassieArray cassieArray = new(23, [2, 2], [1, int.MaxValue - 1], [1, 2, 3, 4]);

            // Act
            Array clrArray = cassieArray.ToArray();

            // Assert
            Assert.Equal(1, clrArray.GetValue(1, int.MaxValue - 1));
            Assert.Equal(2, clrArray.GetValue(1, int.MaxValue));
            Assert.Equal(3, clrArray.GetValue(2, int.MaxValue - 1));
            Assert.Equal(4, clrArray.GetValue(2, int.MaxValue));
        }

        [Fact]
        public Task ShouldEncodeMaxInt32ArrayBoundGivenCassieArrayWhenParameterUsesTextFormat()
        {
            // Arrange
            CassieArray cassieArray = new(23, [1], [int.MaxValue], [42]);

            // Act
            Task binding = AssertBoundParameterAsync(cassieArray, 1007, "[2147483647:2147483647]={42}"u8.ToArray(),
                parameterFormat: 0, parameterEncoding: CassieParameterFormat.Text);

            // Assert
            return binding;
        }

        [Fact]
        public Task ShouldRejectZeroLengthBinaryArrayDimensionGivenServerResultWhenReaderReads()
        {
            // Arrange
            using MemoryStream encoded = new();
            WriteInt32(encoded, 1);
            WriteInt32(encoded, 0);
            WriteInt32(encoded, 23);
            WriteInt32(encoded, 0);
            WriteInt32(encoded, 1);

            // Act
            // Assert
            return AssertBoundParameterAsync(42, 23, BinaryInt42, resultFormat: 1, resultOid: 1007,
                encodedResult: encoded.ToArray(), verifyResult: reader =>
                {
                    _ = Assert.Throws<FormatException>(() => reader.GetValue(0));
                });
        }

        [Fact]
        public Task ShouldRejectOverflowingBinaryArrayBoundsGivenServerResultWhenReaderReads()
        {
            // Arrange
            using MemoryStream encoded = new();
            WriteInt32(encoded, 1);
            WriteInt32(encoded, 0);
            WriteInt32(encoded, 23);
            WriteInt32(encoded, 2);
            WriteInt32(encoded, int.MaxValue);
            WriteInt32(encoded, 4);
            WriteInt32(encoded, 1);
            WriteInt32(encoded, 4);
            WriteInt32(encoded, 2);

            // Act
            // Assert
            return AssertBoundParameterAsync(42, 23, BinaryInt42, resultFormat: 1, resultOid: 1007,
                encodedResult: encoded.ToArray(), verifyResult: reader =>
                {
                    _ = Assert.Throws<FormatException>(() => reader.GetValue(0));
                });
        }

        [Fact]
        public Task ShouldMapUuidArrayResultGivenPostgresOidWhenReaderExecutes()
        {
            // Arrange
            byte[] value = [0, 0, 0, 42];

            // Act
            Task binding = AssertBoundParameterAsync(42, 23, value, resultOid: 2951, resultTypeName: "uuid[]");

            // Assert
            return binding;
        }

        [Fact]
        public async Task ShouldBindBinaryParameterGivenNamedIntegerMarkerWhenReaderExecutes()
        {
            // Arrange
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task peer = Task.Run(async () => await ServeOneBoundQueryAsync(listener), TestContext.Current.CancellationToken);
            using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;Password=secret;SSL Mode=Disable;Timeout=3;Pooling=false"
            };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using CassieCommand command = (CassieCommand)connection.CreateCommand();
            command.CommandText = "SELECT @value";
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", Value = 42 });

            // Act
            await using DbDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            bool hasRow = await reader.ReadAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.True(hasRow);
            Assert.Equal(42, reader.GetInt32(0));
            Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken));
            await reader.DisposeAsync();
            await connection.CloseAsync();
            await peer;
        }

        private static async Task ServeOneBoundQueryAsync(TcpListener listener, int typeOid = 23, byte[]? expectedValue = null, short parameterFormat = 1, short resultFormat = 0, int resultOid = 23, byte[]? encodedResult = null, byte[]? encodedDataRowPayload = null, string? startupDateStyle = null)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            NetworkStream stream = client.GetStream();
            byte[] startup = new byte[8];
            await stream.ReadExactlyAsync(startup);
            await ReadStartupPacketAsync(stream, startup);
            await WriteFrameAsync(stream, (byte)'R', BitConverter.GetBytes(IPAddress.HostToNetworkOrder(0)));
            if (startupDateStyle is not null)
            {
                using MemoryStream parameterStatus = new();
                WriteCString(parameterStatus, "DateStyle");
                WriteCString(parameterStatus, startupDateStyle);
                await WriteFrameAsync(stream, (byte)'S', parameterStatus.ToArray());
            }

            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
            byte[] parse = await ReadFrontendFrameAsync(stream);
            byte[] bind = await ReadFrontendFrameAsync(stream);
            byte[] describe = await ReadFrontendFrameAsync(stream);
            byte[] execute = await ReadFrontendFrameAsync(stream);
            byte[] flush = await ReadFrontendFrameAsync(stream);
            Assert.Equal((byte)'P', parse[0]);
            Assert.Contains("SELECT $1", Encoding.UTF8.GetString(parse), StringComparison.Ordinal);
            Assert.Equal(new byte[] { 0, 1, (byte)(typeOid >> 24), (byte)(typeOid >> 16), (byte)(typeOid >> 8), (byte)typeOid }, parse[^6..]);
            Assert.Equal((byte)'B', bind[0]);
            Assert.Equal((byte)'D', describe[0]);
            Assert.Equal((byte)'E', execute[0]);
            Assert.Equal((byte)'H', flush[0]);
            Assert.Equal(128, IPAddress.NetworkToHostOrder(BitConverter.ToInt32(execute, execute.Length - sizeof(int))));
            using MemoryStream expectedBind = new();
            WriteCString(expectedBind, string.Empty);
            WriteCString(expectedBind, string.Empty);
            WriteInt16(expectedBind, 1);
            WriteInt16(expectedBind, parameterFormat);
            WriteInt16(expectedBind, 1);
            byte[] encodedValue = expectedValue ?? [0, 0, 0, 42];
            WriteInt32(expectedBind, encodedValue.Length);
            expectedBind.Write(encodedValue);
            WriteInt16(expectedBind, resultFormat == 0 ? (short)0 : (short)1);
            if (resultFormat != 0)
            {
                WriteInt16(expectedBind, resultFormat);
            }
            Assert.Equal(expectedBind.ToArray(), bind.Skip(1));

            await WriteFrameAsync(stream, (byte)'1', []);
            await WriteFrameAsync(stream, (byte)'2', []);
            using MemoryStream description = new();
            WriteInt16(description, 1);
            WriteCString(description, "value");
            description.Write(new byte[4]);
            WriteInt16(description, 0);
            WriteInt32(description, resultOid);
            bool arrayType = resultOid is 1000 or 1001 or 1002 or 1003 or 1005 or 1007 or 1009 or 1014 or 1015 or 1016 or 1021 or 1022 or 1028 or 1040 or 1041 or 1115 or 1182 or 1183 or 1185 or 1187 or 1231 or 1270 or 1561 or 1563 or 199 or 651 or 2951 or 3221 or 3807 or 775;
            short typeLength = resultOid switch
            {
                16 => 1,
                18 => 1,
                19 => 64,
                2249 or 2287 => -1,
                20 or 701 or 1083 or 1114 or 1184 or 3220 => 8,
                21 => 2,
                23 or 26 or 700 or 1082 => 4,
                1266 => 12,
                1186 => 16,
                2950 => 16,
                774 => 8,
                829 => 6,
                17 or 25 or 1042 or 1043 or 114 or 650 or 869 or 1560 or 1562 or 1700 or 3802 or 4072 => -1,
                >= 100001 and <= 132767 => -1,
                _ when arrayType => -1,
                _ => 4
            };
            WriteInt16(description, typeLength);
            WriteInt32(description, -1);
            WriteInt16(description, resultFormat);
            await WriteFrameAsync(stream, (byte)'T', description.ToArray());
            byte[] dataRowPayload;
            if (encodedDataRowPayload is not null)
            {
                dataRowPayload = encodedDataRowPayload;
            }
            else
            {
                using MemoryStream row = new();
                WriteInt16(row, 1);
                byte[] resultValue = encodedResult ?? (resultFormat == 0 ? "42"u8.ToArray() : [0, 0, 0, 42]);
                WriteInt32(row, resultValue.Length);
                row.Write(resultValue);
                dataRowPayload = row.ToArray();
            }

            await WriteFrameAsync(stream, (byte)'D', dataRowPayload);
            await WriteFrameAsync(stream, (byte)'C', [.. Encoding.UTF8.GetBytes("SELECT 1"), 0]);
            byte[] sync = await ReadFrontendFrameAsync(stream);
            Assert.Equal((byte)'S', sync[0]);
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
        }

        private static byte[] CreateRowDescriptionPayload(short formatCode = 0)
        {
            using MemoryStream description = new();
            WriteInt16(description, 1);
            WriteCString(description, "value");
            description.Write(new byte[4]);
            WriteInt16(description, 0);
            WriteInt32(description, 23);
            WriteInt16(description, 4);
            WriteInt32(description, -1);
            WriteInt16(description, formatCode);
            return description.ToArray();
        }

        private static async Task ServeMalformedRowDescriptionAsync(TcpListener listener, byte[] payload)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            NetworkStream stream = client.GetStream();
            byte[] startup = new byte[8];
            await stream.ReadExactlyAsync(startup, TestContext.Current.CancellationToken);
            await ReadStartupPacketAsync(stream, startup);
            await WriteFrameAsync(stream, (byte)'R', BitConverter.GetBytes(IPAddress.HostToNetworkOrder(0)));
            await WriteFrameAsync(stream, (byte)'Z', [(byte)'I']);
            for (int index = 0; index < 5; index++)
            {
                _ = await ReadFrontendFrameAsync(stream);
            }

            await WriteFrameAsync(stream, (byte)'1', []);
            await WriteFrameAsync(stream, (byte)'2', []);
            await WriteFrameAsync(stream, (byte)'T', payload);
        }

        private static async Task AssertMalformedRowDescriptionAsync(byte[] payload)
        {
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task peer = Task.Run(async () => await ServeMalformedRowDescriptionAsync(listener, payload), TestContext.Current.CancellationToken);
            await using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;Password=secret;SSL Mode=Disable;Timeout=3;Pooling=false"
            };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using CassieCommand command = (CassieCommand)connection.CreateCommand();
            command.CommandText = "SELECT @value";
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", Value = 42 });

            IOException? exception = null;
            DbDataReader? reader = null;
            try
            {
                reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            }
            catch (IOException error)
            {
                exception = error;
            }

            if (reader is not null)
            {
                try
                {
                    await reader.DisposeAsync();
                }
                catch (IOException)
                {
                }
            }

            Assert.NotNull(exception);
            Assert.Contains("RowDescription", exception.Message, StringComparison.Ordinal);
            await connection.CloseAsync();
            await peer;
        }

        private static async Task AssertBoundParameterAsync(object value, int typeOid, byte[] encodedValue, short parameterFormat = 1, short resultFormat = 0, int resultOid = 23, object? resultValue = null, byte[]? encodedResult = null, int parameterOid = 0, string? resultTypeName = null, Action<DbDataReader>? verifyResult = null, Type? resultFieldType = null, CassieParameterFormat parameterEncoding = CassieParameterFormat.Auto, DbType parameterDbType = DbType.Object, string? startupDateStyle = null)
        {
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task peer = Task.Run(async () => await ServeOneBoundQueryAsync(listener, parameterOid == 0 ? typeOid : parameterOid, encodedValue, parameterFormat: parameterFormat, resultFormat: resultFormat, resultOid: resultOid, encodedResult: encodedResult, startupDateStyle: startupDateStyle));
            using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;Password=secret;SSL Mode=Disable;Timeout=3;Pooling=false"
            };
            await connection.OpenAsync();
            using CassieCommand command = (CassieCommand)connection.CreateCommand();
            command.CommandText = "SELECT @value";
            command.BinaryResults = resultFormat != 0;
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", DbType = parameterDbType, Value = value, PostgresTypeOid = parameterOid, ParameterFormat = parameterEncoding });
            DbDataReader reader;
            try
            {
                reader = await command.ExecuteReaderAsync();
            }
            catch
            {
                await peer;
                throw;
            }

            await using (reader)
            {
                if (resultTypeName is not null)
                {
                    Assert.Equal(resultTypeName, reader.GetDataTypeName(0));
                }

                if (resultFieldType is not null)
                {
                    Assert.Equal(resultFieldType, reader.GetFieldType(0));
                }

                if (resultFormat != 0 || resultValue is not null || verifyResult is not null)
                {
                    Assert.True(await reader.ReadAsync());
                    if (verifyResult is null)
                    {
                        Assert.Equal(resultValue ?? 42, reader.GetValue(0));
                    }
                    else
                    {
                        verifyResult(reader);
                    }
                }

                await reader.DisposeAsync();
            }

            await connection.CloseAsync();
            await peer;
        }

        private static async Task AssertMalformedDataRowAsync(byte[] encodedDataRowPayload)
        {
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task peer = Task.Run(async () => await ServeOneBoundQueryAsync(listener, expectedValue: [0, 0, 0, 42], encodedDataRowPayload: encodedDataRowPayload), TestContext.Current.CancellationToken);
            await using CassieConnection connection = new()
            {
                ConnectionString = $"Host=127.0.0.1;Port={port};Database=ledger;Username=app;Password=secret;SSL Mode=Disable;Timeout=3;Pooling=false"
            };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using CassieCommand command = (CassieCommand)connection.CreateCommand();
            command.CommandText = "SELECT @value";
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", Value = 42 });
            await using DbDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

            IOException exception = await Assert.ThrowsAsync<IOException>(() => reader.ReadAsync(TestContext.Current.CancellationToken));

            Assert.Contains("DataRow", exception.Message, StringComparison.Ordinal);
            await reader.DisposeAsync();
            await connection.CloseAsync();
            await peer;
        }

        private static async Task ReadStartupPacketAsync(NetworkStream stream, byte[] prefix)
        {
            int length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(prefix, 0));
            await stream.ReadExactlyAsync(new byte[length - prefix.Length]);
        }

        private static async Task<byte[]> ReadFrontendFrameAsync(NetworkStream stream)
        {
            byte[] header = new byte[5];
            await stream.ReadExactlyAsync(header);
            int length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(header, 1));
            byte[] payload = new byte[length - 4];
            await stream.ReadExactlyAsync(payload);
            return [header[0], .. payload];
        }

        private static async Task WriteFrameAsync(NetworkStream stream, byte tag, byte[] payload)
        {
            byte[] frame = new byte[payload.Length + 5];
            frame[0] = tag;
            BitConverter.GetBytes(IPAddress.HostToNetworkOrder(payload.Length + 4)).CopyTo(frame, 1);
            payload.CopyTo(frame, 5);
            await stream.WriteAsync(frame);
        }

        private static void WriteInt16(Stream stream, short value)
        {
            stream.Write(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(value)));
        }

        private static void WriteInt32(Stream stream, int value)
        {
            stream.Write(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(value)));
        }

        private static void WriteInt64(Stream stream, long value)
        {
            stream.Write(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(value)));
        }

        private static void WriteCString(Stream stream, string value) { stream.Write(Encoding.UTF8.GetBytes(value)); stream.WriteByte(0); }
    }
}
