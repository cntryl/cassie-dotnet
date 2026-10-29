using System.Text.Json.Serialization;

namespace Cntryl.Cassie.Rest
{
    /// <summary>Defines a vector index. Option values are strings, including numeric tuning values.</summary>
    /// <remarks><c>source_field</c> is required. The server selects the embedding provider and model.</remarks>
    public sealed record CassieVectorIndexDefinition(
        [property: JsonPropertyName("field")] string Field,
        [property: JsonPropertyName("options")] IReadOnlyDictionary<string, string> Options,
        [property: JsonPropertyName("kind")] string Kind = "vector");
}
