namespace Cntryl.Cassie.Commands
{
    /// <summary>Finds named parameter markers while ignoring SQL strings, identifiers, and comments.</summary>
    public static class SqlParameterScanner
    {
        /// <summary>Returns distinct parameter names in their first-seen order.</summary>
        /// <param name="sql">SQL text to scan.</param>
        /// <returns>Parameter names without the leading at-sign.</returns>
        public static IReadOnlyList<string> FindParameterNames(string sql)
        {
            ArgumentNullException.ThrowIfNull(sql);
            List<string> names = [];
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            foreach (SqlToken token in SqlLexicalTraversal.Read(sql))
            {
                if (token.Kind != SqlTokenKind.Parameter)
                {
                    continue;
                }

                string name = sql.Substring(token.Start + 1, token.Length - 1);
                if (seen.Add(name))
                {
                    names.Add(name);
                }
            }

            return names;
        }
    }
}
