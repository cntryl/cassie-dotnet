using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Cntryl.Cassie.DependencyInjection.Tests.Infrastructure
{
    internal sealed class OtherConnection : DbConnection
    {
        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => "other";
        public override string DataSource => "other-provider";
        public override string ServerVersion => "0.1";
        public override ConnectionState State => ConnectionState.Closed;

        public override void ChangeDatabase(string databaseName)
        {
            throw new NotSupportedException();
        }

        public override void Close()
        {
        }

        public override void Open()
        {
            throw new NotSupportedException();
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            throw new NotSupportedException();
        }

        protected override DbCommand CreateDbCommand()
        {
            throw new NotSupportedException();
        }
    }
}
