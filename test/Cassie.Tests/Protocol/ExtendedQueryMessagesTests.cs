using Cntryl.Cassie.Protocol;
using Xunit;

namespace Cntryl.Cassie.Tests.Protocol;

public sealed class ExtendedQueryMessagesTests
{
    [Fact]
    public void ShouldWriteStatementAndTypeOidsGivenParameterizedSqlWhenParseIsBuilt()
    {
        // Arrange
        var expected = Convert.FromHexString("730053454C4543542024312C2024320000020000001700000019");

        // Act
        using var payload = ExtendedQueryMessages.Parse("s", "SELECT $1, $2", [23, 25]);

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
        using var describe = ExtendedQueryMessages.DescribeStatement("s");
        using var close = ExtendedQueryMessages.CloseStatement("s");
        using var portal = ExtendedQueryMessages.DescribeUnnamedPortal();

        // Assert
        Assert.Equal(statementTarget, describe.ToArray());
        Assert.Equal(statementTarget, close.ToArray());
        Assert.Equal(portalTarget, portal.ToArray());
    }

    [Fact]
    public void ShouldAppendFlushAfterExecuteGivenPagedPortalWhenFramesAreBuilt()
    {
        // Arrange
        var expected = Convert.FromHexString("450000000900010203044800000004");

        // Act
        var frames = ExtendedQueryMessages.ExecutePageFrames(0x01020304);

        // Assert
        Assert.Equal(expected, frames);
    }

    [Fact]
    public void ShouldRejectParameterCountOverflowGivenTooManyTypeOidsWhenParseIsBuilt()
    {
        // Arrange
        var oids = new int[short.MaxValue + 1];

        // Act
        void Build()
        {
            using var payload = ExtendedQueryMessages.Parse(string.Empty, string.Empty, oids);
        }

        // Assert
        _ = Assert.Throws<OverflowException>(Build);
    }
}
