using System.Text.Json.Serialization;

namespace Cntryl.Cassie.Rest
{
    /// <summary>The digest and verification state reported for one projection row.</summary>
    public sealed record CassieProjectionManifestRowHashSummary(
        [property: JsonPropertyName("row_id")] string RowId,
        [property: JsonPropertyName("digest")] string Digest,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("computed_ms")] ulong ComputedMs);
}
