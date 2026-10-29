using System.Data;
using System.Data.Common;
using System.Net;
using Xunit;

namespace Cntryl.Cassie.Tests.Live
{
    public sealed class LiveArrayContractTests(LiveServerFixture server) : LiveTestBase(server)
    {
        private static readonly byte[] Blob = [0, 1, 255];
        private static readonly decimal[] Decimals = [1.25m, 2.5m];

        private static void AssertArray(object? candidate, int elementTypeOid, params object?[] expected)
        {
            CassieArray array = Assert.IsType<CassieArray>(candidate);
            Assert.Equal(elementTypeOid, array.ElementTypeOid);
            Assert.Equal(expected.Length, array.Count);
            for (int index = 0; index < expected.Length; index++)
            {
                Assert.Equal(expected[index], array[index]);
            }
        }

        [Fact]
        public async Task ShouldReturnNetworkAddressesGivenTextFormatParameterWhenArraysAreEnabled()
        {
            // Arrange
            LiveFeature.Require("ARRAYS");
            using CassieCommand command = CreateCommand("SELECT @addresses AS values", binaryResults: true);
            CassieNetworkAddress[] addresses =
            [
                new(IPAddress.Parse("192.0.2.9"), 24, false),
                new(IPAddress.Parse("192.0.2.10"), 32, false)
            ];
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "addresses", Value = addresses, ParameterFormat = CassieParameterFormat.Text });

            // Act
            await using DbDataReader reader = await ReadFirstRowAsync(command);

            // Assert
            CassieArray values = Assert.IsType<CassieArray>(reader.GetValue(0));
            Assert.Equal(869, values.ElementTypeOid);
            Assert.Equal(1, values.Rank);
            Assert.Equal(2, values.Count);
            Assert.Equal("192.0.2.9/24", Assert.IsType<CassieNetworkAddress>(values[0]).ToString());
            Assert.Equal("192.0.2.10/32", Assert.IsType<CassieNetworkAddress>(values[1]).ToString());
        }

        [Fact]
        public async Task ShouldReturnShapeAndBoundsGivenInt32ArrayLiteralWhenResultIsBinary()
        {
            // Arrange
            LiveFeature.Require("ARRAYS");
            using CassieCommand command = CreateCommand("SELECT ARRAY[7,NULL,9]::int4[] AS values", binaryResults: true);

            // Act
            await using DbDataReader reader = await ReadFirstRowAsync(command);

            // Assert
            CassieArray values = Assert.IsType<CassieArray>(reader.GetValue(0));
            Assert.Equal(1, values.Rank);
            Assert.Equal(3, values.Count);
            Assert.Equal(3, values.Dimensions[0]);
            Assert.Equal(1, values.LowerBounds[0]);
            Assert.Equal(7, values[0]);
            Assert.Null(values[1]);
            Assert.Equal(9, values[2]);
        }

        [Fact]
        public async Task ShouldReturnExactValuesGivenNumericArrayLiteralWhenResultIsBinary()
        {
            // Arrange
            LiveFeature.Require("ARRAYS");
            using CassieCommand command = CreateCommand("SELECT ARRAY[1.25,NULL,-2.5]::numeric[] AS values", binaryResults: true);

            // Act
            await using DbDataReader reader = await ReadFirstRowAsync(command);

            // Assert
            AssertArray(reader.GetValue(0), 1700, new CassieNumeric("1.25"), null, new CassieNumeric("-2.5"));
        }

        [Fact]
        public async Task ShouldRoundTripElementsGivenNullableClrArraysWhenParametersAreBound()
        {
            // Arrange
            LiveFeature.Require("ARRAYS");
            Guid id = Guid.Parse("550e8400-e29b-41d4-a716-446655440001");
            using CassieCommand command = CreateCommand(
                "SELECT @numbers::numeric[] AS numbers, @ids::uuid[] AS ids, @flags::bool[] AS flags, @blobs::bytea[] AS blobs, @labels::text[] AS labels",
                binaryResults: true);
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "numbers", DbType = DbType.Decimal, Value = new decimal?[] { 1.25m, null, -2.5m } });
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "ids", DbType = DbType.Guid, Value = new Guid?[] { id, null } });
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "flags", DbType = DbType.Boolean, Value = new bool?[] { true, null, false } });
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "blobs", DbType = DbType.Binary, Value = new byte[]?[] { Blob, null } });
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "labels", DbType = DbType.String, Value = new string?[] { "alpha", null, "beta" } });

            // Act
            await using DbDataReader reader = await ReadFirstRowAsync(command);

            // Assert
            AssertArray(reader.GetValue(0), 1700, new CassieNumeric("1.25"), null, new CassieNumeric("-2.5"));
            AssertArray(reader.GetValue(1), 2950, id, null);
            AssertArray(reader.GetValue(2), 16, true, null, false);
            AssertArray(reader.GetValue(3), 17, Blob, null);
            AssertArray(reader.GetValue(4), 25, "alpha", null, "beta");
        }

        [Fact]
        public async Task ShouldRoundTripExactValuesGivenDecimalArrayParameterWhenBoundAsNumericArray()
        {
            // Arrange
            LiveFeature.Require("ARRAYS");
            using CassieCommand command = CreateCommand("SELECT @values::numeric[] AS values", binaryResults: true);
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "values", Value = Decimals });

            // Act
            await using DbDataReader reader = await ReadFirstRowAsync(command);

            // Assert
            AssertArray(reader.GetValue(0), 1700, new CassieNumeric("1.25"), new CassieNumeric("2.5"));
        }

        [Fact]
        public async Task ShouldRoundTripElementsGivenUInt64AndTimeSpanArraysWhenParametersAreBound()
        {
            // Arrange
            LiveFeature.Require("ARRAYS");
            TimeSpan time = new TimeSpan(1, 2, 3).Add(TimeSpan.FromTicks(4_567_890));
            using CassieCommand command = CreateCommand("SELECT @numbers::numeric[] AS numbers, @times::time[] AS times", binaryResults: true);
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "numbers", Value = new ulong?[] { ulong.MaxValue, null } });
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "times", Value = new TimeSpan?[] { time, null } });

            // Act
            await using DbDataReader reader = await ReadFirstRowAsync(command);

            // Assert
            AssertArray(reader.GetValue(0), 1700, new CassieNumeric("18446744073709551615"), null);
            AssertArray(reader.GetValue(1), 1083, TimeOnly.FromTimeSpan(time), null);
        }

        [Fact]
        public async Task ShouldReturnShapeGivenMultidimensionalCassieArrayWhenBoundAsTextParameter()
        {
            // Arrange
            LiveFeature.Require("ARRAY_SHAPES");
            CassieArray source = new(23, [2, 2], [0, 4], [1, 2, 3, 4]);
            using CassieCommand command = CreateCommand("SELECT @values::int[] AS values", binaryResults: true);
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "values", Value = source, ParameterFormat = CassieParameterFormat.Text });

            // Act
            await using DbDataReader reader = await ReadFirstRowAsync(command);

            // Assert
            CassieArray value = Assert.IsType<CassieArray>(reader.GetValue(0));
            Assert.Equal([2, 2], value.Dimensions);
            Assert.Equal([0, 4], value.LowerBounds);
            Assert.Equal([1, 2, 3, 4], value.Select(item => (int)item!));
        }

        [Fact]
        public async Task ShouldPreserveDimensionsAndBoundsGivenCassieArrayParameterWhenBoundAsBinary()
        {
            // Arrange
            LiveFeature.Require("ARRAY_SHAPES");
            using CassieCommand command = CreateCommand("SELECT @values::int[]::text AS values");
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "values", Value = new CassieArray(23, [2, 2], [0, 4], [1, 2, 3, 4]) });

            // Act
            object? text = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal("[0:1][4:5]={{1,2},{3,4}}", text);
        }

        [Fact]
        public async Task ShouldPreserveDimensionsAndBoundsGivenMultidimensionalClrArrayWhenBoundAsBinary()
        {
            // Arrange
            LiveFeature.Require("ARRAY_SHAPES");
            Array clrArray = Array.CreateInstance(typeof(int), [2, 2], [0, 4]);
            clrArray.SetValue(5, 0, 4);
            clrArray.SetValue(6, 0, 5);
            clrArray.SetValue(7, 1, 4);
            clrArray.SetValue(8, 1, 5);
            using CassieCommand command = CreateCommand("SELECT @values::int[]::text AS values");
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "values", Value = clrArray });

            // Act
            object? text = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal("[1:2][4:5]={{5,6},{7,8}}", text);
        }

        [Fact]
        public async Task ShouldRoundTripNameValuesGivenBinaryParametersWhenServerSupportsNameType()
        {
            // Arrange
            LiveFeature.Require("NAME_TYPE");
            using CassieCommand command = CreateCommand("SELECT @value::name AS name, @values::name[] AS names", binaryResults: true);
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", Value = "catalog_name", PostgresTypeOid = 19 });
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "values", Value = new string?[] { "alpha", null, "βeta", "b,eta", "say \"hi\"", "slash\\name" }, PostgresTypeOid = 1003 });

            // Act
            await using DbDataReader reader = await ReadFirstRowAsync(command);

            // Assert
            Assert.Equal("catalog_name", reader.GetString(0));
            CassieArray names = Assert.IsType<CassieArray>(reader.GetValue(1));
            Assert.Equal(19, names.ElementTypeOid);
            Assert.Equal(1, names.Rank);
            AssertArray(names, 19, "alpha", null, "βeta", "b,eta", "say \"hi\"", "slash\\name");
        }

        [Fact]
        public async Task ShouldEscapeElementsGivenTextFormatNameArrayWhenServerSupportsNameType()
        {
            // Arrange
            LiveFeature.Require("NAME_TYPE");
            using CassieCommand command = CreateCommand("SELECT @values::name[] AS names");
            _ = command.Parameters.Add(new CassieParameter
            {
                ParameterName = "values",
                Value = new string?[] { "alpha", "b,eta", "say \"hi\"", "slash\\name", null },
                PostgresTypeOid = 1003,
                ParameterFormat = CassieParameterFormat.Text
            });

            // Act
            await using DbDataReader reader = await ReadFirstRowAsync(command);

            // Assert
            Assert.Equal("[\"alpha\",\"b,eta\",\"say \\\"hi\\\"\",\"slash\\\\name\",null]", reader.GetString(0));
        }

        [Fact]
        public async Task ShouldTruncateToSixtyThreeCharactersGivenLongTextFormatNameWhenServerSupportsNameType()
        {
            // Arrange
            LiveFeature.Require("NAME_TYPE");
            using CassieCommand command = CreateCommand("SELECT @value::name AS name");
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", Value = new string('x', 70), PostgresTypeOid = 19, ParameterFormat = CassieParameterFormat.Text });

            // Act
            await using DbDataReader reader = await ReadFirstRowAsync(command);

            // Assert
            Assert.Equal(new string('x', 63), reader.GetString(0));
        }
    }
}
