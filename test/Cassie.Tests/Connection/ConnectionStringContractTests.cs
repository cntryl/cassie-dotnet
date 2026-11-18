using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Connection;
using Cntryl.Cassie.Data;
using Xunit;

namespace Cntryl.Cassie.Tests.Connection;

public sealed class ConnectionStringContractTests
{
    [Fact]
    public void ShouldRoundTripConnectionSettingsGivenConnectionStringWhenBuilderIsCreated()
    {
        // Arrange
        const string connectionString =
            "Host=db.example;Port=6432;Database=ledger;Username=app;Password=secret;SSL Mode=Require;SSL Root Certificate=/etc/cassie/ca.pem";

        // Act
        CassieConnectionStringBuilder builder = [with(connectionString)];

        // Assert
        Assert.Equal("db.example", builder.Host);
        Assert.Equal(6432, builder.Port);
        Assert.Equal("ledger", builder.Database);
        Assert.Equal("app", builder.Username);
        Assert.Equal("secret", builder.Password);
        Assert.Equal(CassieSslMode.Require, builder.SslMode);
        Assert.Equal("/etc/cassie/ca.pem", builder.SslRootCertificate);
    }

    [Fact]
    public void ShouldRoundTripStartupOptionsGivenConnectionStringWhenBuilderIsCreated()
    {
        // Arrange
        const string options = "-c TimeZone=UTC -c search_path=public";
        CassieConnectionStringBuilder builder = [with($"Options={options}")];

        // Act
        var parsedOptions = builder.Options;
        CassieConnectionStringBuilder created = new() { Options = options };

        // Assert
        Assert.Equal(options, parsedOptions);
        Assert.Equal(options, created.Options);
    }

    [Fact]
    public void ShouldRejectNulStartupOptionGivenConnectionStringWhenBuilderSetsOptions()
    {
        // Arrange
        // Act
        var exception = Assert.Throws<ArgumentException>(() => new CassieConnectionStringBuilder
        { Options = "-c TimeZone=UTC\0-c search_path=public" });

        // Assert
        Assert.Contains("NUL", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldRegisterProviderGivenFactoryWhenInstanceIsRequested()
    {
        // Arrange
        var factory = CassieProviderFactory.Instance;

        // Act
        var connection = factory.CreateConnection();
        var command = factory.CreateCommand();
        var parameter = factory.CreateParameter();
        var adapter = factory.CreateDataAdapter();

        // Assert
        _ = Assert.IsType<CassieConnection>(connection);
        _ = Assert.IsType<CassieCommand>(command);
        _ = Assert.IsType<CassieParameter>(parameter);
        _ = Assert.IsType<CassieDataAdapter>(adapter);
        Assert.Same(factory, CassieProviderFactory.Instance);
    }

    [Fact]
    public void ShouldAllowInfiniteTimeoutGivenZeroSecondsWhenSettingsAreParsed()
    {
        // Arrange
        CassieConnectionStringBuilder builder = [with("Timeout=0")];

        // Act
        var timeout = builder.Timeout;

        // Assert
        Assert.Equal(0, timeout);
    }

    [Fact]
    public void ShouldParsePoolingSettingsGivenConnectionStringWhenBuilderIsCreated()
    {
        // Arrange
        CassieConnectionStringBuilder builder = [with("Pooling=true;Maximum Pool Size=8")];

        // Act
        var pooling = builder.Pooling;
        var maximumPoolSize = builder.MaxPoolSize;

        // Assert
        Assert.True(pooling);
        Assert.Equal(8, maximumPoolSize);
    }

    [Fact]
    public void ShouldRejectInvalidMaximumPoolSizeGivenConnectionStringWhenBuilderIsRead()
    {
        // Arrange
        CassieConnectionStringBuilder builder = [with("Maximum Pool Size=0")];

        // Act
        var exception = Assert.Throws<ArgumentException>(() => _ = builder.MaxPoolSize);

        // Assert
        Assert.Contains("between 1 and 1000", exception.Message, StringComparison.Ordinal);
    }
}
