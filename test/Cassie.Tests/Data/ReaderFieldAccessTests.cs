using Cntryl.Cassie.Data;
using System.Buffers.Binary;
using System.Data;
using System.Text;
using Xunit;

namespace Cntryl.Cassie.Tests.Data
{
    public sealed class ReaderFieldAccessTests
    {
        private const int Int8 = 20;
        private const int Int2 = 21;
        private const int Int4 = 23;
        private const int Text = 25;

        [Theory]
        [InlineData(Int4, "42", 42L)]
        [InlineData(Int4, "-2147483648", -2147483648L)]
        [InlineData(Int4, "+42", 42L)]
        [InlineData(Int4, " 42 ", 42L)]
        [InlineData(Int2, "-7", -7L)]
        [InlineData(Int8, "5", 5L)]
        [InlineData(Text, "17", 17L)]
        public async Task ShouldReturnSameIntegerGivenTextColumnWhenEachIntegerGetterIsCalled(int typeOid, string wireText, long expected)
        {
            // Arrange
            await using CassieDataReader reader = await OpenAsync([(typeOid, 0)], [Encoding.UTF8.GetBytes(wireText)]);

            // Act
            long asInt64 = reader.GetInt64(0);
            int asInt32 = reader.GetInt32(0);
            long boxed = Convert.ToInt64(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture);

            // Assert
            Assert.Equal(expected, asInt64);
            Assert.Equal(expected, asInt32);
            Assert.Equal(expected, boxed);
        }

        [Fact]
        public async Task ShouldReturnBigEndianValuesGivenBinaryColumnsWhenIntegerGettersAreCalled()
        {
            // Arrange
            byte[] int2 = new byte[2];
            byte[] int4 = new byte[4];
            byte[] int8 = new byte[8];
            BinaryPrimitives.WriteInt16BigEndian(int2, -3);
            BinaryPrimitives.WriteInt32BigEndian(int4, 70000);
            BinaryPrimitives.WriteInt64BigEndian(int8, 9_999_999_999);
            await using CassieDataReader reader = await OpenAsync([(Int2, 1), (Int4, 1), (Int8, 1)], [int2, int4, int8]);

            // Act
            short first = reader.GetInt16(0);
            int second = reader.GetInt32(1);
            long third = reader.GetInt64(2);
            long widened = reader.GetInt64(1);

            // Assert
            Assert.Equal(-3, first);
            Assert.Equal(70000, second);
            Assert.Equal(9_999_999_999, third);
            Assert.Equal(70000, widened);
            Assert.Equal(70000, Assert.IsType<int>(reader.GetValue(1)));
        }

        [Theory]
        [InlineData(Int8, "9999999999", "int32")]
        [InlineData(Int4, "70000", "int16")]
        [InlineData(Int8, "70000", "int16")]
        public async Task ShouldReportOverflowGivenValueOutsideRequestedRangeWhenNarrowerGetterIsCalled(int typeOid, string wireText, string getter)
        {
            // Arrange
            await using CassieDataReader reader = await OpenAsync([(typeOid, 0)], [Encoding.UTF8.GetBytes(wireText)]);

            // Act
            void Read()
            {
                _ = getter == "int32" ? reader.GetInt32(0) : reader.GetInt16(0);
            }

            // Assert
            _ = Assert.Throws<OverflowException>(Read);
        }

        [Fact]
        public async Task ShouldReportFormatErrorGivenNonNumericTextWhenIntegerGetterIsCalled()
        {
            // Arrange
            await using CassieDataReader reader = await OpenAsync([(Int4, 0)], [Encoding.UTF8.GetBytes("4x")]);

            // Act
            void Read()
            {
                _ = reader.GetInt32(0);
            }

            // Assert
            _ = Assert.Throws<FormatException>(Read);
        }

        [Fact]
        public async Task ShouldReportNullGivenNullFieldWhenIntegerGetterIsCalled()
        {
            // Arrange
            await using CassieDataReader reader = await OpenAsync([(Int4, 0), (Text, 0)], [null, Encoding.UTF8.GetBytes("kept")]);

            // Act
            bool isNull = await reader.IsDBNullAsync(0, TestContext.Current.CancellationToken);
            void Read()
            {
                _ = reader.GetInt32(0);
            }

            // Assert
            Assert.True(isNull);
            _ = Assert.Throws<InvalidCastException>(Read);
            Assert.Same(DBNull.Value, reader.GetValue(0));
            Assert.False(await reader.IsDBNullAsync(1, TestContext.Current.CancellationToken));
            Assert.Equal("kept", reader.GetString(1));
        }

        [Fact]
        public async Task ShouldReturnSameInstanceGivenTextColumnWhenValueIsReadTwice()
        {
            // Arrange
            await using CassieDataReader reader = await OpenAsync([(Text, 0)], [Encoding.UTF8.GetBytes("héllo")]);

            // Act
            string first = reader.GetString(0);
            object second = reader.GetValue(0);

            // Assert
            Assert.Equal("héllo", first);
            Assert.Same(first, second);
        }

        [Fact]
        public async Task ShouldRejectBackwardReadGivenSequentialAccessWhenIntegerGetterIsCalled()
        {
            // Arrange
            await using CassieDataReader reader = await OpenAsync([(Int4, 0), (Int4, 0)], [Encoding.UTF8.GetBytes("1"), Encoding.UTF8.GetBytes("2")], CommandBehavior.SequentialAccess);
            int second = reader.GetInt32(1);

            // Act
            void ReadBackward()
            {
                _ = reader.GetInt32(0);
            }

            Task CheckNullBackwardAsync()
            {
                return reader.IsDBNullAsync(0, TestContext.Current.CancellationToken);
            }

            // Assert
            Assert.Equal(2, second);
            _ = Assert.Throws<InvalidOperationException>(ReadBackward);
            _ = await Assert.ThrowsAsync<InvalidOperationException>(CheckNullBackwardAsync);
        }

        [Fact]
        public async Task ShouldRejectFieldAccessGivenRowsExhaustedWhenGettersAreCalled()
        {
            // Arrange
            await using CassieDataReader reader = await OpenAsync([(Int4, 0)], [Encoding.UTF8.GetBytes("1")]);
            bool more = await reader.ReadAsync(TestContext.Current.CancellationToken);

            // Act
            void ReadInteger()
            {
                _ = reader.GetInt32(0);
            }

            Task CheckNullAsync()
            {
                return reader.IsDBNullAsync(0, TestContext.Current.CancellationToken);
            }

            void ReadValue()
            {
                _ = reader.GetValue(0);
            }

            // Assert
            Assert.False(more);
            _ = Assert.Throws<InvalidOperationException>(ReadInteger);
            _ = await Assert.ThrowsAsync<InvalidOperationException>(CheckNullAsync);
            _ = Assert.Throws<InvalidOperationException>(ReadValue);
        }

        [Fact]
        public async Task ShouldKeepRowsIndependentGivenConsecutiveRowsWhenValuesAreReadAfterAdvancing()
        {
            // Arrange
            FakeReaderSession session = new();
            session.Enqueue((byte)'T', RowDescription([(Int4, 0), (Text, 0)]));
            session.Enqueue((byte)'D', DataRow([Encoding.UTF8.GetBytes("1"), Encoding.UTF8.GetBytes("first")]));
            session.Enqueue((byte)'D', DataRow([Encoding.UTF8.GetBytes("2"), null]));
            session.Enqueue((byte)'C', Encoding.UTF8.GetBytes("SELECT 2\0"));
            session.Enqueue((byte)'Z', [(byte)'I']);
            await using CassieDataReader reader = new(session, commandTimeout: 0, CommandBehavior.Default);
            await reader.InitializeAsync(TestContext.Current.CancellationToken);

            // Act
            _ = await reader.ReadAsync(TestContext.Current.CancellationToken);
            string firstText = reader.GetString(1);
            object firstNumber = reader.GetValue(0);
            _ = await reader.ReadAsync(TestContext.Current.CancellationToken);
            int secondNumber = reader.GetInt32(0);
            object secondBoxed = reader.GetValue(0);
            bool secondTextIsNull = await reader.IsDBNullAsync(1, TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal("first", firstText);
            Assert.Equal(1, firstNumber);
            Assert.Equal(2, secondNumber);
            Assert.Equal(2, secondBoxed);
            Assert.True(secondTextIsNull);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(2)]
        [InlineData(int.MaxValue)]
        public async Task ShouldReportIndexOutOfRangeGivenOrdinalOutsideResultWhenColumnIsAccessed(int ordinal)
        {
            // Arrange
            await using CassieDataReader reader = await OpenAsync([(Int4, 0), (Text, 0)], [Encoding.UTF8.GetBytes("1"), Encoding.UTF8.GetBytes("a")]);

            // Act
            Action[] accesses =
            [
                () => _ = reader.GetValue(ordinal),
                () => _ = reader[ordinal],
                () => _ = reader.GetInt32(ordinal),
                () => _ = reader.GetString(ordinal),
                () => _ = reader.GetName(ordinal),
                () => _ = reader.GetFieldType(ordinal),
                () => _ = reader.GetDataTypeName(ordinal),
                () => _ = reader.GetFieldValue<int>(ordinal)
            ];
            Task<bool> nullCheck = reader.IsDBNullAsync(ordinal, TestContext.Current.CancellationToken);

            // Assert
            Assert.All(accesses, static access => Assert.Throws<IndexOutOfRangeException>(access));
            _ = await Assert.ThrowsAsync<IndexOutOfRangeException>(() => nullCheck);
            Assert.Equal(1, reader.GetInt32(0));
        }

        [Fact]
        public async Task ShouldReportIndexOutOfRangeGivenUnknownColumnNameWhenColumnIsLookedUp()
        {
            // Arrange
            await using CassieDataReader reader = await OpenAsync([(Int4, 0), (Text, 0)], [Encoding.UTF8.GetBytes("1"), Encoding.UTF8.GetBytes("a")]);

            // Act
            IndexOutOfRangeException fromOrdinal = Assert.Throws<IndexOutOfRangeException>(() => reader.GetOrdinal("missing"));
            IndexOutOfRangeException fromIndexer = Assert.Throws<IndexOutOfRangeException>(() => reader["missing"]);

            // Assert
            Assert.Contains("missing", fromOrdinal.Message, StringComparison.Ordinal);
            Assert.Contains("missing", fromIndexer.Message, StringComparison.Ordinal);
            Assert.Equal(1, reader.GetOrdinal("C1"));
            Assert.Equal(1, reader["c0"]);
        }

        [Fact]
        public async Task ShouldReturnNoSchemaTableGivenCommandWithoutResultSetWhenSchemaTableIsRequested()
        {
            // Arrange
            FakeReaderSession session = new();
            session.Enqueue((byte)'C', Encoding.UTF8.GetBytes("INSERT 0 1\0"));
            session.Enqueue((byte)'Z', [(byte)'I']);
            await using CassieDataReader reader = new(session, commandTimeout: 0, CommandBehavior.Default);
            await reader.InitializeAsync(TestContext.Current.CancellationToken);

            // Act
            DataTable? schema = await reader.GetSchemaTableAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(0, reader.FieldCount);
            Assert.Null(schema);
        }

        [Fact]
        public async Task ShouldDescribeColumnsThenRejectGivenReaderClosedWhenSchemaTableIsRequested()
        {
            // Arrange
            CassieDataReader reader = await OpenAsync([(Int4, 0), (Text, 0)], [Encoding.UTF8.GetBytes("1"), Encoding.UTF8.GetBytes("a")]);

            // Act
            DataTable? open = await reader.GetSchemaTableAsync(TestContext.Current.CancellationToken);
            await reader.DisposeAsync();
            Task<DataTable?> closed = reader.GetSchemaTableAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(2, Assert.IsType<DataTable>(open).Rows.Count);
            _ = await Assert.ThrowsAsync<InvalidOperationException>(() => closed);
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
