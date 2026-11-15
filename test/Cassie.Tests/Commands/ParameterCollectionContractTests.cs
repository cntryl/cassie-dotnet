using Cntryl.Cassie.Commands;
using System.Data.Common;
using Xunit;

namespace Cntryl.Cassie.Tests.Commands
{
    public sealed class ParameterCollectionContractTests
    {
        [Theory]
        [InlineData(-1)]
        [InlineData(2)]
        public void ShouldReportIndexOutOfRangeGivenIndexOutsideCollectionWhenIndexerIsUsed(int index)
        {
            // Arrange
            CassieParameterCollection parameters = [];
            _ = parameters.Add(new CassieParameter { ParameterName = "first", Value = 1 });
            _ = parameters.Add(new CassieParameter { ParameterName = "second", Value = 2 });
            DbParameterCollection collection = parameters;

            // Act
            void Read()
            {
                _ = collection[index];
            }

            void Write()
            {
                collection[index] = new CassieParameter { ParameterName = "replacement" };
            }

            // Assert
            _ = Assert.Throws<IndexOutOfRangeException>(Read);
            _ = Assert.Throws<IndexOutOfRangeException>(Write);
            Assert.Equal(2, collection.Count);
        }

        [Fact]
        public void ShouldReadAndReplaceGivenIndexInsideCollectionWhenIndexerIsUsed()
        {
            // Arrange
            CassieParameterCollection parameters = [];
            _ = parameters.Add(new CassieParameter { ParameterName = "first", Value = 1 });
            DbParameterCollection collection = parameters;
            CassieParameter replacement = new() { ParameterName = "replacement", Value = 9 };

            // Act
            DbParameter original = collection[0];
            collection[0] = replacement;

            // Assert
            Assert.Equal("first", original.ParameterName);
            Assert.Same(replacement, collection[0]);
            Assert.Same(replacement, collection["@replacement"]);
        }
    }
}
