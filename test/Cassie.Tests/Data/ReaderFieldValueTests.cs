using Cntryl.Cassie.Data;
using Cntryl.Cassie.Types;
using System.Buffers.Binary;
using System.Data;
using System.Globalization;
using System.Text;
using Xunit;

namespace Cntryl.Cassie.Tests.Data
{
    public sealed class ReaderFieldValueTests
    {
        private const int Bool = 16;
        private const int Int4 = 23;
        private const int Oid = 26;
        private const int Text = 25;
        private const int Float8 = 701;
        private const int Date = 1082;
        private const int Time = 1083;
        private const int Timestamp = 1114;
        private const int TimestampTz = 1184;
        private const int TimeTz = 1266;
        private const int Numeric = 1700;
        private const int Uuid = 2950;
        private const string NullField = "<null>";

        [Theory]
        [InlineData("decimal", Numeric, 0, "12.50", "ok:12.50")]
        [InlineData("decimal", Numeric, 1, "00010000000000040001", "ok:1.0000")]
        [InlineData("decimal", Numeric, 0, "NaN", "ex:FormatException")]
        [InlineData("decimal", Int4, 0, "5", "ok:5")]
        [InlineData("decimal", Numeric, 0, NullField, "ex:InvalidCastException")]
        [InlineData("datetime", Timestamp, 1, "0000000000000000", "ok:630822816000000000/Unspecified")]
        [InlineData("datetime", TimestampTz, 1, "0000000000000000", "ok:630822816000000000/Utc")]
        [InlineData("datetime", Date, 1, "00000000", "ok:630822816000000000/Unspecified")]
        [InlineData("datetime", Date, 1, "7FFFFFFF", "ex:InvalidCastException")]
        [InlineData("datetime", Timestamp, 0, "2024-01-02 03:04:05", "ok:638397614450000000/Unspecified")]
        [InlineData("datetime", Int4, 0, "5", "ex:InvalidCastException")]
        [InlineData("datetime", Timestamp, 0, NullField, "ex:InvalidCastException")]
        [InlineData("timespan", Time, 1, "0000000B18777838", "ok:13:14:15.1230000")]
        [InlineData("timespan", Time, 0, "13:14:15.5", "ok:13:14:15.5000000")]
        [InlineData("timespan", Time, 0, "24:00:00", "ok:1.00:00:00")]
        [InlineData("timespan", Timestamp, 1, "0000000000000000", "ex:InvalidCastException")]
        [InlineData("timespan", Time, 0, NullField, "ex:InvalidCastException")]
        [InlineData("ulong", Oid, 0, "4294967295", "ok:4294967295")]
        [InlineData("ulong", Oid, 1, "FFFFFFFF", "ok:4294967295")]
        [InlineData("ulong", Numeric, 0, "18446744073709551615", "ok:18446744073709551615")]
        [InlineData("ulong", Numeric, 0, "18446744073709551616", "ex:OverflowException")]
        [InlineData("ulong", Numeric, 0, "-1", "ex:InvalidCastException")]
        [InlineData("ulong", Numeric, 0, "1.5", "ex:InvalidCastException")]
        [InlineData("ulong", Int4, 0, "5", "ex:InvalidCastException")]
        [InlineData("ulong", Oid, 0, NullField, "ex:InvalidCastException")]
        [InlineData("temporal", Date, 1, "7FFFFFFF", "ok:Date/infinity")]
        [InlineData("temporal", Date, 1, "00000000", "ok:Date/Date(0)")]
        [InlineData("temporal", Time, 1, "0000000B18777838", "ok:Time/Time(47655123000)")]
        [InlineData("temporal", TimeTz, 1, "0000000000000001FFFFE3E0", "ok:TimeWithTimeZone/TimeWithTimeZone(1)")]
        [InlineData("temporal", Timestamp, 1, "7FFFFFFFFFFFFFFF", "ok:Timestamp/infinity")]
        [InlineData("temporal", TimestampTz, 1, "0000000000000000", "ok:TimestampWithTimeZone/TimestampWithTimeZone(0)")]
        [InlineData("temporal", Int4, 0, "5", "ex:InvalidCastException")]
        [InlineData("temporal", Date, 0, NullField, "ex:InvalidCastException")]
        [InlineData("int", Int4, 0, "42", "ok:42")]
        [InlineData("int", Int4, 1, "0000002A", "ok:42")]
        [InlineData("int", Text, 0, "42", "ex:InvalidCastException")]
        [InlineData("int", Int4, 0, NullField, "ex:InvalidCastException")]
        [InlineData("long", Int4, 0, "42", "ex:InvalidCastException")]
        [InlineData("string", Text, 0, "hello", "ok:hello")]
        [InlineData("string", Int4, 0, "42", "ex:InvalidCastException")]
        [InlineData("string", Text, 0, NullField, "ex:InvalidCastException")]
        [InlineData("bool", Bool, 0, "t", "ok:True")]
        [InlineData("bool", Bool, 1, "00", "ok:False")]
        [InlineData("double", Float8, 1, "3FF8000000000000", "ok:1.5")]
        [InlineData("double", Int4, 0, "1", "ex:InvalidCastException")]
        [InlineData("guid", Uuid, 0, "6f9619ff-8b86-d011-b42d-00c04fc964ff", "ok:6f9619ff-8b86-d011-b42d-00c04fc964ff")]
        [InlineData("object", Int4, 0, "42", "ok:42")]
        [InlineData("object", Text, 0, NullField, "ok:DBNull")]
        [InlineData("dbnull", Text, 0, NullField, "ok:DBNull")]
        [InlineData("numeric", Numeric, 0, "1.50", "ok:1.50")]
        [InlineData("nullable-decimal", Numeric, 0, "1.5", "ex:InvalidCastException")]
        [InlineData("nullable-int", Int4, 0, "1", "ok:1")]
        [InlineData("nullable-int", Int4, 0, NullField, "ex:InvalidCastException")]
        public async Task ShouldReturnSameOutcomeGivenWireFieldWhenGenericFieldValueIsRequested(string requested, int typeOid, short format, string payload, string expected)
        {
            // Arrange
            byte[]? field = payload == NullField ? null : format == 0 ? Encoding.UTF8.GetBytes(payload) : Convert.FromHexString(payload);
            await using CassieDataReader reader = await OpenAsync([(typeOid, format)], [field]);

            // Act
            string actual = Run(reader, requested);

            // Assert
            Assert.Equal(expected, actual);
        }

        [Fact]
        public async Task ShouldRejectBackwardReadGivenSequentialAccessWhenGenericFieldValueIsRequested()
        {
            // Arrange
            await using CassieDataReader reader = await OpenAsync(
                [(Numeric, 0), (Numeric, 0)],
                [Encoding.UTF8.GetBytes("1"), Encoding.UTF8.GetBytes("2")],
                CommandBehavior.SequentialAccess);

            // Act
            decimal ReadAt(int ordinal)
            {
                return reader.GetFieldValue<decimal>(ordinal);
            }

            decimal second = ReadAt(1);

            void ReadBackward()
            {
                _ = ReadAt(0);
            }

            // Assert
            Assert.Equal(2m, second);
            _ = Assert.Throws<InvalidOperationException>(ReadBackward);
        }

        [Fact]
        public async Task ShouldRejectOutOfRangeOrdinalGivenGenericFieldValueWhenOrdinalIsInvalid()
        {
            // Arrange
            await using CassieDataReader reader = await OpenAsync([(Numeric, 0)], [Encoding.UTF8.GetBytes("1")]);

            // Act
            void ReadDecimal()
            {
                _ = reader.GetFieldValue<decimal>(4);
            }

            void ReadString()
            {
                _ = reader.GetFieldValue<string>(4);
            }

            // Assert
            _ = Assert.Throws<IndexOutOfRangeException>(ReadDecimal);
            _ = Assert.Throws<IndexOutOfRangeException>(ReadString);
        }

        private static string Run(CassieDataReader reader, string requested)
        {
            try
            {
                return "ok:" + requested switch
                {
                    "decimal" => reader.GetFieldValue<decimal>(0).ToString(CultureInfo.InvariantCulture),
                    "datetime" => FormatDateTime(reader.GetFieldValue<DateTime>(0)),
                    "timespan" => reader.GetFieldValue<TimeSpan>(0).ToString("c", CultureInfo.InvariantCulture),
                    "ulong" => reader.GetFieldValue<ulong>(0).ToString(CultureInfo.InvariantCulture),
                    "temporal" => FormatTemporal(reader.GetFieldValue<CassieTemporalValue>(0)),
                    "int" => reader.GetFieldValue<int>(0).ToString(CultureInfo.InvariantCulture),
                    "long" => reader.GetFieldValue<long>(0).ToString(CultureInfo.InvariantCulture),
                    "string" => reader.GetFieldValue<string>(0),
                    "bool" => reader.GetFieldValue<bool>(0).ToString(),
                    "double" => reader.GetFieldValue<double>(0).ToString("R", CultureInfo.InvariantCulture),
                    "guid" => reader.GetFieldValue<Guid>(0).ToString(),
                    "object" => FormatObject(reader.GetFieldValue<object>(0)),
                    "dbnull" => FormatObject(reader.GetFieldValue<DBNull>(0)),
                    "numeric" => reader.GetFieldValue<CassieNumeric>(0).Value,
                    "nullable-decimal" => Convert.ToString(reader.GetFieldValue<decimal?>(0), CultureInfo.InvariantCulture),
                    "nullable-int" => Convert.ToString(reader.GetFieldValue<int?>(0), CultureInfo.InvariantCulture),
                    _ => throw new ArgumentException("Unknown type.", nameof(requested))
                };
            }
            catch (Exception exception) when (exception is not ArgumentException)
            {
                return "ex:" + exception.GetType().Name;
            }
        }

        private static string FormatObject(object value)
        {
            return value is DBNull ? "DBNull" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static string FormatDateTime(DateTime value)
        {
            return value.Ticks.ToString(CultureInfo.InvariantCulture) + "/" + value.Kind;
        }

        private static string FormatTemporal(CassieTemporalValue value)
        {
            return value.Kind + "/" + value;
        }

        private static async Task<CassieDataReader> OpenAsync((int TypeOid, short Format)[] columns, byte[]?[] fields, CommandBehavior behavior = CommandBehavior.Default)
        {
            FakeReaderSession session = new();
            session.Enqueue((byte)'T', RowDescription(columns));
            session.Enqueue((byte)'D', DataRow(fields));
            session.Enqueue((byte)'C', Encoding.UTF8.GetBytes("SELECT 1\0"));
            session.Enqueue((byte)'Z', [(byte)'I']);
            CassieDataReader reader = new(session, commandTimeout: 0, behavior);
            await reader.InitializeAsync(TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            return reader;
        }

        private static byte[] RowDescription((int TypeOid, short Format)[] columns)
        {
            using MemoryStream payload = new();
            WriteInt16(payload, (short)columns.Length);
            for (int index = 0; index < columns.Length; index++)
            {
                payload.Write(Encoding.UTF8.GetBytes($"c{index}\0"));
                WriteInt32(payload, 0);
                WriteInt16(payload, 0);
                WriteInt32(payload, columns[index].TypeOid);
                WriteInt16(payload, -1);
                WriteInt32(payload, -1);
                WriteInt16(payload, columns[index].Format);
            }

            return payload.ToArray();
        }

        private static byte[] DataRow(byte[]?[] fields)
        {
            using MemoryStream payload = new();
            WriteInt16(payload, (short)fields.Length);
            foreach (byte[]? field in fields)
            {
                WriteInt32(payload, field?.Length ?? -1);
                if (field is not null)
                {
                    payload.Write(field);
                }
            }

            return payload.ToArray();
        }

        private static void WriteInt16(MemoryStream stream, short value)
        {
            Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteInt16BigEndian(bytes, value);
            stream.Write(bytes);
        }

        private static void WriteInt32(MemoryStream stream, int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            stream.Write(bytes);
        }
    }
}
