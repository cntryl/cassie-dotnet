using System.Text.Json.Serialization;

namespace Cntryl.Cassie.Rest
{
    /// <summary>A database catalog object and its reported columns and metadata.</summary>
    public sealed record CassieCatalogItem(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("database")] string Database,
        [property: JsonPropertyName("schema")] string Schema,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("columns")] IReadOnlyList<CassieCatalogColumn> Columns,
        [property: JsonPropertyName("metadata")] string? Metadata = null);
}
