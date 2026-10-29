using System.Globalization;
using System.Text;

namespace Cntryl.Cassie.Protocol
{
    /// <summary>A CommandComplete tag such as <c>INSERT 0 3</c> or <c>DISCARD ALL</c>.</summary>
    internal readonly record struct CommandTag(string Text)
    {
        private static readonly string[] RecordCountingVerbs = ["INSERT", "UPDATE", "DELETE", "MERGE", "COPY", "MOVE", "FETCH"];

        public static CommandTag FromPayload(byte[] payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            return new CommandTag(Encoding.UTF8.GetString(payload).TrimEnd('\0'));
        }

        /// <summary>Gets the leading word of the tag.</summary>
        public ReadOnlySpan<char> Verb
        {
            get
            {
                ReadOnlySpan<char> text = Text.AsSpan().TrimStart();
                int separator = text.IndexOf(' ');
                return separator < 0 ? text : text[..separator];
            }
        }

        public bool HasVerb(string verb)
        {
            return Verb.Equals(verb, StringComparison.OrdinalIgnoreCase);
        }

        public bool Matches(string tag)
        {
            return Text.Equals(tag, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Gets whether the trailing count of this tag is the number of rows affected.</summary>
        public bool CountsRecords
        {
            get
            {
                foreach (string verb in RecordCountingVerbs)
                {
                    if (Verb.SequenceEqual(verb))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>Gets the trailing row count, or zero when the tag has none or it does not fit an <see cref="int"/>.</summary>
        public int RowCount
        {
            get
            {
                int lastSpace = Text.LastIndexOf(' ');
                return lastSpace > 0
                    && long.TryParse(Text.AsSpan(lastSpace + 1), NumberStyles.None, CultureInfo.InvariantCulture, out long count)
                    && count <= int.MaxValue
                        ? (int)count
                        : 0;
            }
        }
    }
}
