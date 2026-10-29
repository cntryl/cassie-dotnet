using System.Text.Json.Serialization;

namespace Cntryl.Cassie.Rest
{
    /// <summary>The algorithms and encoding versions used by a projection verification manifest.</summary>
    public sealed record CassieProjectionManifestHashMetadata(
        [property: JsonPropertyName("algorithm")] string Algorithm,
        [property: JsonPropertyName("digest_length")] ushort DigestLength,
        [property: JsonPropertyName("canonical_encoder_version")] ushort CanonicalEncoderVersion,
        [property: JsonPropertyName("row_hash_version")] ushort RowHashVersion,
        [property: JsonPropertyName("range_hash_version")] ushort RangeHashVersion,
        [property: JsonPropertyName("root_hash_version")] ushort RootHashVersion);
}
