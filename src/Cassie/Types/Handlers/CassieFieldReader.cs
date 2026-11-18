using Cntryl.Cassie.Data;

namespace Cntryl.Cassie.Types.Handlers;

/// <summary>Reads one column of the current row as <typeparamref name="T" /> without boxing.</summary>
/// <typeparam name="T">The requested CLR type.</typeparam>
internal abstract class CassieFieldReader<T>
{
    internal abstract T Read(CassieDataReader reader, int ordinal);
}

/// <summary>A field reader backed by a static conversion method of the reader.</summary>
/// <typeparam name="T">The requested CLR type.</typeparam>
internal sealed class DelegateFieldReader<T>(Func<CassieDataReader, int, T> read) : CassieFieldReader<T>
{
    internal override T Read(CassieDataReader reader, int ordinal)
    {
        return read(reader, ordinal);
    }
}
