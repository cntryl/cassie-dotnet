namespace Cntryl.Cassie
{
    /// <summary>Contains SQL rewritten for positional parameter binding.</summary>
    /// <param name="Sql">SQL text with named markers replaced by positional markers.</param>
    /// <param name="ParameterNames">Names in positional slot order.</param>
    /// <param name="Values">Values in positional slot order.</param>
    /// <param name="ParameterTypeOids">PostgreSQL type OIDs in positional slot order; zero means infer from SQL context.</param>
    /// <param name="ParameterFormats">Optional requested pgwire input formats in positional slot order.</param>
    public sealed record CassiePreparedCommand(string Sql, IReadOnlyList<string> ParameterNames, IReadOnlyList<object?> Values, IReadOnlyList<int> ParameterTypeOids, IReadOnlyList<CassieParameterFormat>? ParameterFormats = null);
}
