namespace Cntryl.Cassie
{
    /// <summary>
    /// Creates the exceptions that ADO.NET documents for lookups by a name that does not exist.
    /// <c>DbDataReader.GetOrdinal</c>, the reader's string indexer, and <c>DbParameterCollection</c>'s string
    /// indexer are all documented to throw <see cref="IndexOutOfRangeException"/>, and callers catch that type.
    /// Analyzer rule CA2201 reserves the type for the runtime, so the rule is turned off for this file alone
    /// (see .editorconfig); nothing else in the provider constructs it.
    /// </summary>
    internal static class AdoContractExceptions
    {
        internal static IndexOutOfRangeException ColumnNotFound(string name)
        {
            return new IndexOutOfRangeException($"Column '{name}' was not found.");
        }

        internal static IndexOutOfRangeException ParameterNotFound(string parameterName)
        {
            return new IndexOutOfRangeException($"Parameter '{parameterName}' was not found.");
        }
    }
}
