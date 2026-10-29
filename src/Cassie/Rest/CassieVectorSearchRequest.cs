using System.Text.Json.Serialization;

namespace Cntryl.Cassie.Rest
{
    /// <summary>Searches a vector field using query text embedded by the server's configured provider.</summary>
    /// <remarks>Omitted limits and offsets use server defaults. A zero limit returns an empty result.</remarks>
    public sealed record CassieVectorSearchRequest(
        [property: JsonPropertyName("field")] string Field,
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("metric")] string? Metric = null,
        [property: JsonPropertyName("limit")] int? Limit = null,
        [property: JsonPropertyName("offset")] int? Offset = null);
}
