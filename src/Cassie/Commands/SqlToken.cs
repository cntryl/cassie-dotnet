namespace Cntryl.Cassie.Commands
{
    /// <summary>A token's location in the original SQL, allowing callers to preserve all surrounding text.</summary>
    internal readonly record struct SqlToken(SqlTokenKind Kind, int Start, int Length);
}
