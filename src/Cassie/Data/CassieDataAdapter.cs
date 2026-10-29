using Cntryl.Cassie.Commands;
using System.Data.Common;

namespace Cntryl.Cassie.Data
{
    /// <summary>Bridges Cassie commands to ADO.NET DataSet and DataTable workflows.</summary>
    public sealed class CassieDataAdapter : DbDataAdapter
    {
        /// <summary>Creates an empty adapter. Assign its select and modification commands before use.</summary>
        public CassieDataAdapter()
        {
        }

        /// <summary>Creates an adapter that fills from the specified Cassie command.</summary>
        /// <param name="selectCommand">The command used to retrieve rows.</param>
        public CassieDataAdapter(CassieCommand selectCommand)
        {
            ArgumentNullException.ThrowIfNull(selectCommand);
            SelectCommand = selectCommand;
        }

        /// <summary>Gets or sets the Cassie select command.</summary>
        public new CassieCommand? SelectCommand
        {
            get => (CassieCommand?)base.SelectCommand;
            set => base.SelectCommand = value;
        }

        /// <summary>Gets or sets the Cassie insert command.</summary>
        public new CassieCommand? InsertCommand
        {
            get => (CassieCommand?)base.InsertCommand;
            set => base.InsertCommand = value;
        }

        /// <summary>Gets or sets the Cassie update command.</summary>
        public new CassieCommand? UpdateCommand
        {
            get => (CassieCommand?)base.UpdateCommand;
            set => base.UpdateCommand = value;
        }

        /// <summary>Gets or sets the Cassie delete command.</summary>
        public new CassieCommand? DeleteCommand
        {
            get => (CassieCommand?)base.DeleteCommand;
            set => base.DeleteCommand = value;
        }
    }
}
