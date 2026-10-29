using System.Data.Common;
using System.Globalization;
using System.Text;
using Xunit;

namespace Cntryl.Cassie.Tests
{
    public sealed class ValueConversionContractTests
    {
        [Theory]
        [InlineData("08006", true)]
        [InlineData("08P01", true)]
        [InlineData("40001", true)]
        [InlineData("40P01", true)]
        [InlineData("53300", true)]
        [InlineData("57P01", true)]
        [InlineData("23505", false)]
        [InlineData("42601", false)]
        [InlineData(null, false)]
        public void ShouldClassifyTransienceGivenSqlStateWhenExceptionIsRead(string? sqlState, bool expected)
        {
            // Arrange
            CassieException exception = new("failure", sqlState);

            // Act
            bool transient = exception.IsTransient;

            // Assert
            Assert.Equal(expected, transient);
        }

        [Fact]
        public void ShouldConvertToCommonTypesGivenNumericWhenConvertChangeTypeRuns()
        {
            // Arrange
            CassieNumeric numeric = new("12.50");

            // Act
            decimal asDecimal = Convert.ToDecimal(numeric, CultureInfo.InvariantCulture);
            double asDouble = Convert.ToDouble(numeric, CultureInfo.InvariantCulture);
            object changed = Convert.ChangeType(new CassieNumeric("42"), typeof(int), CultureInfo.InvariantCulture);
            decimal explicitValue = (decimal)numeric;

            // Assert
            Assert.Equal(12.50m, asDecimal);
            Assert.Equal(12.5, asDouble);
            Assert.Equal(42, changed);
            Assert.Equal(12.50m, explicitValue);
        }

        [Fact]
        public async Task ShouldReuseConvertedValueGivenRepeatedGetValueWhenRowIsCurrent()
        {
            // Arrange
            using ScriptedServer server = new(session => session.ServeQueriesAsync(async _ =>
            {
                await session.WriteIntRowDescriptionAsync();
                await session.WriteTextRowAsync("42");
                await session.WriteTextRowAsync("43");
                await session.WriteCommandCompleteAsync("SELECT 2");
            }));
            await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using CassieCommand command = new() { Connection = connection, CommandText = "SELECT 1" };
            await using DbDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            _ = await reader.ReadAsync(TestContext.Current.CancellationToken);

            // Act
            object first = reader.GetValue(0);
            object second = reader.GetValue(0);
            _ = await reader.ReadAsync(TestContext.Current.CancellationToken);
            object next = reader.GetValue(0);

            // Assert
            Assert.Same(first, second);
            Assert.Equal(42, first);
            Assert.Equal(43, next);
        }

        [Fact]
        public async Task ShouldWidenIntegersGivenInt4ColumnWhenTypedGettersAreCalled()
        {
            // Arrange
            using ScriptedServer server = new(session => session.ServeQueriesAsync(async _ =>
            {
                await session.WriteIntRowDescriptionAsync();
                await session.WriteTextRowAsync("42");
                await session.WriteCommandCompleteAsync("SELECT 1");
            }));
            await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using CassieCommand command = new() { Connection = connection, CommandText = "SELECT 1" };
            await using DbDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            _ = await reader.ReadAsync(TestContext.Current.CancellationToken);

            // Act
            (int asInt, long asLong, double asDouble, decimal asDecimal) = (reader.GetInt32(0), reader.GetInt64(0), reader.GetDouble(0), reader.GetDecimal(0));

            // Assert
            Assert.Equal((42, 42L, 42.0, 42m), (asInt, asLong, asDouble, asDecimal));
        }

        [Fact]
        public async Task ShouldSendPostgresLiteralGivenNonFiniteDoubleArrayWhenTextFormatIsRequested()
        {
            // Arrange
            string? sentText = null;
            using ScriptedServer server = new(async session =>
            {
                while (true)
                {
                    (byte tag, byte[] payload) = await session.ReadFrameAsync();
                    if (tag == (byte)'B')
                    {
                        sentText = ReadFirstTextParameter(payload);
                    }
                    else if (tag == (byte)'H')
                    {
                        await session.WriteAsync((byte)'1', []);
                        await session.WriteAsync((byte)'2', []);
                        await session.WriteCommandCompleteAsync("SELECT 0");
                    }
                    else if (tag == (byte)'S')
                    {
                        await session.WriteReadyAsync();
                    }
                    else if (tag == (byte)'X')
                    {
                        return;
                    }
                }
            });
            await using CassieConnection connection = new() { ConnectionString = server.ConnectionString("Pooling=false") };
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using CassieCommand command = new() { Connection = connection, CommandText = "SELECT @values" };
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "values", Value = new[] { double.NaN, 1.5, double.PositiveInfinity }, ParameterFormat = CassieParameterFormat.Text });

            // Act
            _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal("{NaN,1.5,Infinity}", sentText);
        }

        private static string ReadFirstTextParameter(byte[] bind)
        {
            int offset = 2;
            short formatCount = System.Buffers.Binary.BinaryPrimitives.ReadInt16BigEndian(bind.AsSpan(offset));
            offset += 2 + (formatCount * 2);
            offset += 2;
            int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bind.AsSpan(offset));
            return Encoding.UTF8.GetString(bind, offset + 4, length);
        }
    }
}
