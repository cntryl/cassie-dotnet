using System.Text;
using Cntryl.Cassie.Protocol;
using Xunit;

namespace Cntryl.Cassie.Tests.Protocol
{
    public sealed class ServerErrorParserTests
    {
        [Fact]
        public void ShouldPreserveStructuredErrorGivenAllSupportedFieldsWhenPayloadIsParsed()
        {
            // Arrange
            byte[] payload = Encoding.UTF8.GetBytes("Slocalized\0VERROR\0C23505\0Mduplicate\0Ddetail\0Hhint\0P7\0spublic\0titems\0cid\0nitems_pkey\0Findex.c\0L12\0Rinsert\0?future\0\0");

            // Act
            CassieException error = ServerErrorParser.Parse(payload);

            // Assert
            Assert.Equal("duplicate", error.Message);
            Assert.Equal("23505", error.SqlState);
            Assert.Equal("ERROR", error.Severity);
            Assert.Equal("detail", error.Detail);
            Assert.Equal("hint", error.Hint);
            Assert.Equal("7", error.Position);
            Assert.Equal("public", error.SchemaName);
            Assert.Equal("items", error.TableName);
            Assert.Equal("id", error.ColumnName);
            Assert.Equal("items_pkey", error.ConstraintName);
            Assert.Equal("index.c", error.File);
            Assert.Equal("12", error.Line);
            Assert.Equal("insert", error.Routine);
        }

        [Fact]
        public void ShouldUseFallbacksGivenMissingMessageAndNonlocalizedSeverityWhenPayloadIsParsed()
        {
            // Arrange
            byte[] payload = Encoding.UTF8.GetBytes("SERROR\0\0");

            // Act
            CassieException error = ServerErrorParser.Parse(payload);

            // Assert
            Assert.Equal("Cassie returned an error without a message.", error.Message);
            Assert.Equal("ERROR", error.Severity);
            Assert.Null(error.SqlState);
        }

        [Fact]
        public void ShouldRejectUnterminatedFieldGivenTruncatedServerErrorWhenPayloadIsParsed()
        {
            // Arrange
            byte[] payload = Encoding.UTF8.GetBytes("Mtruncated");

            // Act
            void Parse()
            {
                _ = ServerErrorParser.Parse(payload);
            }

            // Assert
            _ = Assert.Throws<IOException>(Parse);
        }
    }
}
