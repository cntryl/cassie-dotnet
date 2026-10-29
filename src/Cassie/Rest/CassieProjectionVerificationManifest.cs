using System.Text.Json.Serialization;

namespace Cntryl.Cassie.Rest
{
    /// <summary>A versioned projection verification manifest, including hash metadata and optional row digests.</summary>
    public sealed record CassieProjectionVerificationManifest(
        [property: JsonPropertyName("manifest_version")] ushort ManifestVersion,
        [property: JsonPropertyName("instance_id")] string InstanceId,
        [property: JsonPropertyName("projection_id")] string ProjectionId,
        [property: JsonPropertyName("projection_version_id")] string? ProjectionVersionId,
        [property: JsonPropertyName("projection_kind")] string ProjectionKind,
        [property: JsonPropertyName("schema_epoch")] ulong SchemaEpoch,
        [property: JsonPropertyName("projection_definition_hash")] ulong? ProjectionDefinitionHash,
        [property: JsonPropertyName("source_identity")] string? SourceIdentity,
        [property: JsonPropertyName("source_checkpoint")] string? SourceCheckpoint,
        [property: JsonPropertyName("source_position")] ulong? SourcePosition,
        [property: JsonPropertyName("generated_ms")] ulong GeneratedMs,
        [property: JsonPropertyName("expires_at_ms")] ulong ExpiresAtMs,
        [property: JsonPropertyName("hash")] CassieProjectionManifestHashMetadata Hash,
        [property: JsonPropertyName("root")] CassieProjectionManifestRootSummary? Root,
        [property: JsonPropertyName("ranges")] IReadOnlyList<CassieProjectionManifestRangeSummary> Ranges,
        [property: JsonPropertyName("row_hashes")] IReadOnlyList<CassieProjectionManifestRowHashSummary> RowHashes,
        [property: JsonPropertyName("manifest_digest")] string ManifestDigest);
}
