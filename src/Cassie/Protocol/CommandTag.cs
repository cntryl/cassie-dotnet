using System.Globalization;
using System.Text;

namespace Cntryl.Cassie.Protocol;

/// <summary>A CommandComplete tag such as <c>INSERT 0 3</c> or <c>DISCARD ALL</c>.</summary>
internal readonly record struct CommandTag(string Text)
{
    private static readonly string[] RecordCountingVerbs =
        ["INSERT", "UPDATE", "DELETE", "MERGE", "COPY", "MOVE", "FETCH"];

    /// <summary>Gets the leading word of the tag.</summary>
    public ReadOnlySpan<char> Verb
    {
        get
        {
            var text = Text.AsSpan().TrimStart();
            var separator = text.IndexOf(' ');
            return separator < 0 ? text : text[..separator];
        }
    }

    /// <summary>Gets whether the trailing count of this tag is the number of rows affected.</summary>
    public bool CountsRecords
    {
        get
        {
            foreach (var verb in RecordCountingVerbs)
            {
                if (Verb.SequenceEqual(verb))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Gets the trailing row count, or zero when the tag has none or it does not fit an <see cref="int" />.</summary>
    public int RowCount
    {
        get
        {
            var lastSpace = Text.LastIndexOf(' ', StringComparison.Ordinal);
            return lastSpace > 0
                   && long.TryParse(Text.AsSpan(lastSpace + 1), NumberStyles.None, CultureInfo.InvariantCulture,
                       out var count)
                   && count <= int.MaxValue
                ? (int)count
                : 0;
        }
    }

    public static CommandTag FromPayload(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new CommandTag(Encoding.UTF8.GetString(payload).TrimEnd('\0'));
    }

    public bool HasVerb(string verb)
    {
        return Verb.Equals(verb, StringComparison.OrdinalIgnoreCase);
    }

    public bool Matches(string tag)
    {
        return Text.Equals(tag, StringComparison.OrdinalIgnoreCase);
    }
}
