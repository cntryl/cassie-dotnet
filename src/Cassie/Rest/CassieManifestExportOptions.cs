using System.Text.Json.Serialization;

namespace Cntryl.Cassie.Rest
{
    /// <summary>Options for exporting a projection verification manifest. Omitted values use server defaults.</summary>
    public sealed record CassieManifestExportOptions(
        [property: JsonPropertyName("instance_id")] string? InstanceId = null,
        [property: JsonPropertyName("generated_ms")] ulong? GeneratedMs = null,
        [property: JsonPropertyName("ttl_ms")] ulong? TtlMs = null,
        [property: JsonPropertyName("include_row_hashes")] bool IncludeRowHashes = false);
}
