using Xunit;

namespace Cntryl.Cassie.Tests
{
    public sealed class ParameterScannerContractTests
    {
        [Fact]
        public void ShouldFindNamedMarkersGivenSqlLiteralsAndCommentsWhenSqlIsScanned()
        {
            // Arrange
            const string sql = "SELECT @amount, '@ignored', \"@identifier\" -- @comment\n/* @block */ WHERE id = @id AND text = '@also_ignored'";

            // Act
            IReadOnlyList<string> names = SqlParameterScanner.FindParameterNames(sql);

            // Assert
            Assert.Equal(["amount", "id"], names);
        }

        [Fact]
        public void ShouldDeduplicateMarkersGivenRepeatedParameterWhenSqlIsScanned()
        {
            // Arrange
            const string sql = "SELECT @value + @value";

            // Act
            IReadOnlyList<string> names = SqlParameterScanner.FindParameterNames(sql);

            // Assert
            Assert.Equal(["value"], names);
        }

        [Fact]
        public void ShouldIgnoreDollarQuotedAndNestedCommentMarkersGivenPostgreSqlSqlWhenSqlIsScanned()
        {
            // Arrange
            const string sql = "SELECT $$ @dollar $$, $body$ @tagged $body$, @real /* @outer /* @inner */ @still_outer */";

            // Act
            IReadOnlyList<string> names = SqlParameterScanner.FindParameterNames(sql);

            // Assert
            Assert.Equal(["real"], names);
        }

        [Fact]
        public void ShouldRecognizeMarkerAfterBackslashInOrdinaryStringGivenStandardConformingSqlWhenSqlIsScanned()
        {
            // Arrange
            const string sql = "SELECT 'literal\\', @outside, E'@inside \\'";

            // Act
            IReadOnlyList<string> names = SqlParameterScanner.FindParameterNames(sql);

            // Assert
            Assert.Equal(["outside"], names);
        }
    }
}
