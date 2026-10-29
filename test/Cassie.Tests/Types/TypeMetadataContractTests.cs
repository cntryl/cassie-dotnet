using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Types;
using System.Data;
using System.Net.NetworkInformation;
using Xunit;

namespace Cntryl.Cassie.Tests.Types
{
    public sealed class TypeMetadataContractTests
    {
        [Theory]
        [InlineData(16, 1000, "bool")]
        [InlineData(17, 1001, "bytea")]
        [InlineData(18, 1002, "char")]
        [InlineData(19, 1003, "name")]
        [InlineData(20, 1016, "int8")]
        [InlineData(21, 1005, "int2")]
        [InlineData(23, 1007, "int4")]
        [InlineData(25, 1009, "text")]
        [InlineData(26, 1028, "oid")]
        [InlineData(114, 199, "json")]
        [InlineData(650, 651, "cidr")]
        [InlineData(700, 1021, "float4")]
        [InlineData(701, 1022, "float8")]
        [InlineData(774, 775, "macaddr8")]
        [InlineData(829, 1040, "macaddr")]
        [InlineData(869, 1041, "inet")]
        [InlineData(1042, 1014, "bpchar")]
        [InlineData(1043, 1015, "varchar")]
        [InlineData(1082, 1182, "date")]
        [InlineData(1083, 1183, "time")]
        [InlineData(1114, 1115, "timestamp")]
        [InlineData(1184, 1185, "timestamptz")]
        [InlineData(1186, 1187, "interval")]
        [InlineData(1266, 1270, "timetz")]
        [InlineData(1560, 1561, "bit")]
        [InlineData(1562, 1563, "varbit")]
        [InlineData(1700, 1231, "numeric")]
        [InlineData(2950, 2951, "uuid")]
        [InlineData(3220, 3221, "pg_lsn")]
        [InlineData(3802, 3807, "jsonb")]
        public void ShouldDescribeBothDirectionsGivenBuiltInPairWhenMetadataIsRead(int scalarOid, int arrayOid, string name)
        {
            // Arrange
            CassieArray empty = new(scalarOid, [], [], []);

            // Act
            int inferredArrayOid = CassieTypeMap.GetArrayParameterOid(empty);
            int? elementOid = CassieTypeMap.GetArrayElementOid(arrayOid);

            // Assert
            Assert.Equal(arrayOid, inferredArrayOid);
            Assert.Equal(scalarOid, elementOid);
            Assert.True(CassieTypeMap.IsArrayOid(arrayOid));
            Assert.Equal(name, CassieTypeMap.GetDataTypeName(scalarOid));
            Assert.Equal(name + "[]", CassieTypeMap.GetDataTypeName(arrayOid));
            Assert.Equal(typeof(string), CassieResultCodec.GetClrType(arrayOid, 0));
            Assert.Equal(typeof(CassieArray), CassieResultCodec.GetClrType(arrayOid, 1));
        }

        [Theory]
        [InlineData(16, typeof(bool), typeof(bool))]
        [InlineData(17, typeof(byte[]), typeof(byte[]))]
        [InlineData(20, typeof(long), typeof(long))]
        [InlineData(21, typeof(short), typeof(short))]
        [InlineData(23, typeof(int), typeof(int))]
        [InlineData(26, typeof(uint), typeof(uint))]
        [InlineData(700, typeof(float), typeof(float))]
        [InlineData(701, typeof(double), typeof(double))]
        [InlineData(1700, typeof(CassieNumeric), typeof(CassieNumeric))]
        [InlineData(2950, typeof(Guid), typeof(Guid))]
        [InlineData(1082, typeof(DateOnly), typeof(DateOnly))]
        [InlineData(1083, typeof(TimeOnly), typeof(TimeOnly))]
        [InlineData(1114, typeof(DateTime), typeof(DateTime))]
        [InlineData(1184, typeof(DateTimeOffset), typeof(DateTimeOffset))]
        [InlineData(1266, typeof(CassieTimeWithTimeZone), typeof(CassieTimeWithTimeZone))]
        [InlineData(650, typeof(string), typeof(CassieNetworkAddress))]
        [InlineData(869, typeof(string), typeof(CassieNetworkAddress))]
        [InlineData(774, typeof(string), typeof(PhysicalAddress))]
        [InlineData(829, typeof(string), typeof(PhysicalAddress))]
        [InlineData(1186, typeof(string), typeof(CassieInterval))]
        [InlineData(1560, typeof(string), typeof(CassieBitString))]
        [InlineData(1562, typeof(string), typeof(CassieBitString))]
        [InlineData(3220, typeof(string), typeof(CassieLsn))]
        [InlineData(24, typeof(string), typeof(uint))]
        [InlineData(2202, typeof(string), typeof(uint))]
        [InlineData(2203, typeof(string), typeof(uint))]
        [InlineData(2204, typeof(string), typeof(uint))]
        [InlineData(2205, typeof(string), typeof(uint))]
        [InlineData(2206, typeof(string), typeof(uint))]
        [InlineData(3734, typeof(string), typeof(uint))]
        [InlineData(3769, typeof(string), typeof(uint))]
        [InlineData(4089, typeof(string), typeof(uint))]
        [InlineData(4096, typeof(string), typeof(uint))]
        [InlineData(4191, typeof(string), typeof(uint))]
        public void ShouldPreserveFormatSpecificFieldTypesGivenBuiltInOidWhenMetadataIsRead(int oid, Type textType, Type binaryType)
        {
            // Arrange
            const short textFormat = 0;
            const short binaryFormat = 1;

            // Act
            Type textFieldType = CassieResultCodec.GetClrType(oid, textFormat);
            Type binaryFieldType = CassieResultCodec.GetClrType(oid, binaryFormat);

            // Assert
            Assert.Equal(textType, textFieldType);
            Assert.Equal(binaryType, binaryFieldType);
        }

        [Theory]
        [InlineData(705, "unknown")]
        [InlineData(2249, "record")]
        [InlineData(2287, "record[]")]
        [InlineData(99999, "99999")]
        public void ShouldRetainTextOnlyMetadataGivenUnsupportedBinaryTypeWhenCodecIsUsed(int oid, string name)
        {
            // Arrange
            const string text = "(original,text)";

            // Act
            object decoded = CassieResultCodec.ConvertValue(text, oid);

            // Assert
            Assert.Equal(text, decoded);
            Assert.Equal(name, CassieTypeMap.GetDataTypeName(oid));
            Assert.Equal(typeof(string), CassieResultCodec.GetClrType(oid, 1));
            Assert.Null(CassieTypeMap.GetArrayElementOid(oid));
            Assert.Null(CassieParameterCodec.GetBinaryParameter(text, oid));
            _ = Assert.Throws<NotSupportedException>(() => CassieResultCodec.ConvertBinaryValue([], oid));
        }

        [Theory]
        [InlineData(16, true)]
        [InlineData(19, true)]
        [InlineData(24, true)]
        [InlineData(1000, true)]
        [InlineData(3802, true)]
        [InlineData(4072, true)]
        [InlineData(18, false)]
        [InlineData(25, false)]
        [InlineData(114, false)]
        [InlineData(650, false)]
        [InlineData(774, false)]
        [InlineData(3220, false)]
        [InlineData(100003, false)]
        public void ShouldPreserveNullFormatSelectionGivenKnownTypeWhenBinaryParameterIsRequested(int oid, bool eligible)
        {
            // Arrange
            object? value = null;

            // Act
            byte[]? encoded = CassieParameterCodec.GetBinaryParameter(value, oid);

            // Assert
            Assert.Equal(eligible, encoded is not null);
        }

        [Theory]
        [InlineData(DbType.Boolean, 16)]
        [InlineData(DbType.Byte, 21)]
        [InlineData(DbType.UInt16, 23)]
        [InlineData(DbType.UInt32, 26)]
        [InlineData(DbType.UInt64, 1700)]
        [InlineData(DbType.AnsiStringFixedLength, 1042)]
        [InlineData(DbType.DateTime2, 1114)]
        public void ShouldPreferExplicitDbTypeGivenConflictingClrValueWhenParameterIsPrepared(DbType dbType, int expectedOid)
        {
            // Arrange
            using CassieCommand command = new() { CommandText = "SELECT @p" };
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "p", DbType = dbType, Value = "text" });

            // Act
            CassiePreparedCommand prepared = command.PrepareParameters();

            // Assert
            Assert.Equal(expectedOid, Assert.Single(prepared.ParameterTypeOids));
        }

        [Fact]
        public void ShouldShareInferenceAndCodecMetadataGivenMacaddr8WhenScalarAndArrayArePrepared()
        {
            // Arrange
            PhysicalAddress address = PhysicalAddress.Parse("0011223344556677");
            using CassieCommand scalar = new() { CommandText = "SELECT @p" };
            _ = scalar.Parameters.Add(new CassieParameter { ParameterName = "p", Value = address });
            using CassieCommand array = new() { CommandText = "SELECT @p" };
            _ = array.Parameters.Add(new CassieParameter { ParameterName = "p", Value = new[] { address } });

            // Act
            int scalarOid = Assert.Single(scalar.PrepareParameters().ParameterTypeOids);
            int arrayOid = Assert.Single(array.PrepareParameters().ParameterTypeOids);
            byte[] encoded = Assert.IsType<byte[]>(CassieParameterCodec.GetBinaryParameter(address, scalarOid));

            // Assert
            Assert.Equal(774, scalarOid);
            Assert.Equal(775, arrayOid);
            Assert.Equal("macaddr8[]", CassieTypeMap.GetDataTypeName(arrayOid));
            Assert.Equal(address, CassieResultCodec.ConvertBinaryValue(encoded, scalarOid));
        }
    }
}
