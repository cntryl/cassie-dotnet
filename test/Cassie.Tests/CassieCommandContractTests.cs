using System.Data;
using Xunit;

namespace Cntryl.Cassie.Tests
{
    public sealed class CassieCommandContractTests
    {
        [Fact]
        public void ShouldBuildPositionalCallGivenStoredProcedureCommandWhenParametersAreBound()
        {
            // Arrange
            using CassieCommand command = new()
            {
                CommandType = CommandType.StoredProcedure,
                CommandText = "public.echo"
            };
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", Value = "hello" });
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "count", Value = 42 });

            // Act
            CassiePreparedCommand prepared = command.PrepareParameters();

            // Assert
            Assert.Equal("CALL \"public\".\"echo\"($1, $2)", prepared.Sql);
            Assert.Equal(2, prepared.ParameterNames.Count);
            Assert.Equal("value", prepared.ParameterNames[0]);
            Assert.Equal("count", prepared.ParameterNames[1]);
            Assert.Equal("hello", prepared.Values[0]);
            Assert.Equal(42, prepared.Values[1]);
            Assert.Equal(25, prepared.ParameterTypeOids[0]);
            Assert.Equal(23, prepared.ParameterTypeOids[1]);
        }

        [Fact]
        public void ShouldPreserveQuotedProcedureNameGivenEscapedIdentifierComponentsWhenCommandIsPrepared()
        {
            // Arrange
            using CassieCommand command = new()
            {
                CommandType = CommandType.StoredProcedure,
                CommandText = "\"sales.region\".\"echo\"\"Row\""
            };

            // Act
            CassiePreparedCommand prepared = command.PrepareParameters();

            // Assert
            Assert.Equal("CALL \"sales.region\".\"echo\"\"Row\"()", prepared.Sql);
        }

        [Fact]
        public void ShouldRejectSqlGivenStoredProcedureCommandTextWhenIdentifierPathIsMalformed()
        {
            // Arrange
            using CassieCommand command = new()
            {
                CommandType = CommandType.StoredProcedure,
                CommandText = "public.echo(); SELECT 1"
            };

            // Act
            ArgumentException exception = Assert.Throws<ArgumentException>(command.PrepareParameters);

            // Assert
            Assert.Contains("procedure name", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ShouldRejectNullCharacterGivenQuotedProcedureNameWhenCommandIsPrepared()
        {
            // Arrange
            using CassieCommand command = new()
            {
                CommandType = CommandType.StoredProcedure,
                CommandText = "\"unsafe\0name\""
            };

            // Act
            ArgumentException exception = Assert.Throws<ArgumentException>(command.PrepareParameters);

            // Assert
            Assert.Contains("procedure name", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ShouldRejectOutputParameterGivenStoredProcedureCommandWhenParametersArePrepared()
        {
            // Arrange
            using CassieCommand command = new()
            {
                CommandType = CommandType.StoredProcedure,
                CommandText = "echo"
            };
            _ = command.Parameters.Add(new CassieParameter { ParameterName = "value", Direction = ParameterDirection.Output });

            // Act
            NotSupportedException exception = Assert.Throws<NotSupportedException>(command.PrepareParameters);

            // Assert
            Assert.Contains("input-only", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ShouldRejectTableDirectGivenUnsupportedCommandTypeWhenParametersArePrepared()
        {
            // Arrange
            using CassieCommand command = new()
            {
                CommandType = CommandType.TableDirect,
                CommandText = "public.items"
            };

            // Act
            NotSupportedException exception = Assert.Throws<NotSupportedException>(command.PrepareParameters);

            // Assert
            Assert.Contains("command type", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
