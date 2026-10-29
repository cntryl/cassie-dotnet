namespace Cntryl.Cassie
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
            ScanState state = ScanState.Normal;
            int blockCommentDepth = 0;
            string? dollarQuoteDelimiter = null;
            bool escapeString = false;

            for (int index = 0; index < sql.Length; index++)
            {
                char current = sql[index];
                char next = index + 1 < sql.Length ? sql[index + 1] : '\0';

                if (dollarQuoteDelimiter is not null)
                {
                    if (sql.AsSpan(index).StartsWith(dollarQuoteDelimiter, StringComparison.Ordinal))
                    {
                        index += dollarQuoteDelimiter.Length - 1;
                        dollarQuoteDelimiter = null;
                    }

                    continue;
                }

                if (state == ScanState.LineComment)
                {
                    if (current is '\r' or '\n')
                    {
                        state = ScanState.Normal;
                    }

                    continue;
                }

                if (state == ScanState.BlockComment)
                {
                    if (current == '/' && next == '*') { blockCommentDepth++; index++; }
                    else if (current == '*' && next == '/')
                    {
                        blockCommentDepth--;
                        index++;
                        if (blockCommentDepth == 0)
                        {
                            state = ScanState.Normal;
                        }
                    }
                    continue;
                }

                if (state is ScanState.SingleQuote or ScanState.DoubleQuote)
                {
                    char delimiter = state == ScanState.SingleQuote ? '\'' : '"';
                    if (current == delimiter)
                    {
                        if (next == delimiter)
                        {
                            index++;
                        }
                        else
                        {
                            state = ScanState.Normal;
                        }
                    }
                    else if (current == '\\' && state == ScanState.SingleQuote && escapeString && next != '\0')
                    {
                        index++;
                    }

                    continue;
                }

                if (current == '-' && next == '-') { state = ScanState.LineComment; index++; continue; }
                if (current == '/' && next == '*') { state = ScanState.BlockComment; blockCommentDepth = 1; index++; continue; }
                if (current == '\'') { escapeString = IsEscapeStringPrefix(sql, index); state = ScanState.SingleQuote; continue; }
                if (current == '"') { state = ScanState.DoubleQuote; continue; }
                if (current == '$' && TryReadDollarQuoteDelimiter(sql, index, out string dollarDelimiterToken))
                {
                    dollarQuoteDelimiter = dollarDelimiterToken;
                    index += dollarDelimiterToken.Length - 1;
                    continue;
                }
                if (current != '@' || next == '@' || !(char.IsLetter(next) || next == '_'))
                {
                    continue;
                }

                int start = index + 1;
                index = start;
                while (index < sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index] is '_' or '$'))
                {
                    index++;
                }

                string name = sql[start..index];
                index--;
                if (seen.Add(name))
                {
                    names.Add(name);
                }
            }

            return names;
        }

        internal static bool TryReadDollarQuoteDelimiter(string sql, int start, out string delimiter)
        {
            delimiter = string.Empty;
            if ((uint)start >= (uint)sql.Length || sql[start] != '$')
            {
                return false;
            }

            int index = start + 1;
            if (index < sql.Length && sql[index] == '$')
            {
                delimiter = "$$";
                return true;
            }

            if (index >= sql.Length || !(char.IsLetter(sql[index]) || sql[index] == '_'))
            {
                return false;
            }

            while (index < sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index] == '_'))
            {
                index++;
            }

            if (index >= sql.Length || sql[index] != '$')
            {
                return false;
            }

            delimiter = sql[start..(index + 1)];
            return true;
        }

        internal static bool IsEscapeStringPrefix(string sql, int quoteIndex)
        {
            return quoteIndex > 0 && sql[quoteIndex - 1] is 'e' or 'E' &&
                (quoteIndex < 2 || !(char.IsLetterOrDigit(sql[quoteIndex - 2]) || sql[quoteIndex - 2] is '_' or '$'));
        }

        private enum ScanState { Normal, SingleQuote, DoubleQuote, LineComment, BlockComment }
    }
}
