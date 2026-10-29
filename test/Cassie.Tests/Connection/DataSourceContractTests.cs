using Cntryl.Cassie.Connection;
using System.Data;
using System.Data.Common;
using Xunit;

namespace Cntryl.Cassie.Tests.Connection
{
    public sealed class DataSourceContractTests
    {
        [Fact]
        public void ShouldCreateCassieConnectionGivenConnectionStringWhenFactoryDataSourceCreatesConnection()
        {
            // Arrange
            const string connectionString = "Host=db.example;Port=6432;Database=ledger;Username=app;SSL Mode=Require";
            CassieProviderFactory factory = CassieProviderFactory.Instance;

            // Act
            using DbDataSource dataSource = factory.CreateDataSource(connectionString);
            using DbConnection connection = dataSource.CreateConnection();

            // Assert
            _ = Assert.IsType<CassieConnection>(connection);
            Assert.Equal(connectionString, dataSource.ConnectionString);
            Assert.Equal(connectionString, connection.ConnectionString);
            Assert.Equal(ConnectionState.Closed, connection.State);
        }

        [Fact]
        public void ShouldReportUsernameAsDatabaseGivenConnectionStringOmitsDatabaseWhenDatabaseIsRead()
        {
            // Arrange
            using CassieConnection connection = new()
            {
                ConnectionString = "Host=localhost;Username=app"
            };

            // Act
            string database = connection.Database;

            // Assert
            Assert.Equal("app", database);
        }
    }
}
