using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Types;
using System.Data;
using System.Net;
using Xunit;

namespace Cntryl.Cassie.Tests.Commands
{
    public sealed class CommandParameterContractTests
    {
        [Fact]
        public void ShouldBindSupportedArrayParametersGivenClrArrayWhenCommandIsPrepared()
        {
            // Arrange
            using CassieCommand command = new() { CommandText = "SELECT @ids" };
            Guid first = Guid.Parse("550e8400-e29b-41d4-a716-446655440001");
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "ids", Value = new Guid?[] { first, null } });

            // Act
            CassiePreparedCommand prepared = command.PrepareParameters();

            // Assert
            Assert.Equal([2951], prepared.ParameterTypeOids);
            Assert.Equal("SELECT $1", prepared.Sql);
        }

        [Fact]
        public void ShouldInferArrayTypeGivenMultidimensionalClrArrayWhenCommandIsPrepared()
        {
            // Arrange
            using CassieCommand command = new() { CommandText = "SELECT @ids" };
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "ids", Value = Array.CreateInstance(typeof(int), [1, 2]) });

            // Act
            CassiePreparedCommand prepared = command.PrepareParameters();

            // Assert
            Assert.Equal([1007], prepared.ParameterTypeOids);
        }

        [Fact]
        public void ShouldInferNumericArrayTypeGivenUInt64ValuesWhenCommandIsPrepared()
        {
            // Arrange
            using CassieCommand command = new() { CommandText = "SELECT @values" };
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "values", Value = new ulong?[] { ulong.MaxValue, null } });

            // Act
            CassiePreparedCommand prepared = command.PrepareParameters();

            // Assert
            Assert.Equal([1231], prepared.ParameterTypeOids);
        }

        [Fact]
        public void ShouldInferPostgresArrayTypesGivenUnsignedIntegerArrayParametersWhenCommandIsPrepared()
        {
            // Arrange
            using CassieCommand command = new() { CommandText = "SELECT @signedBytes, @bytes, @unsignedShorts, @unsignedLongs" };
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "signedBytes", Value = new sbyte?[] { -1, null } });
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "bytes", Value = new byte?[] { byte.MaxValue, null } });
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "unsignedShorts", Value = new ushort?[] { ushort.MaxValue, null } });
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "unsignedLongs", Value = new ulong?[] { ulong.MaxValue, null } });

            // Act
            CassiePreparedCommand prepared = command.PrepareParameters();

            // Assert
            Assert.Equal([1005, 1005, 1007, 1231], prepared.ParameterTypeOids);
        }

        [Fact]
        public void ShouldInferTimeArrayTypeGivenTimeSpanValuesWhenCommandIsPrepared()
        {
            // Arrange
            using CassieCommand command = new() { CommandText = "SELECT @values" };
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "values", Value = new TimeSpan?[] { TimeSpan.FromHours(1), null } });

            // Act
            CassiePreparedCommand prepared = command.PrepareParameters();

            // Assert
            Assert.Equal([1183], prepared.ParameterTypeOids);
        }

        [Fact]
        public void ShouldRewriteNamedMarkersGivenCommandParametersWhenSqlIsPrepared()
        {
            // Arrange
            using CassieCommand command = new() { CommandText = "SELECT @amount, '@ignored', @amount, @id -- @comment" };
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "amount", Value = 12 });
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "@id", Value = 7 });

            // Act
            CassiePreparedCommand prepared = command.PrepareParameters();

            // Assert
            Assert.Equal("SELECT $1, '@ignored', $1, $2 -- @comment", prepared.Sql);
            Assert.Equal(["amount", "id"], prepared.ParameterNames);
            Assert.Equal([12, 7], prepared.Values);
        }

        [Fact]
        public void ShouldRejectMissingParameterGivenSqlMarkerWhenCommandIsPrepared()
        {
            // Arrange
            using CassieCommand command = new() { CommandText = "SELECT @missing" };

            // Act
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(command.PrepareParameters);

            // Assert
            Assert.Contains("missing", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ShouldRejectUnusedParameterGivenExtraBindingWhenCommandIsPrepared()
        {
            // Arrange
            using CassieCommand command = new() { CommandText = "SELECT 1" };
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "unused", Value = 1 });

            // Act
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(command.PrepareParameters);

            // Assert
            Assert.Contains("unused", exception.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(ParameterDirection.Output)]
        [InlineData(ParameterDirection.InputOutput)]
        [InlineData(ParameterDirection.ReturnValue)]
        public void ShouldRejectNonInputParameterDirectionGivenBoundParameterWhenCommandIsPrepared(ParameterDirection direction)
        {
            // Arrange
            using CassieCommand command = new() { CommandText = "SELECT @value" };
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", Value = 7, Direction = direction });

            // Act
            NotSupportedException exception = Assert.Throws<NotSupportedException>(command.PrepareParameters);

            // Assert
            Assert.Contains("input-only", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ShouldValidateBindingsGivenNamedParameterWhenCommandIsPrepared()
        {
            // Arrange
            using CassieCommand command = new() { CommandText = "SELECT @value" };
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", Value = 7 });

            // Act
            command.Prepare();

            // Assert
            Assert.Equal("SELECT @value", command.CommandText);
        }

        [Fact]
        public void ShouldRejectMissingBindingGivenNamedParameterWhenCommandIsPrepared()
        {
            // Arrange
            using CassieCommand command = new() { CommandText = "SELECT @missing" };

            // Act
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(command.Prepare);

            // Assert
            Assert.Contains("missing", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ShouldPreserveDollarQuotedSqlGivenNamedParameterWhenCommandIsPrepared()
        {
            // Arrange
            using CassieCommand command = new() { CommandText = "SELECT $$ @ignored $$, @value /* @hidden /* @nested */ */" };
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", Value = 9 });

            // Act
            CassiePreparedCommand prepared = command.PrepareParameters();

            // Assert
            Assert.Equal("SELECT $$ @ignored $$, $1 /* @hidden /* @nested */ */", prepared.Sql);
            Assert.Equal(["value"], prepared.ParameterNames);
        }

        [Fact]
        public void ShouldRewriteMarkerAfterBackslashInOrdinaryStringGivenSqlWhenCommandIsPrepared()
        {
            // Arrange
            using CassieCommand command = new() { CommandText = "SELECT 'literal\\', @outside" };
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "outside", Value = 4 });

            // Act
            CassiePreparedCommand prepared = command.PrepareParameters();

            // Assert
            Assert.Equal("SELECT 'literal\\', $1", prepared.Sql);
        }

        [Fact]
        public void ShouldKeepDeclaredTypeGivenNullParameterWhenCommandIsPrepared()
        {
            // Arrange
            using CassieCommand command = new() { CommandText = "SELECT @value" };
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", DbType = DbType.Int32, Value = DBNull.Value });

            // Act
            CassiePreparedCommand prepared = command.PrepareParameters();

            // Assert
            Assert.Equal([23], prepared.ParameterTypeOids);
            Assert.Equal([null], prepared.Values);
        }

        [Fact]
        public void ShouldUseExplicitPostgresTypeOidGivenTypedParameterWhenCommandIsPrepared()
        {
            // Arrange
            using CassieCommand command = new() { CommandText = "SELECT @document" };
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "document", Value = /*lang=json,strict*/ "{\"answer\":42}", PostgresTypeOid = 3802 });

            // Act
            CassiePreparedCommand prepared = command.PrepareParameters();

            // Assert
            Assert.Equal([3802], prepared.ParameterTypeOids);
        }

        [Fact]
        public void ShouldPreserveRequestedParameterFormatsGivenTextAndBinaryBindingsWhenCommandIsPrepared()
        {
            // Arrange
            using CassieCommand command = new() { CommandText = "SELECT @text, @binary" };
            _ = command.Parameters.Add(new CassieParameter
            {
                ParameterName = "text",
                Value = new string?[] { "one", null },
                PostgresTypeOid = 1003,
                ParameterFormat = CassieParameterFormat.Text
            });
            _ = command.Parameters.Add(new CassieParameter
            {
                ParameterName = "binary",
                Value = "two",
                PostgresTypeOid = 19,
                ParameterFormat = CassieParameterFormat.Binary
            });

            // Act
            CassiePreparedCommand prepared = command.PrepareParameters();

            // Assert
            Assert.Equal([CassieParameterFormat.Text, CassieParameterFormat.Binary], prepared.ParameterFormats);
        }

        [Fact]
        public void ShouldInferRegisteredArrayOidsGivenExtendedClrArrayCarriersWhenCommandIsPrepared()
        {
            // Arrange
            using CassieCommand command = new() { CommandText = "SELECT @numeric, @interval, @lsn, @network, @bits" };
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "numeric", Value = new CassieNumeric[] { new("1.25") } });
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "interval", Value = new CassieInterval[] { new(1, 2, 3) } });
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "lsn", Value = new CassieLsn[] { new(0x0000000100000002) } });
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "network", Value = new CassieNetworkAddress[] { new(IPAddress.Loopback, 32, false) } });
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "bits", Value = new CassieBitString[] { new("101", true) } });

            // Act
            CassiePreparedCommand prepared = command.PrepareParameters();

            // Assert
            Assert.Equal([1231, 1187, 3221, 1041, 1563], prepared.ParameterTypeOids);
        }

        [Fact]
        public void ShouldRequireExplicitTypeGivenEmptyVariantArraysWhenCommandIsPrepared()
        {
            // Arrange
            using CassieCommand networkCommand = new() { CommandText = "SELECT @values" };
            _ = networkCommand.Parameters.Add(new CassieParameter { ParameterName = "values", Value = Array.Empty<CassieNetworkAddress>() });
            using CassieCommand nullNetworkCommand = new() { CommandText = "SELECT @values" };
            _ = nullNetworkCommand.Parameters.Add(new CassieParameter { ParameterName = "values", Value = new CassieNetworkAddress?[] { null } });
            using CassieCommand bitCommand = new() { CommandText = "SELECT @values" };
            _ = bitCommand.Parameters.Add(new CassieParameter { ParameterName = "values", Value = Array.Empty<CassieBitString>() });
            using CassieCommand macCommand = new() { CommandText = "SELECT @values" };
            _ = macCommand.Parameters.Add(new CassieParameter { ParameterName = "values", Value = Array.Empty<System.Net.NetworkInformation.PhysicalAddress>() });

            // Act
            NotSupportedException networkException = Assert.Throws<NotSupportedException>(networkCommand.PrepareParameters);
            NotSupportedException nullNetworkException = Assert.Throws<NotSupportedException>(nullNetworkCommand.PrepareParameters);
            NotSupportedException bitException = Assert.Throws<NotSupportedException>(bitCommand.PrepareParameters);
            NotSupportedException macException = Assert.Throws<NotSupportedException>(macCommand.PrepareParameters);

            // Assert
            Assert.Contains("PostgresTypeOid", networkException.Message, StringComparison.Ordinal);
            Assert.Contains("PostgresTypeOid", nullNetworkException.Message, StringComparison.Ordinal);
            Assert.Contains("PostgresTypeOid", bitException.Message, StringComparison.Ordinal);
            Assert.Contains("PostgresTypeOid", macException.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ShouldRejectMixedRegisteredTypesGivenVariantArraysWhenCommandIsPrepared()
        {
            // Arrange
            using CassieCommand networkCommand = new() { CommandText = "SELECT @values" };
            _ = networkCommand.Parameters.Add(new CassieParameter
            {
                ParameterName = "values",
                Value = new CassieNetworkAddress[]
                {
                    new(IPAddress.Loopback, 32, false),
                    new(IPAddress.Parse("192.0.2.0"), 24, true)
                }
            });
            using CassieCommand bitCommand = new() { CommandText = "SELECT @values" };
            _ = bitCommand.Parameters.Add(new CassieParameter
            {
                ParameterName = "values",
                Value = new CassieBitString[] { new("101", false), new("010", true) }
            });
            using CassieCommand macCommand = new() { CommandText = "SELECT @values" };
            _ = macCommand.Parameters.Add(new CassieParameter
            {
                ParameterName = "values",
                Value = new System.Net.NetworkInformation.PhysicalAddress[]
                {
                    new([0, 1, 2, 3, 4, 5]),
                    new([0, 1, 2, 3, 4, 5, 6, 7])
                }
            });

            // Act
            ArgumentException networkException = Assert.Throws<ArgumentException>(networkCommand.PrepareParameters);
            ArgumentException bitException = Assert.Throws<ArgumentException>(bitCommand.PrepareParameters);
            ArgumentException macException = Assert.Throws<ArgumentException>(macCommand.PrepareParameters);

            // Assert
            Assert.Contains("cannot mix", networkException.Message, StringComparison.Ordinal);
            Assert.Contains("cannot mix", bitException.Message, StringComparison.Ordinal);
            Assert.Contains("cannot mix", macException.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ShouldUseExplicitTypeGivenEmptyVariantArrayWhenCommandIsPrepared()
        {
            // Arrange
            using CassieCommand command = new() { CommandText = "SELECT @values" };
            _ = command.Parameters.Add(new CassieParameter
            {
                ParameterName = "values",
                Value = Array.Empty<CassieNetworkAddress>(),
                PostgresTypeOid = 651
            });

            // Act
            CassiePreparedCommand prepared = command.PrepareParameters();

            // Assert
            Assert.Equal([651], prepared.ParameterTypeOids);
        }

        [Fact]
        public void ShouldRejectXmlDbTypeGivenUnsupportedCassieTypeWhenCommandIsPrepared()
        {
            // Arrange
            using CassieCommand command = new() { CommandText = "SELECT @value" };
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", DbType = DbType.Xml, Value = "<root />" });

            // Act
            NotSupportedException exception = Assert.Throws<NotSupportedException>(command.PrepareParameters);

            // Assert
            Assert.Contains("XML", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ShouldPreferArrayElementIdentityOverScalarDbTypeGivenArrayParameterWhenCommandIsPrepared()
        {
            // Arrange
            using CassieCommand command = new() { CommandText = "SELECT @integers, @decimals, @bytes, @byteArrays" };
            int[] integers = [1, 2];
            decimal?[] decimals = [1.25m, null];
            byte[] bytes = [1, 2];
            byte[]?[] byteArrays = [[3], null];
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "integers", DbType = DbType.Int32, Value = integers });
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "decimals", DbType = DbType.Decimal, Value = decimals });
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "bytes", DbType = DbType.Binary, Value = bytes });
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "byteArrays", Value = byteArrays });

            // Act
            CassiePreparedCommand prepared = command.PrepareParameters();

            // Assert
            Assert.Equal([1007, 1231, 17, 1001], prepared.ParameterTypeOids);
        }
    }
}
