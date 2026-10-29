using System.Text.Json.Serialization;

namespace Cntryl.Cassie.Rest
{
    /// <summary>The server's vector-index metadata and its <c>created</c> or <c>exists</c> status.</summary>
    public sealed record CassieVectorIndexResult(
        [property: JsonPropertyName("collection")] string Collection,
        [property: JsonPropertyName("field")] string Field,
        [property: JsonPropertyName("source_field")] string SourceField,
        [property: JsonPropertyName("provider")] string Provider,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("dimensions")] int Dimensions,
        [property: JsonPropertyName("metric")] string Metric,
        [property: JsonPropertyName("index_type")] string IndexType,
        [property: JsonPropertyName("status")] string Status);
}
