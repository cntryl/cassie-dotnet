using Cntryl.Cassie.Protocol;
using Xunit;

namespace Cntryl.Cassie.Tests.Protocol
{
    public sealed class ExtendedQueryMessagesTests
    {
        [Fact]
        public void ShouldWriteStatementAndTypeOidsGivenParameterizedSqlWhenParseIsBuilt()
        {
            // Arrange
            byte[] expected = Convert.FromHexString("730053454C4543542024312C2024320000020000001700000019");

            // Act
            using MemoryStream payload = ExtendedQueryMessages.Parse("s", "SELECT $1, $2", [23, 25]);

            // Assert
            Assert.Equal(expected, payload.ToArray());
        }

        [Fact]
        public void ShouldDistinguishStatementAndPortalGivenDescribeAndCloseWhenTargetsAreBuilt()
        {
            // Arrange
            byte[] statementTarget = [(byte)'S', (byte)'s', 0];
            byte[] portalTarget = [(byte)'P', 0];

            // Act
            using MemoryStream describe = ExtendedQueryMessages.DescribeStatement("s");
            using MemoryStream close = ExtendedQueryMessages.CloseStatement("s");
            using MemoryStream portal = ExtendedQueryMessages.DescribeUnnamedPortal();

            // Assert
            Assert.Equal(statementTarget, describe.ToArray());
            Assert.Equal(statementTarget, close.ToArray());
            Assert.Equal(portalTarget, portal.ToArray());
        }

        [Fact]
        public void ShouldAppendFlushAfterExecuteGivenPagedPortalWhenFramesAreBuilt()
        {
            // Arrange
            byte[] expected = Convert.FromHexString("450000000900010203044800000004");

            // Act
            byte[] frames = ExtendedQueryMessages.ExecutePageFrames(0x01020304);

            // Assert
            Assert.Equal(expected, frames);
        }

        [Fact]
        public void ShouldRejectParameterCountOverflowGivenTooManyTypeOidsWhenParseIsBuilt()
        {
            // Arrange
            int[] oids = new int[short.MaxValue + 1];

            // Act
            void Build()
            {
                using MemoryStream payload = ExtendedQueryMessages.Parse(string.Empty, string.Empty, oids);
            }

            // Assert
            _ = Assert.Throws<OverflowException>(Build);
        }
    }
}
