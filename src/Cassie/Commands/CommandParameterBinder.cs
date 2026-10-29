using Cntryl.Cassie.Connection;
using Cntryl.Cassie.Types;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;

namespace Cntryl.Cassie.Commands
{
    /// <summary>Validates bindings and translates command text into one positional parameter set.</summary>
    internal static class CommandParameterBinder
    {
        public static CassiePreparedCommand Prepare(string sql, CommandType commandType, DbParameterCollection parameters)
        {
            if (commandType == CommandType.StoredProcedure)
            {
                return PrepareStoredProcedure(sql, parameters);
            }

            if (commandType != CommandType.Text)
            {
                throw new NotSupportedException($"Cassie does not support command type '{commandType}'.");
            }

            CassieConnection.ValidateSqlText(sql);

            if (parameters.Count == 0 && sql.AsSpan().IndexOf('@') < 0)
            {
                return new CassiePreparedCommand(
                    sql,
                    [],
                    [],
                    [],
                    []);
            }

            if (parameters.Count == 1)
            {
                return PrepareSingleParameter(sql, parameters[0]);
            }

            Dictionary<string, DbParameter> bindings = new(StringComparer.OrdinalIgnoreCase);
            foreach (DbParameter parameter in parameters)
            {
                if (parameter.Direction != ParameterDirection.Input)
                {
                    throw new NotSupportedException("Cassie parameters are input-only; CALL output parameters are not supported.");
                }

                string name = NormalizeParameterName(parameter.ParameterName);
                if (name.Length == 0)
                {
                    throw new InvalidOperationException("A parameter name cannot be empty.");
                }

                if (!bindings.TryAdd(name, parameter))
                {
                    throw new InvalidOperationException($"Parameter '{name}' is bound more than once.");
                }
            }

            List<string> names = [];
            List<object?> values = [];
            List<int> typeOids = [];
            List<CassieParameterFormat> parameterFormats = [];
            Dictionary<string, int> slots = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
            string rewritten = Rewrite(sql, name =>
            {
                _ = used.Add(name);
                if (!bindings.TryGetValue(name, out DbParameter? parameter))
                {
                    throw new InvalidOperationException($"SQL parameter '{name}' has no matching binding.");
                }

                if (!slots.TryGetValue(name, out int slot))
                {
                    slot = names.Count + 1;
                    slots.Add(name, slot);
                    names.Add(name);
                    values.Add(parameter.Value is DBNull ? null : parameter.Value);
                    typeOids.Add(CassieTypeMap.GetParameterOid(parameter));
                    parameterFormats.Add(parameter is CassieParameter cassieParameter ? cassieParameter.ParameterFormat : CassieParameterFormat.Auto);
                }

                return "$" + slot.ToString(CultureInfo.InvariantCulture);
            });

            string[] unused = [.. bindings.Keys.Where(name => !used.Contains(name))];
            return unused.Length > 0
                ? throw new InvalidOperationException($"Parameter binding(s) are not used by SQL: {string.Join(", ", unused)}.")
                : new CassiePreparedCommand(rewritten, names.AsReadOnly(), values.AsReadOnly(), typeOids.AsReadOnly(), parameterFormats.AsReadOnly());
        }

        private static CassiePreparedCommand PrepareSingleParameter(string sql, DbParameter parameter)
        {
            if (parameter.Direction != ParameterDirection.Input)
            {
                throw new NotSupportedException("Cassie parameters are input-only; CALL output parameters are not supported.");
            }

            string name = NormalizeParameterName(parameter.ParameterName);
            if (name.Length == 0)
            {
                throw new InvalidOperationException("A parameter name cannot be empty.");
            }

            bool used = false;
            string rewritten = Rewrite(sql, marker =>
            {
                if (!marker.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"SQL parameter '{marker}' has no matching binding.");
                }

                used = true;
                return "$1";
            });
            if (!used)
            {
                throw new InvalidOperationException($"Parameter binding(s) are not used by SQL: {name}.");
            }

            object? value = parameter.Value is DBNull ? null : parameter.Value;
            int typeOid = CassieTypeMap.GetParameterOid(parameter);
            CassieParameterFormat format = parameter is CassieParameter cassieParameter
                ? cassieParameter.ParameterFormat
                : CassieParameterFormat.Auto;
            return new CassiePreparedCommand(rewritten, [name], [value], [typeOid], [format]);
        }

        private static CassiePreparedCommand PrepareStoredProcedure(string sqlText, DbParameterCollection parameters)
        {
            string procedureName = ProcedureName.Quote(sqlText);
            List<string> names = [];
            List<object?> values = [];
            List<int> typeOids = [];
            List<CassieParameterFormat> parameterFormats = [];
            StringBuilder sql = new();
            _ = sql.Append("CALL ").Append(procedureName).Append('(');

            foreach (DbParameter parameter in parameters)
            {
                if (parameter.Direction != ParameterDirection.Input)
                {
                    throw new NotSupportedException("Cassie parameters are input-only; CALL output parameters are not supported.");
                }

                if (names.Count > 0)
                {
                    _ = sql.Append(", ");
                }

                _ = sql.Append('$').Append((names.Count + 1).ToString(CultureInfo.InvariantCulture));
                string parameterName = NormalizeParameterName(parameter.ParameterName ?? string.Empty);
                names.Add(parameterName.Length == 0 ? (names.Count + 1).ToString(CultureInfo.InvariantCulture) : parameterName);
                values.Add(parameter.Value is DBNull ? null : parameter.Value);
                typeOids.Add(CassieTypeMap.GetParameterOid(parameter));
                parameterFormats.Add(parameter is CassieParameter cassieParameter ? cassieParameter.ParameterFormat : CassieParameterFormat.Auto);
            }

            _ = sql.Append(')');
            return new CassiePreparedCommand(sql.ToString(), names.AsReadOnly(), values.AsReadOnly(), typeOids.AsReadOnly(), parameterFormats.AsReadOnly());
        }

        private static string NormalizeParameterName(string name)
        {
            return name.Trim().TrimStart('@', ':');
        }

        private static string Rewrite(string sql, Func<string, string> replace)
        {
            StringBuilder result = new(sql.Length);
            int copiedThrough = 0;
            foreach (SqlToken token in SqlLexicalTraversal.Read(sql))
            {
                if (token.Kind != SqlTokenKind.Parameter)
                {
                    continue;
                }

                _ = result.Append(sql.AsSpan(copiedThrough, token.Start - copiedThrough));
                _ = result.Append(replace(sql.Substring(token.Start + 1, token.Length - 1)));
                copiedThrough = token.Start + token.Length;
            }

            _ = result.Append(sql.AsSpan(copiedThrough));
            return result.ToString();
        }
    }
}
