using Cntryl.Cassie.Commands;
using System.Data;
using Xunit;

namespace Cntryl.Cassie.Tests.Commands
{
    public sealed class ParameterScannerContractTests
    {
        [Theory]
        [InlineData("SELECT @value + @VALUE -- @line\r\n/* @block */", "SELECT $1 + $1 -- @line\r\n/* @block */")]
        [InlineData("SELECT \"a\"\"@hidden\", '@hidden''@still', @value", "SELECT \"a\"\"@hidden\", '@hidden''@still', $1")]
        [InlineData("SELECT E'it\\'s @hidden', @value", "SELECT E'it\\'s @hidden', $1")]
        [InlineData("SELECT $$ @one $inner$ @two $$, @value", "SELECT $$ @one $inner$ @two $$, $1")]
        [InlineData("SELECT $Tag$ @one $tag$ @two $Tag$, @value", "SELECT $Tag$ @one $tag$ @two $Tag$, $1")]
        [InlineData("SELECT 'literal\\',@value", "SELECT 'literal\\',$1")]
        [InlineData("SELECT 1+@value", "SELECT 1+$1")]
        [InlineData("SELECT @value::int, payload@>other, payload@?path", "SELECT $1::int, payload@>other, payload@?path")]
        [InlineData("SELECT values<@other_values, @value", "SELECT values<@other_values, $1")]
        [InlineData("SELECT /* nest /* @hidden */ @still */ @value", "SELECT /* nest /* @hidden */ @still */ $1")]
        [InlineData("SELECT $1, @value", "SELECT $1, $1")]
        public void ShouldUseIdenticalLexicalBoundariesGivenAdversarialSqlWhenMarkersAreDiscoveredAndRewritten(string sql, string rewritten)
        {
            // Arrange
            CassieParameterCollection parameters = [new CassieParameter { ParameterName = "value", Value = 7 }];

            // Act
            IReadOnlyList<string> names = SqlParameterScanner.FindParameterNames(sql);
            CassiePreparedCommand prepared = CommandParameterBinder.Prepare(sql, CommandType.Text, parameters);

            // Assert
            Assert.Equal(["value"], names);
            Assert.Equal(names, prepared.ParameterNames);
            Assert.Equal(rewritten, prepared.Sql);
            Assert.Equal([7], prepared.Values);
        }

        [Fact]
        public void ShouldPreserveExistingDollarMarkerGrammarGivenDollarInNameWhenSqlIsScannedAndPrepared()
        {
            // Arrange
            const string sql = "SELECT @value$part";
            using CassieCommand command = new() { CommandText = sql };
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "value$part", Value = 7 });

            // Act
            IReadOnlyList<string> names = SqlParameterScanner.FindParameterNames(sql);
            CassiePreparedCommand prepared = command.PrepareParameters();

            // Assert
            Assert.Equal(["value$part"], names);
            Assert.Equal(names, prepared.ParameterNames);
            Assert.Equal("SELECT $1", prepared.Sql);
        }

        [Fact]
        public void ShouldPreserveDoubleAtOperatorGivenAdjacentIdentifierWhenSqlIsScannedAndPrepared()
        {
            // Arrange
            const string sql = "SELECT document@@query, @value";
            using CassieCommand command = new() { CommandText = sql };
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", Value = 7 });

            // Act
            IReadOnlyList<string> names = SqlParameterScanner.FindParameterNames(sql);
            CassiePreparedCommand prepared = command.PrepareParameters();

            // Assert
            Assert.Equal(["value"], names);
            Assert.Equal(names, prepared.ParameterNames);
            Assert.Equal("SELECT document@@query, $1", prepared.Sql);
        }

        [Fact]
        public void ShouldPreserveDollarSignsInIdentifierGivenFollowingMarkerWhenSqlIsScannedAndPrepared()
        {
            // Arrange
            const string sql = "SELECT column$tag$ + @value";
            using CassieCommand command = new() { CommandText = sql };
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", Value = 7 });

            // Act
            IReadOnlyList<string> names = SqlParameterScanner.FindParameterNames(sql);
            CassiePreparedCommand prepared = command.PrepareParameters();

            // Assert
            Assert.Equal(["value"], names);
            Assert.Equal(names, prepared.ParameterNames);
            Assert.Equal("SELECT column$tag$ + $1", prepared.Sql);
        }

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
