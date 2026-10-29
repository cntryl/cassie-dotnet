namespace Cntryl.Cassie.Commands
{
    internal enum SqlTokenKind
    {
        Parameter,
        Identifier,
        QuotedIdentifier,
        Literal,
        Symbol
    }
}
