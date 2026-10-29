using System.Text.Json.Serialization;

namespace Cntryl.Cassie.Rest
{
    /// <summary>A column described by the REST database catalog.</summary>
    public sealed record CassieCatalogColumn(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("data_type")] string DataType,
        [property: JsonPropertyName("primary_key")] bool PrimaryKey);
}
