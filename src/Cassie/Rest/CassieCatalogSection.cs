using System.Text.Json.Serialization;

namespace Cntryl.Cassie.Rest
{
    /// <summary>A labeled group of database catalog items.</summary>
    public sealed record CassieCatalogSection(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("items")] IReadOnlyList<CassieCatalogItem> Items);
}
