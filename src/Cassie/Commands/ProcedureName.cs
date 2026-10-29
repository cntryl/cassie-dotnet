using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Cntryl.Cassie.Commands
{
    /// <summary>Parses a schema-qualified procedure name and quotes each identifier safely.</summary>
    internal static class ProcedureName
    {
        [SuppressMessage("Globalization", "CA1308", Justification = "PostgreSQL folds unquoted procedure identifiers to lowercase before resolution.")]
        public static string Quote(string commandText)
        {
            if (string.IsNullOrWhiteSpace(commandText))
            {
                throw InvalidProcedureName();
            }

            List<string> components = [];
            int index = 0;
            while (true)
            {
                SkipProcedureNameWhitespace(commandText, ref index);
                if (index >= commandText.Length || components.Count == 2)
                {
                    throw InvalidProcedureName();
                }

                string component;
                if (commandText[index] == '"')
                {
                    index++;
                    StringBuilder quoted = new();
                    bool closed = false;
                    while (index < commandText.Length)
                    {
                        char current = commandText[index++];
                        if (current == '\0')
                        {
                            throw InvalidProcedureName();
                        }

                        if (current != '"')
                        {
                            _ = quoted.Append(current);
                            continue;
                        }

                        if (index < commandText.Length && commandText[index] == '"')
                        {
                            _ = quoted.Append('"');
                            index++;
                            continue;
                        }

                        closed = true;
                        break;
                    }

                    if (!closed || quoted.Length == 0)
                    {
                        throw InvalidProcedureName();
                    }

                    component = quoted.ToString();
                }
                else
                {
                    int start = index;
                    if (!IsProcedureIdentifierStart(commandText[index]))
                    {
                        throw InvalidProcedureName();
                    }

                    index++;
                    while (index < commandText.Length && IsProcedureIdentifierPart(commandText[index]))
                    {
                        index++;
                    }

                    component = commandText[start..index].ToLowerInvariant();
                }

                components.Add(component);
                SkipProcedureNameWhitespace(commandText, ref index);
                if (index == commandText.Length)
                {
                    break;
                }

                if (commandText[index++] != '.')
                {
                    throw InvalidProcedureName();
                }
            }

            return string.Join('.', components.Select(component => $"\"{component.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));
        }

        private static bool IsProcedureIdentifierStart(char value)
        {
            return char.IsLetter(value) || value == '_';
        }

        private static bool IsProcedureIdentifierPart(char value)
        {
            return char.IsLetterOrDigit(value) || value is '_' or '$';
        }

        private static void SkipProcedureNameWhitespace(string value, ref int index)
        {
            while (index < value.Length && char.IsWhiteSpace(value[index]))
            {
                index++;
            }
        }

        private static ArgumentException InvalidProcedureName()
        {
            return new ArgumentException("CommandText must be a procedure name, optionally qualified by schema.", "CommandText");
        }

    }
}
