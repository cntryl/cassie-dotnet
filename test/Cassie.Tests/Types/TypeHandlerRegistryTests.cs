using Cntryl.Cassie.Types;
using Cntryl.Cassie.Types.Handlers;
using Xunit;

namespace Cntryl.Cassie.Tests.Types
{
    public sealed class TypeHandlerRegistryTests
    {
        [Theory]
        [InlineData(16)]
        [InlineData(17)]
        [InlineData(18)]
        [InlineData(19)]
        [InlineData(20)]
        [InlineData(21)]
        [InlineData(23)]
        [InlineData(24)]
        [InlineData(25)]
        [InlineData(26)]
        [InlineData(114)]
        [InlineData(650)]
        [InlineData(700)]
        [InlineData(701)]
        [InlineData(774)]
        [InlineData(829)]
        [InlineData(869)]
        [InlineData(1042)]
        [InlineData(1043)]
        [InlineData(1082)]
        [InlineData(1083)]
        [InlineData(1114)]
        [InlineData(1184)]
        [InlineData(1186)]
        [InlineData(1266)]
        [InlineData(1560)]
        [InlineData(1562)]
        [InlineData(1700)]
        [InlineData(2205)]
        [InlineData(2278)]
        [InlineData(2950)]
        [InlineData(3220)]
        [InlineData(3802)]
        [InlineData(4072)]
        [InlineData(4191)]
        [InlineData(100001)]
        [InlineData(132767)]
        public void ShouldFindHandlerGivenBinaryCapableScalarOidWhenRegistryIsQueried(int oid)
        {
            // Arrange

            // Act
            CassieTypeHandler? handler = CassieTypeHandlers.Find(oid);

            // Assert
            Assert.NotNull(handler);
            Assert.True(CassieTypeMap.SupportsBinaryResult(oid));
        }

        [Theory]
        [InlineData(705)]
        [InlineData(2249)]
        [InlineData(2287)]
        [InlineData(1007)]
        [InlineData(100000)]
        [InlineData(132768)]
        [InlineData(9999)]
        public void ShouldFindNoHandlerGivenRecordArrayOrUnknownOidWhenRegistryIsQueried(int oid)
        {
            // Arrange

            // Act
            CassieTypeHandler? handler = CassieTypeHandlers.Find(oid);

            // Assert
            Assert.Null(handler);
        }

        [Fact]
        public void ShouldShareOneHandlerGivenOidFamilyWhenRegistryIsQueried()
        {
            // Arrange

            // Act
            CassieTypeHandler? oid = CassieTypeHandlers.Find(26);
            CassieTypeHandler? regclass = CassieTypeHandlers.Find(2205);
            CassieTypeHandler? text = CassieTypeHandlers.Find(25);

            // Assert
            Assert.Same(oid, regclass);
            Assert.NotSame(oid, text);
        }

        [Fact]
        public void ShouldFindFieldReaderGivenConvertedClrTypesWhenRegistryIsQueried()
        {
            // Arrange

            // Act
            bool decimalFound = CassieTypeHandlers.FindFieldReader<decimal>() is not null;
            bool dateTimeFound = CassieTypeHandlers.FindFieldReader<DateTime>() is not null;
            bool timeSpanFound = CassieTypeHandlers.FindFieldReader<TimeSpan>() is not null;
            bool ulongFound = CassieTypeHandlers.FindFieldReader<ulong>() is not null;
            bool temporalFound = CassieTypeHandlers.FindFieldReader<CassieTemporalValue>() is not null;
            bool intFound = CassieTypeHandlers.FindFieldReader<int>() is not null;
            bool nullableFound = CassieTypeHandlers.FindFieldReader<decimal?>() is not null;

            // Assert
            Assert.True(decimalFound && dateTimeFound && timeSpanFound && ulongFound && temporalFound);
            Assert.False(intFound);
            Assert.False(nullableFound);
        }
    }
}
