using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Connection;
using Cntryl.Cassie.Data;
using System.Data.Common;

namespace Cntryl.Cassie
{
    /// <summary>Creates Cassie ADO.NET provider objects.</summary>
    public sealed class CassieProviderFactory : DbProviderFactory
    {
        /// <summary>The singleton Cassie provider factory.</summary>
        public static CassieProviderFactory Instance { get; } = new();

        private CassieProviderFactory()
        {
        }

        /// <inheritdoc />
        public override DbConnection CreateConnection()
        {
            return new CassieConnection();
        }

        /// <inheritdoc />
        public override CassieDataSource CreateDataSource(string connectionString)
        {
            return new CassieDataSource(connectionString);
        }

        /// <inheritdoc />
        public override DbCommand CreateCommand()
        {
            return new CassieCommand();
        }

        /// <inheritdoc />
        public override DbParameter CreateParameter()
        {
            return new CassieParameter();
        }

        /// <inheritdoc />
        public override DbDataAdapter CreateDataAdapter()
        {
            return new CassieDataAdapter();
        }

        /// <inheritdoc />
        public override DbConnectionStringBuilder CreateConnectionStringBuilder()
        {
            return new CassieConnectionStringBuilder();
        }
    }
}
