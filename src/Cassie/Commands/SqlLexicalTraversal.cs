namespace Cntryl.Cassie.Commands
{
    /// <summary>Traverses SQL once, sharing literal/comment boundaries across marker and function detection.</summary>
    internal static class SqlLexicalTraversal
    {
        public static IEnumerable<SqlToken> Read(string sql)
        {
            ArgumentNullException.ThrowIfNull(sql);
            int index = 0;
            while (index < sql.Length)
            {
                char current = sql[index];
                char next = index + 1 < sql.Length ? sql[index + 1] : '\0';
                if (char.IsWhiteSpace(current))
                {
                    index++;
                    continue;
                }

                if (current == '-' && next == '-')
                {
                    while (index < sql.Length && sql[index] is not ('\r' or '\n'))
                    {
                        index++;
                    }

                    continue;
                }

                if (current == '/' && next == '*')
                {
                    SkipBlockComment(sql, ref index);
                    continue;
                }

                int start = index;
                SqlTokenKind kind;
                if (current is '\'' or '"')
                {
                    bool escapes = current == '\'' && IsEscapeStringPrefix(sql, index);
                    SkipQuoted(sql, ref index, current, escapes);
                    kind = current == '"' ? SqlTokenKind.QuotedIdentifier : SqlTokenKind.Literal;
                }
                else if (current == '$' && TryReadDollarQuoteDelimiter(sql, index, out string delimiter))
                {
                    int end = sql.IndexOf(delimiter, index + delimiter.Length, StringComparison.Ordinal);
                    index = end < 0 ? sql.Length : end + delimiter.Length;
                    kind = SqlTokenKind.Literal;
                }
                else if ((current == '@' || current == '<') && next == '@')
                {
                    index += 2;
                    while (index < sql.Length && sql[index] == '@')
                    {
                        index++;
                    }

                    kind = SqlTokenKind.Symbol;
                }
                else if (current == '@' && IsIdentifierStart(next))
                {
                    index += 2;
                    SkipIdentifierTail(sql, ref index);
                    kind = SqlTokenKind.Parameter;
                }
                else if (IsIdentifierStart(current))
                {
                    index++;
                    SkipIdentifierTail(sql, ref index);
                    kind = SqlTokenKind.Identifier;
                }
                else
                {
                    index++;
                    kind = SqlTokenKind.Symbol;
                }

                yield return new SqlToken(kind, start, index - start);
            }
        }

        public static bool ContainsFunctionCall(string sql, string functionName)
        {
            bool expectingParenthesis = false;
            foreach (SqlToken token in Read(sql))
            {
                ReadOnlySpan<char> text = sql.AsSpan(token.Start, token.Length);
                if (expectingParenthesis && token.Kind == SqlTokenKind.Symbol && text.SequenceEqual("("))
                {
                    return true;
                }

                expectingParenthesis = token.Kind switch
                {
                    SqlTokenKind.Identifier => text.Equals(functionName, StringComparison.OrdinalIgnoreCase),
                    SqlTokenKind.QuotedIdentifier => text.Length == functionName.Length + 2
                        && text[1..^1].Equals(functionName, StringComparison.Ordinal),
                    _ => false
                };
            }

            return false;
        }

        private static void SkipBlockComment(string sql, ref int index)
        {
            int depth = 1;
            index += 2;
            while (index < sql.Length && depth > 0)
            {
                char current = sql[index++];
                char next = index < sql.Length ? sql[index] : '\0';
                if (current == '/' && next == '*')
                {
                    depth++;
                    index++;
                }
                else if (current == '*' && next == '/')
                {
                    depth--;
                    index++;
                }
            }
        }

        private static void SkipQuoted(string sql, ref int index, char delimiter, bool escapes)
        {
            index++;
            while (index < sql.Length)
            {
                char current = sql[index++];
                if (current == delimiter)
                {
                    if (index >= sql.Length || sql[index] != delimiter)
                    {
                        return;
                    }

                    index++;
                }
                else if (current == '\\' && escapes && index < sql.Length)
                {
                    index++;
                }
            }
        }

        private static void SkipIdentifierTail(string sql, ref int index)
        {
            while (index < sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index] is '_' or '$'))
            {
                index++;
            }
        }

        private static bool IsIdentifierStart(char value)
        {
            return char.IsLetter(value) || value == '_';
        }

        private static bool IsEscapeStringPrefix(string sql, int quoteIndex)
        {
            return quoteIndex > 0 && sql[quoteIndex - 1] is 'e' or 'E'
                && (quoteIndex < 2 || !(char.IsLetterOrDigit(sql[quoteIndex - 2]) || sql[quoteIndex - 2] is '_' or '$'));
        }

        private static bool TryReadDollarQuoteDelimiter(string sql, int start, out string delimiter)
        {
            delimiter = string.Empty;
            int index = start + 1;
            if (index < sql.Length && sql[index] == '$')
            {
                delimiter = "$$";
                return true;
            }

            if (index >= sql.Length || !IsIdentifierStart(sql[index]))
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
    }
}
