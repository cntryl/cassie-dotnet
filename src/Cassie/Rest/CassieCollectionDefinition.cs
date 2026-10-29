using System.Text.Json.Serialization;

namespace Cntryl.Cassie.Rest
{
    /// <summary>Defines a collection in the authenticated session's database and schema.</summary>
    public sealed record CassieCollectionDefinition(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("fields")] IReadOnlyList<CassieRestField> Fields,
        [property: JsonPropertyName("description")] string? Description = null);
}
