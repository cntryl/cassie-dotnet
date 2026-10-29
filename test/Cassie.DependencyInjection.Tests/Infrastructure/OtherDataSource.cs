using System.Data.Common;

namespace Cntryl.Cassie.DependencyInjection.Tests.Infrastructure
{
    internal sealed class OtherDataSource : DbDataSource
    {
        public override string ConnectionString => "Provider=other";

        protected override DbConnection CreateDbConnection()
        {
            return new OtherConnection { ConnectionString = ConnectionString };
        }
    }
}
