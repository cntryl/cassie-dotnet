using System.Text.Json.Serialization;

namespace Cntryl.Cassie.Rest
{
    /// <summary>The sections of the catalog in one database.</summary>
    public sealed record CassieCatalogSchema(
        [property: JsonPropertyName("sections")] IReadOnlyList<CassieCatalogSection> Sections);
}
