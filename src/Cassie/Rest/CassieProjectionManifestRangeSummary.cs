using System.Text.Json.Serialization;

namespace Cntryl.Cassie.Rest
{
    /// <summary>The row range covered by one projection verification digest.</summary>
    public sealed record CassieProjectionManifestRangeSummary(
        [property: JsonPropertyName("range_id")] ulong RangeId,
        [property: JsonPropertyName("first_row_id")] string? FirstRowId,
        [property: JsonPropertyName("last_row_id")] string? LastRowId,
        [property: JsonPropertyName("row_count")] ulong RowCount,
        [property: JsonPropertyName("digest")] string Digest,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("computed_ms")] ulong ComputedMs);
}
