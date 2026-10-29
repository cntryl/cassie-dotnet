using Cntryl.Cassie.Commands;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;

namespace Cntryl.Cassie.Connection
{
    public sealed partial class CassieConnection
    {
        private static readonly (string Name, int Restrictions, int IdentifierParts)[] SchemaCollections =
        [
            ("MetaDataCollections", 0, 0),
            ("Restrictions", 0, 0),
            ("Tables", 4, 3),
            ("Columns", 4, 4)
        ];

        /// <inheritdoc />
        public override DataTable GetSchema()
        {
            return GetSchemaAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <inheritdoc />
        public override DataTable GetSchema(string collectionName)
        {
            return GetSchemaAsync(collectionName, null, CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <inheritdoc />
        public override DataTable GetSchema(string collectionName, string?[]? restrictionValues)
        {
            return GetSchemaAsync(collectionName, restrictionValues, CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <inheritdoc />
        public override Task<DataTable> GetSchemaAsync(CancellationToken cancellationToken = default)
        {
            return GetSchemaAsync("MetaDataCollections", null, cancellationToken);
        }

        /// <inheritdoc />
        public override Task<DataTable> GetSchemaAsync(string collectionName, CancellationToken cancellationToken)
        {
            return GetSchemaAsync(collectionName, null, cancellationToken);
        }

        /// <inheritdoc />
        public override Task<DataTable> GetSchemaAsync(string collectionName, string?[]? restrictionValues, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
            cancellationToken.ThrowIfCancellationRequested();
            string collection = collectionName.Trim();
            if (collection.Equals("MetaDataCollections", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(GetMetaDataCollections());
            }

            if (collection.Equals("Restrictions", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(GetRestrictions());
            }

            if (collection.Equals("Tables", StringComparison.OrdinalIgnoreCase))
            {
                string?[] restrictions = ValidateRestrictions(collection, restrictionValues);
                return GetTablesAsync(restrictions, cancellationToken);
            }

            if (collection.Equals("Columns", StringComparison.OrdinalIgnoreCase))
            {
                string?[] restrictions = ValidateRestrictions(collection, restrictionValues);
                return GetColumnsAsync(restrictions, cancellationToken);
            }

            throw new ArgumentException($"Schema collection '{collectionName}' is not supported.", nameof(collectionName));
        }

        private static DataTable GetMetaDataCollections()
        {
            DataTable table = CreateSchemaTable("MetaDataCollections");
            AddStringColumns(table, "CollectionName");
            AddIntColumns(table, "NumberOfRestrictions", "NumberOfIdentifierParts");
            foreach ((string name, int restrictions, int identifierParts) in SchemaCollections)
            {
                _ = table.Rows.Add(name, restrictions, identifierParts);
            }

            return table;
        }

        private static DataTable GetRestrictions()
        {
            DataTable table = CreateSchemaTable("Restrictions");
            AddStringColumns(table, "CollectionName", "RestrictionName", "RestrictionDefault");
            AddIntColumns(table, "RestrictionNumber");
            AddRestrictions(table, "Tables", ["TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "TABLE_TYPE"]);
            AddRestrictions(table, "Columns", ["TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "COLUMN_NAME"]);
            return table;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "Only fixed catalog identifiers are composed into the query; all restriction values are bound parameters.")]
        private async Task<DataTable> GetTablesAsync(string?[] restrictions, CancellationToken cancellationToken)
        {
            DataTable table = CreateSchemaTable("Tables");
            AddStringColumns(table, "TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "TABLE_TYPE");
            if (!MatchesCurrentCatalog(restrictions[0]))
            {
                return table;
            }

            using DbCommand command = CreateCommand();
            command.CommandText = BuildSchemaQuery(command,
                "SELECT table_schema, table_name, table_type FROM information_schema.tables",
                ["table_schema", "table_name", "table_type"],
                restrictions,
                startRestriction: 1,
                orderBy: "table_schema, table_name");
            using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                _ = table.Rows.Add(GetCurrentCatalog(), reader.GetString(0), reader.GetString(1), reader.GetString(2));
            }

            _ = await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            return table;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "Only fixed catalog identifiers are composed into the query; all restriction values are bound parameters.")]
        private async Task<DataTable> GetColumnsAsync(string?[] restrictions, CancellationToken cancellationToken)
        {
            DataTable table = CreateSchemaTable("Columns");
            AddStringColumns(table, "TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "COLUMN_NAME");
            AddIntColumns(table, "ORDINAL_POSITION");
            AddStringColumns(table, "COLUMN_DEFAULT", "IS_NULLABLE", "DATA_TYPE");
            AddIntColumns(table, "CHARACTER_MAXIMUM_LENGTH", "NUMERIC_PRECISION", "NUMERIC_SCALE", "DATETIME_PRECISION");
            AddStringColumns(table, "UDT_NAME");
            if (!MatchesCurrentCatalog(restrictions[0]))
            {
                return table;
            }

            using DbCommand command = CreateCommand();
            command.CommandText = BuildSchemaQuery(command,
                "SELECT table_schema, table_name, column_name, ordinal_position, column_default, is_nullable, data_type, character_maximum_length, numeric_precision, numeric_scale, datetime_precision, udt_name FROM information_schema.columns",
                ["table_schema", "table_name", "column_name", "ordinal_position"],
                restrictions,
                startRestriction: 1,
                orderBy: "table_schema, table_name, ordinal_position");
            using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                object?[] row =
                [
                    GetCurrentCatalog(),
                    reader.GetValue(0),
                    reader.GetValue(1),
                    reader.GetValue(2),
                    Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
                    GetNullableValue(reader, 4),
                    reader.GetValue(5),
                    reader.GetValue(6),
                    GetNullableInt32(reader, 7),
                    GetNullableInt32(reader, 8),
                    GetNullableInt32(reader, 9),
                    GetNullableInt32(reader, 10),
                    reader.GetValue(11)
                ];
                _ = table.Rows.Add(row);
            }

            _ = await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            return table;
        }

        private string BuildSchemaQuery(DbCommand command, string select, string[] restrictionColumns, string?[] restrictions, int startRestriction, string orderBy)
        {
            StringBuilder query = new(select);
            int parameterOrdinal = 0;
            for (int restrictionIndex = startRestriction; restrictionIndex < restrictions.Length; restrictionIndex++)
            {
                string? value = restrictions[restrictionIndex];
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                _ = query.Append(parameterOrdinal == 0 ? " WHERE " : " AND ")
                    .Append(restrictionColumns[restrictionIndex - startRestriction])
                    .Append(" = @restriction")
                    .Append(parameterOrdinal.ToString(CultureInfo.InvariantCulture));
                _ = command.Parameters.Add(new CassieParameter
                {
                    ParameterName = $"restriction{parameterOrdinal}",
                    Value = value,
                    PostgresTypeOid = 25
                });
                parameterOrdinal++;
            }

            _ = query.Append(" ORDER BY ").Append(orderBy);
            return query.ToString();
        }

        private bool MatchesCurrentCatalog(string? catalog)
        {
            return string.IsNullOrEmpty(catalog) || string.Equals(catalog, GetCurrentCatalog(), StringComparison.OrdinalIgnoreCase);
        }

        private string GetCurrentCatalog()
        {
            CassieConnectionStringBuilder settings = new(_connectionString);
            return string.IsNullOrEmpty(settings.Database) ? settings.Username : settings.Database;
        }

        private static string?[] ValidateRestrictions(string collectionName, string?[]? restrictions)
        {
            int restrictionCount = SchemaCollections.First(item => item.Name.Equals(collectionName, StringComparison.OrdinalIgnoreCase)).Restrictions;
            if (restrictions is not null && restrictions.Length > restrictionCount)
            {
                throw new ArgumentException($"Schema collection '{collectionName}' accepts at most {restrictionCount} restrictions.", nameof(restrictions));
            }

            string?[] normalized = new string?[restrictionCount];
            restrictions?.CopyTo(normalized, 0);
            return normalized;
        }

        private static DataTable CreateSchemaTable(string name)
        {
            return new DataTable(name) { Locale = CultureInfo.InvariantCulture };
        }

        private static void AddStringColumns(DataTable table, params string[] names)
        {
            foreach (string name in names)
            {
                _ = table.Columns.Add(name, typeof(string));
            }
        }

        private static void AddIntColumns(DataTable table, params string[] names)
        {
            foreach (string name in names)
            {
                _ = table.Columns.Add(name, typeof(int));
            }
        }

        private static void AddRestrictions(DataTable table, string collectionName, string[] names)
        {
            for (int index = 0; index < names.Length; index++)
            {
                _ = table.Rows.Add(collectionName, names[index], null, index + 1);
            }
        }

        private static object? GetNullableValue(DbDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? DBNull.Value : reader.GetValue(ordinal);
        }

        private static object GetNullableInt32(DbDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? DBNull.Value : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }
    }
}
