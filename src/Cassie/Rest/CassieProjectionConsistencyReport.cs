using System.Text.Json.Serialization;

namespace Cntryl.Cassie.Rest
{
    /// <summary>The server's comparison of projection manifests and its mismatch diagnostics.</summary>
    public sealed record CassieProjectionConsistencyReport(
        [property: JsonPropertyName("report_id")] string ReportId,
        [property: JsonPropertyName("created_ms")] ulong CreatedMs,
        [property: JsonPropertyName("projection_id")] string ProjectionId,
        [property: JsonPropertyName("projection_version_id")] string? ProjectionVersionId,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("compatibility_status")] string CompatibilityStatus,
        [property: JsonPropertyName("manifest_count")] ulong ManifestCount,
        [property: JsonPropertyName("instance_ids")] IReadOnlyList<string> InstanceIds,
        [property: JsonPropertyName("root_digest")] string? RootDigest,
        [property: JsonPropertyName("manifest_digest")] string? ManifestDigest,
        [property: JsonPropertyName("mismatch_count")] ulong MismatchCount,
        [property: JsonPropertyName("divergent_range_count")] ulong DivergentRangeCount,
        [property: JsonPropertyName("divergent_row_count")] ulong DivergentRowCount,
        [property: JsonPropertyName("stale_manifest_count")] ulong StaleManifestCount,
        [property: JsonPropertyName("incompatible_manifest_count")] ulong IncompatibleManifestCount,
        [property: JsonPropertyName("unverifiable_count")] ulong UnverifiableCount,
        [property: JsonPropertyName("diagnostic_sample")] IReadOnlyList<string> DiagnosticSample,
        [property: JsonPropertyName("last_error")] string? LastError);
}
