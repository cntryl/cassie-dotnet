using Cntryl.Cassie.Types;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Cntryl.Cassie.Data
{
    /// <summary>Projects reader column metadata into the two ADO.NET schema representations.</summary>
    internal static class ReaderSchema
    {
        /// <inheritdoc />
        [UnconditionalSuppressMessage("Trimming", "IL2111", Justification = "The returned schema table reports Type metadata from the fixed built-in OID map, whose field types are rooted by the OID mapping.")]
        public static DataTable CreateTable(IReadOnlyList<ReaderColumn> columns)
        {
            DataTable table = new();
            _ = table.Columns.Add("ColumnName", typeof(string));
            _ = table.Columns.Add("ColumnOrdinal", typeof(int));
            _ = table.Columns.Add("ColumnSize", typeof(int));
            _ = table.Columns.Add("NumericPrecision", typeof(short));
            _ = table.Columns.Add("NumericScale", typeof(short));
            _ = table.Columns.Add("IsUnique", typeof(bool));
            _ = table.Columns.Add("IsKey", typeof(bool));
            _ = table.Columns.Add("BaseServerName", typeof(string));
            _ = table.Columns.Add("BaseCatalogName", typeof(string));
            _ = table.Columns.Add("BaseColumnName", typeof(string));
            _ = table.Columns.Add("BaseSchemaName", typeof(string));
            _ = table.Columns.Add("BaseTableName", typeof(string));
            _ = table.Columns.Add("DataType", typeof(Type));
            _ = table.Columns.Add("AllowDBNull", typeof(bool));
            _ = table.Columns.Add("ProviderType", typeof(int));
            _ = table.Columns.Add("IsAliased", typeof(bool));
            _ = table.Columns.Add("IsExpression", typeof(bool));
            _ = table.Columns.Add("IsIdentity", typeof(bool));
            _ = table.Columns.Add("IsAutoIncrement", typeof(bool));
            _ = table.Columns.Add("IsRowVersion", typeof(bool));
            _ = table.Columns.Add("IsHidden", typeof(bool));
            _ = table.Columns.Add("IsLong", typeof(bool));
            _ = table.Columns.Add("IsReadOnly", typeof(bool));
            _ = table.Columns.Add("ProviderSpecificDataType", typeof(Type));
            _ = table.Columns.Add("DataTypeName", typeof(string));
            for (int i = 0; i < columns.Count; i++)
            {
                ReaderColumn column = columns[i];
                _ = table.Rows.Add(
                    column.Name,
                    i,
                    GetColumnSize(column),
                    GetNumericPrecision(column),
                    GetNumericScale(column),
                    DBNull.Value,
                    DBNull.Value,
                    DBNull.Value,
                    DBNull.Value,
                    DBNull.Value,
                    DBNull.Value,
                    DBNull.Value,
                    column.FieldType,
                    DBNull.Value,
                    column.TypeOid,
                    DBNull.Value,
                    DBNull.Value,
                    DBNull.Value,
                    DBNull.Value,
                    DBNull.Value,
                    false,
                    DBNull.Value,
                    DBNull.Value,
                    column.FieldType,
                    CassieTypeMap.GetDataTypeName(column.TypeOid));
            }

            return table;
        }

        /// <summary>Gets column metadata while preserving unknown origin and constraint values.</summary>
        public static ReadOnlyCollection<DbColumn> CreateColumns(IReadOnlyList<ReaderColumn> fields)
        {
            List<DbColumn> columns = new(fields.Count);
            for (int ordinal = 0; ordinal < fields.Count; ordinal++)
            {
                columns.Add(new ReaderDbColumn(fields[ordinal], ordinal));
            }

            return columns.AsReadOnly();
        }

        private static int GetColumnSize(ReaderColumn column)
        {
            return column.TypeOid switch
            {
                1042 or 1043 when column.TypeModifier >= 4 => column.TypeModifier - 4,
                1700 when column.TypeModifier >= 4 => (column.TypeModifier - 4) >> 16,
                _ when CassieTypeMap.IsVectorOid(column.TypeOid) => CassieTypeMap.GetVectorDimension(column.TypeOid),
                _ => column.TypeSize
            };
        }

        private static object GetNumericPrecision(ReaderColumn column)
        {
            int precision = column.TypeOid switch
            {
                20 => 64,
                21 => 16,
                23 or 26 => 32,
                700 => 24,
                701 => 53,
                1700 when column.TypeModifier >= 4 => (column.TypeModifier - 4) >> 16,
                _ => -1
            };
            return precision >= 0 ? checked((short)precision) : DBNull.Value;
        }

        private static object GetNumericScale(ReaderColumn column)
        {
            if (column.TypeOid == 1700 && column.TypeModifier >= 4)
            {
                int encodedScale = (column.TypeModifier - 4) & 0x7ff;
                int scale = (encodedScale & 0x400) == 0 ? encodedScale : encodedScale | ~0x7ff;
                return checked((short)scale);
            }

            return column.TypeOid is 20 or 21 or 23 or 26 or 700 or 701 ? (short)0 : DBNull.Value;
        }

        private sealed class ReaderDbColumn : DbColumn
        {
            internal ReaderDbColumn(ReaderColumn column, int ordinal)
            {
                ColumnName = column.Name;
                ColumnOrdinal = ordinal;
                ColumnSize = GetColumnSize(column);
                NumericPrecision = GetNumericPrecision(column) is short precision ? precision : null;
                NumericScale = GetNumericScale(column) is short scale ? scale : null;
                DataType = column.FieldType;
                DataTypeName = CassieTypeMap.GetDataTypeName(column.TypeOid);
                AllowDBNull = null;
                BaseCatalogName = null;
                BaseColumnName = null;
                BaseSchemaName = null;
                BaseServerName = null;
                BaseTableName = null;
                IsAliased = null;
                IsAutoIncrement = null;
                IsExpression = null;
                IsHidden = false;
                IsIdentity = null;
                IsKey = null;
                IsLong = null;
                IsReadOnly = null;
                IsUnique = null;
            }
        }
    }
}
