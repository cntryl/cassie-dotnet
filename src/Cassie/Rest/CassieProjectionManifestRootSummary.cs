using System.Text.Json.Serialization;

namespace Cntryl.Cassie.Rest
{
    /// <summary>The root digest and coverage reported by a projection verification manifest.</summary>
    public sealed record CassieProjectionManifestRootSummary(
        [property: JsonPropertyName("digest")] string Digest,
        [property: JsonPropertyName("row_count")] ulong RowCount,
        [property: JsonPropertyName("range_count")] ulong RangeCount,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("computed_ms")] ulong ComputedMs);
}
