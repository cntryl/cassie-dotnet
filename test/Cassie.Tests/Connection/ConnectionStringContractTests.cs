using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Connection;
using Cntryl.Cassie.Data;
using System.Data.Common;
using Xunit;

namespace Cntryl.Cassie.Tests.Connection
{
    public sealed class ConnectionStringContractTests
    {
        [Fact]
        public void ShouldRoundTripConnectionSettingsGivenConnectionStringWhenBuilderIsCreated()
        {
            // Arrange
            const string connectionString = "Host=db.example;Port=6432;Database=ledger;Username=app;Password=secret;SSL Mode=Require;SSL Root Certificate=/etc/cassie/ca.pem";

            // Act
            CassieConnectionStringBuilder builder = new(connectionString);

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
            CassieConnectionStringBuilder builder = new($"Options={options}");

            // Act
            string parsedOptions = builder.Options;
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
            ArgumentException exception = Assert.Throws<ArgumentException>(() => new CassieConnectionStringBuilder { Options = "-c TimeZone=UTC\0-c search_path=public" });

            // Assert
            Assert.Contains("NUL", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ShouldRegisterProviderGivenFactoryWhenInstanceIsRequested()
        {
            // Arrange
            CassieProviderFactory factory = CassieProviderFactory.Instance;

            // Act
            DbConnection connection = factory.CreateConnection();
            DbCommand command = factory.CreateCommand();
            DbParameter parameter = factory.CreateParameter();
            DbDataAdapter adapter = factory.CreateDataAdapter();

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
            CassieConnectionStringBuilder builder = new("Timeout=0");

            // Act
            int timeout = builder.Timeout;

            // Assert
            Assert.Equal(0, timeout);
        }

        [Fact]
        public void ShouldParsePoolingSettingsGivenConnectionStringWhenBuilderIsCreated()
        {
            // Arrange
            CassieConnectionStringBuilder builder = new("Pooling=true;Maximum Pool Size=8");

            // Act
            bool pooling = builder.Pooling;
            int maximumPoolSize = builder.MaxPoolSize;

            // Assert
            Assert.True(pooling);
            Assert.Equal(8, maximumPoolSize);
        }

        [Fact]
        public void ShouldRejectInvalidMaximumPoolSizeGivenConnectionStringWhenBuilderIsRead()
        {
            // Arrange
            CassieConnectionStringBuilder builder = new("Maximum Pool Size=0");

            // Act
            ArgumentException exception = Assert.Throws<ArgumentException>(() => _ = builder.MaxPoolSize);

            // Assert
            Assert.Contains("between 1 and 1000", exception.Message, StringComparison.Ordinal);
        }
    }
}
