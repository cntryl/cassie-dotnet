using System.Text;
using Cntryl.Cassie.Protocol;
using Xunit;

namespace Cntryl.Cassie.Tests.Protocol
{
    public sealed class CommandTagTests
    {
        [Theory]
        [InlineData("INSERT 0 3", true, 3)]
        [InlineData("UPDATE 0", true, 0)]
        [InlineData("COPY 2147483647", true, int.MaxValue)]
        [InlineData("SELECT 9", false, 9)]
        [InlineData("DISCARD ALL", false, 0)]
        [InlineData("DELETE -1", true, 0)]
        [InlineData("DELETE +1", true, 0)]
        [InlineData("DELETE 1 ", true, 0)]
        [InlineData("DELETE 2147483648", true, 0)]
        [InlineData("DELETE 9999999999999999999999", true, 0)]
        public void ShouldReadUnsignedRowCountGivenCommandTagWhenPayloadIsParsed(string text, bool countsRecords, int rowCount)
        {
            // Arrange
            byte[] payload = Encoding.UTF8.GetBytes(text + '\0');

            // Act
            CommandTag tag = CommandTag.FromPayload(payload);

            // Assert
            Assert.Equal(text, tag.Text);
            Assert.Equal(countsRecords, tag.CountsRecords);
            Assert.Equal(rowCount, tag.RowCount);
        }

        [Fact]
        public void ShouldMatchWholeVerbGivenSimilarPrefixesWhenCommandIsClassified()
        {
            // Arrange
            CommandTag setting = new("set role");
            CommandTag unrelated = new("SETTINGS");

            // Act
            bool matches = setting.HasVerb("SET");
            bool prefixMatches = unrelated.HasVerb("SET");

            // Assert
            Assert.True(matches);
            Assert.False(prefixMatches);
            Assert.True(new CommandTag("discard all").Matches("DISCARD ALL"));
        }
    }
}
