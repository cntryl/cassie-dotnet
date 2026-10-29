namespace Cntryl.Cassie.Connection
{
    /// <summary>A single execution's validated formats and encoded values, shared with auto-preparation.</summary>
    internal sealed record EncodedParameters(IReadOnlyList<short> Formats, IReadOnlyList<byte[]?> Values);
}
