using Cntryl.Cassie.Data;
using Cntryl.Cassie.Types;
using System.Buffers.Binary;
using System.Data;
using System.Globalization;
using System.Text;
using Xunit;

namespace Cntryl.Cassie.Tests.Data
{
    public sealed class ReaderTypedGetterTests
    {
        private const int Bool = 16;
        private const int Int4 = 23;
        private const int Text = 25;
        private const int Float4 = 700;
        private const int Float8 = 701;
        private const int Date = 1082;
        private const int Timestamp = 1114;
        private const int TimestampTz = 1184;
        private const int Numeric = 1700;
        private const int Uuid = 2950;
        private const string NullField = "<null>";

        [Theory]
        // boolean
        [InlineData("bool", Bool, 0, "t", "ok:True")]
        [InlineData("bool", Bool, 0, "f", "ok:False")]
        [InlineData("bool", Bool, 0, "true", "ok:True")]
        [InlineData("bool", Bool, 0, "false", "ok:False")]
        [InlineData("bool", Bool, 0, "TRUE", "ok:True")]
        [InlineData("bool", Bool, 0, "FALSE", "ok:False")]
        [InlineData("bool", Bool, 0, "True", "ex:FormatException")]
        [InlineData("bool", Bool, 0, "1", "ex:FormatException")]
        [InlineData("bool", Bool, 0, "", "ex:FormatException")]
        [InlineData("bool", Bool, 1, "01", "ok:True")]
        [InlineData("bool", Bool, 1, "00", "ok:False")]
        [InlineData("bool", Bool, 1, "02", "ex:NotSupportedException")]
        [InlineData("bool", Bool, 1, "0001", "ex:NotSupportedException")]
        [InlineData("bool", Text, 0, "true", "ok:True")]
        [InlineData("bool", Text, 0, "x", "ex:FormatException")]
        [InlineData("bool", Int4, 0, "1", "ok:True")]
        [InlineData("bool", Int4, 0, "0", "ok:False")]
        [InlineData("bool", Bool, 0, NullField, "ex:InvalidCastException")]
        // float8 through GetDouble
        [InlineData("double", Float8, 0, "1.5", "ok:1.5")]
        [InlineData("double", Float8, 0, "-0", "ok:-0")]
        [InlineData("double", Float8, 0, " 2.5 ", "ok:2.5")]
        [InlineData("double", Float8, 0, "1,000.5", "ok:1000.5")]
        [InlineData("double", Float8, 0, "NaN", "ok:NaN")]
        [InlineData("double", Float8, 0, "Infinity", "ok:Infinity")]
        [InlineData("double", Float8, 0, "-Infinity", "ok:-Infinity")]
        [InlineData("double", Float8, 0, "1e309", "ok:Infinity")]
        [InlineData("double", Float8, 0, "1E-2", "ok:0.01")]
        [InlineData("double", Float8, 0, "0.1", "ok:0.1")]
        [InlineData("double", Float8, 0, "abc", "ex:FormatException")]
        [InlineData("double", Float8, 0, "", "ex:FormatException")]
        [InlineData("double", Float8, 0, "1.5x", "ex:FormatException")]
        [InlineData("double", Float8, 1, "3FF8000000000000", "ok:1.5")]
        [InlineData("double", Float8, 1, "7FF8000000000000", "ok:NaN")]
        [InlineData("double", Float8, 1, "3FF80000", "ex:NotSupportedException")]
        [InlineData("double", Float4, 0, "1.1", "ok:1.100000023841858")]
        [InlineData("double", Float4, 1, "3F800000", "ok:1")]
        [InlineData("double", Float4, 1, "3F8000", "ex:NotSupportedException")]
        [InlineData("double", Int4, 0, "7", "ok:7")]
        [InlineData("double", Text, 0, "2.5", "ok:2.5")]
        [InlineData("double", Numeric, 0, "1.25", "ok:1.25")]
        [InlineData("double", Float8, 0, NullField, "ex:InvalidCastException")]
        // float4 through GetFloat
        [InlineData("float", Float4, 0, "1.1", "ok:1.1")]
        [InlineData("float", Float4, 0, "3.5e38", "ok:Infinity")]
        [InlineData("float", Float4, 0, "NaN", "ok:NaN")]
        [InlineData("float", Float4, 0, " 4 ", "ok:4")]
        [InlineData("float", Float4, 0, "x", "ex:FormatException")]
        [InlineData("float", Float4, 1, "3F800000", "ok:1")]
        [InlineData("float", Float4, 1, "3F80", "ex:NotSupportedException")]
        [InlineData("float", Float8, 0, "1.1", "ok:1.1")]
        [InlineData("float", Float8, 0, "0.30000000000000004", "ok:0.3")]
        [InlineData("float", Float8, 1, "3FF199999999999A", "ok:1.1")]
        [InlineData("float", Int4, 0, "3", "ok:3")]
        [InlineData("float", Float4, 0, NullField, "ex:InvalidCastException")]
        // uuid
        [InlineData("guid", Uuid, 0, "6f9619ff-8b86-d011-b42d-00c04fc964ff", "ok:6f9619ff-8b86-d011-b42d-00c04fc964ff")]
        [InlineData("guid", Uuid, 0, "6F9619FF-8B86-D011-B42D-00C04FC964FF", "ok:6f9619ff-8b86-d011-b42d-00c04fc964ff")]
        [InlineData("guid", Uuid, 0, "{6f9619ff-8b86-d011-b42d-00c04fc964ff}", "ok:6f9619ff-8b86-d011-b42d-00c04fc964ff")]
        [InlineData("guid", Uuid, 0, "6f9619ff8b86d011b42d00c04fc964ff", "ok:6f9619ff-8b86-d011-b42d-00c04fc964ff")]
        [InlineData("guid", Uuid, 0, " 6f9619ff-8b86-d011-b42d-00c04fc964ff ", "ok:6f9619ff-8b86-d011-b42d-00c04fc964ff")]
        [InlineData("guid", Uuid, 0, "nope", "ex:FormatException")]
        [InlineData("guid", Uuid, 0, "", "ex:FormatException")]
        [InlineData("guid", Uuid, 1, "6F9619FF8B86D011B42D00C04FC964FF", "ok:6f9619ff-8b86-d011-b42d-00c04fc964ff")]
        [InlineData("guid", Uuid, 1, "6F9619FF8B86D011B42D00C04FC964", "ex:NotSupportedException")]
        [InlineData("guid", Text, 0, "6f9619ff-8b86-d011-b42d-00c04fc964ff", "ok:6f9619ff-8b86-d011-b42d-00c04fc964ff")]
        [InlineData("guid", Uuid, 0, NullField, "ex:InvalidCastException")]
        // numeric through GetDecimal
        [InlineData("decimal", Numeric, 0, "123.456", "ok:123.456")]
        [InlineData("decimal", Numeric, 0, "-0.0", "ok:0.0")]
        [InlineData("decimal", Numeric, 0, "1.50", "ok:1.50")]
        [InlineData("decimal", Numeric, 0, "0", "ok:0")]
        [InlineData("decimal", Numeric, 0, "-12", "ok:-12")]
        [InlineData("decimal", Numeric, 0, "+5", "ok:5")]
        [InlineData("decimal", Numeric, 0, "1e3", "ok:1000")]
        [InlineData("decimal", Numeric, 0, ".5", "ok:0.5")]
        [InlineData("decimal", Numeric, 0, "5.", "ok:5")]
        [InlineData("decimal", Numeric, 0, "NaN", "ex:FormatException")]
        [InlineData("decimal", Numeric, 0, "79228162514264337593543950336", "ex:OverflowException")]
        [InlineData("decimal", Numeric, 0, "79228162514264337593543950335", "ok:79228162514264337593543950335")]
        [InlineData("decimal", Numeric, 0, " 1", "ex:FormatException")]
        [InlineData("decimal", Numeric, 0, "1 ", "ex:FormatException")]
        [InlineData("decimal", Numeric, 0, "abc", "ex:FormatException")]
        [InlineData("decimal", Numeric, 0, "", "ex:FormatException")]
        [InlineData("decimal", Numeric, 1, "00010000000000040001", "ok:1.0000")]
        [InlineData("decimal", Int4, 0, "5", "ok:5")]
        [InlineData("decimal", Text, 0, "5.5", "ok:5.5")]
        [InlineData("decimal", Numeric, 0, NullField, "ex:InvalidCastException")]
        // date / timestamp / timestamptz through GetDateTime
        [InlineData("datetime", Timestamp, 1, "0000000000000000", "ok:630822816000000000/Unspecified")]
        [InlineData("datetime", Timestamp, 1, "FFFFFFFFFFFFFFFF", "ok:630822815999999990/Unspecified")]
        [InlineData("datetime", Timestamp, 1, "0000000000000001", "ok:630822816000000010/Unspecified")]
        [InlineData("datetime", Timestamp, 1, "7FFFFFFFFFFFFFFF", "ex:InvalidCastException")]
        [InlineData("datetime", Timestamp, 1, "8000000000000000", "ex:InvalidCastException")]
        [InlineData("datetime", Timestamp, 1, "7FFFFFFFFFFFFFFE", "ex:InvalidCastException")]
        [InlineData("datetime", Timestamp, 1, "0000", "ex:NotSupportedException")]
        [InlineData("datetime", TimestampTz, 1, "0000000000000000", "ok:630822816000000000/Utc")]
        [InlineData("datetime", TimestampTz, 1, "0000000100000000", "ok:630822858949672960/Utc")]
        [InlineData("datetime", TimestampTz, 1, "7FFFFFFFFFFFFFFF", "ex:InvalidCastException")]
        [InlineData("datetime", TimestampTz, 1, "FFFFFFFFFFFFFFFF", "ok:630822815999999990/Utc")]
        [InlineData("datetime", Date, 1, "00000000", "ok:630822816000000000/Unspecified")]
        [InlineData("datetime", Date, 1, "FFFFFFFF", "ok:630821952000000000/Unspecified")]
        [InlineData("datetime", Date, 1, "7FFFFFFF", "ex:InvalidCastException")]
        [InlineData("datetime", Date, 1, "7FFFFFFE", "ex:InvalidCastException")]
        [InlineData("datetime", Date, 1, "80000000", "ex:InvalidCastException")]
        [InlineData("datetime", Date, 1, "00", "ex:NotSupportedException")]
        [InlineData("datetime", Date, 0, "2024-01-02", "ok:638397504000000000/Unspecified")]
        [InlineData("datetime", Timestamp, 0, "2024-01-02 03:04:05", "ok:638397614450000000/Unspecified")]
        [InlineData("datetime", Text, 0, "2024-01-02T03:04:05Z", "ok:638397614450000000/Utc")]
        [InlineData("datetime", Text, 0, "nope", "ex:FormatException")]
        [InlineData("datetime", Timestamp, 0, NullField, "ex:InvalidCastException")]
        public async Task ShouldReturnSameOutcomeGivenWireFieldWhenTypedGetterIsCalled(string getter, int typeOid, short format, string payload, string expected)
        {
            // Arrange
            byte[]? field = payload == NullField ? null : format == 0 ? Encoding.UTF8.GetBytes(payload) : Convert.FromHexString(payload);
            await using CassieDataReader reader = await OpenAsync([(typeOid, format)], [field]);

            // Act
            string actual = Run(reader, getter);

            // Assert
            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData("double", Float8, "0123456789.,eE+- aNIfy")]
        [InlineData("float", Float4, "0123456789.,eE+- aNIfy")]
        [InlineData("decimal", Numeric, "0123456789.-+eE ")]
        [InlineData("guid", Uuid, "0123456789abcdefABCDEF-{}() ,x")]
        public async Task ShouldMatchReferenceParserGivenGeneratedTextWhenTypedGetterIsCalled(string getter, int typeOid, string alphabet)
        {
            // Arrange
            ArgumentNullException.ThrowIfNull(alphabet);
            uint state = 12345;
            int mismatches = 0;
            string firstMismatch = string.Empty;

            // Act
            for (int round = 0; round < 1500; round++)
            {
                state = unchecked((state * 1664525) + 1013904223);
                int length = (int)((state >> 16) % (getter == "guid" ? 40u : 14u));
                StringBuilder builder = new();
                for (int index = 0; index < length; index++)
                {
                    state = unchecked((state * 1664525) + 1013904223);
                    _ = builder.Append(alphabet[(int)((state >> 16) % (uint)alphabet.Length)]);
                }

                string text = round % 3 == 0 && getter == "guid" ? "6f9619ff-8b86-d011-b42d-00c04fc964ff"[..Math.Min(36, length)] : builder.ToString();
                await using CassieDataReader reader = await OpenAsync([(typeOid, 0)], [Encoding.UTF8.GetBytes(text)]);
                string actual = Run(reader, getter);
                string expected = Reference(getter, text);
                if (actual != expected)
                {
                    mismatches++;
                    firstMismatch = firstMismatch.Length == 0 ? $"'{text}': expected {expected} but was {actual}" : firstMismatch;
                }
            }

            // Assert
            Assert.True(mismatches == 0, firstMismatch);
        }

        [Fact]
        public async Task ShouldKeepBoxedValueIntactGivenTypedGetterCalledFirstWhenValueIsReadAfterwards()
        {
            // Arrange
            byte[] double8 = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(double8, BitConverter.DoubleToInt64Bits(2.25));
            await using CassieDataReader reader = await OpenAsync(
                [(Float8, 1), (Bool, 0), (Uuid, 0), (Numeric, 0)],
                [double8, Encoding.UTF8.GetBytes("t"), Encoding.UTF8.GetBytes("6f9619ff-8b86-d011-b42d-00c04fc964ff"), Encoding.UTF8.GetBytes("9.5")]);

            // Act
            double first = reader.GetDouble(0);
            bool second = reader.GetBoolean(1);
            Guid third = reader.GetGuid(2);
            decimal fourth = reader.GetDecimal(3);

            // Assert
            Assert.Equal(2.25, first);
            Assert.True(second);
            Assert.Equal(new Guid("6f9619ff-8b86-d011-b42d-00c04fc964ff"), third);
            Assert.Equal(9.5m, fourth);
            Assert.Equal(2.25, Assert.IsType<double>(reader.GetValue(0)));
            Assert.True(Assert.IsType<bool>(reader.GetValue(1)));
            Assert.Equal(third, Assert.IsType<Guid>(reader.GetValue(2)));
            Assert.Equal("9.5", Assert.IsType<CassieNumeric>(reader.GetValue(3)).Value);
        }

        [Fact]
        public async Task ShouldRejectBackwardReadGivenSequentialAccessWhenTypedGettersAreCalled()
        {
            // Arrange
            await using CassieDataReader reader = await OpenAsync(
                [(Bool, 0), (Bool, 0)],
                [Encoding.UTF8.GetBytes("t"), Encoding.UTF8.GetBytes("f")],
                CommandBehavior.SequentialAccess);
            bool second = reader.GetBoolean(1);

            // Act
            void ReadBackward()
            {
                _ = reader.GetBoolean(0);
            }

            // Assert
            Assert.False(second);
            _ = Assert.Throws<InvalidOperationException>(ReadBackward);
        }

        [Fact]
        public async Task ShouldRejectOutOfRangeOrdinalGivenTypedGettersWhenOrdinalIsInvalid()
        {
            // Arrange
            await using CassieDataReader reader = await OpenAsync([(Bool, 0)], [Encoding.UTF8.GetBytes("t")]);

            // Act
            void Read()
            {
                _ = reader.GetDouble(3);
            }

            // Assert
            _ = Assert.Throws<IndexOutOfRangeException>(Read);
        }

        private static string Run(CassieDataReader reader, string getter)
        {
            try
            {
                return "ok:" + getter switch
                {
                    "bool" => reader.GetBoolean(0).ToString(),
                    "double" => reader.GetDouble(0).ToString("R", CultureInfo.InvariantCulture),
                    "float" => reader.GetFloat(0).ToString("R", CultureInfo.InvariantCulture),
                    "guid" => reader.GetGuid(0).ToString(),
                    "decimal" => reader.GetDecimal(0).ToString(CultureInfo.InvariantCulture),
                    "datetime" => FormatDateTime(reader.GetDateTime(0)),
                    _ => throw new ArgumentException("Unknown getter.", nameof(getter))
                };
            }
            catch (Exception exception) when (exception is not ArgumentException)
            {
                return "ex:" + exception.GetType().Name;
            }
        }

        private static string Reference(string getter, string text)
        {
            try
            {
                return "ok:" + getter switch
                {
                    "double" => double.Parse(text, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture),
                    "float" => float.Parse(text, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture),
                    "decimal" => new CassieNumeric(text).ToDecimal().ToString(CultureInfo.InvariantCulture),
                    "guid" => Guid.Parse(text).ToString(),
                    _ => throw new ArgumentException("Unknown getter.", nameof(getter))
                };
            }
            catch (Exception exception) when (exception is not ArgumentException)
            {
                return "ex:" + exception.GetType().Name;
            }
        }

        private static string FormatDateTime(DateTime value)
        {
            return value.Ticks.ToString(CultureInfo.InvariantCulture) + "/" + value.Kind;
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
