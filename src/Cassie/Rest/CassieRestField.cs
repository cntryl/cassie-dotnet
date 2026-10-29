using System.Text.Json.Serialization;

namespace Cntryl.Cassie.Rest
{
    /// <summary>A collection field using a Cassie SQL type name such as <c>text</c> or <c>vector(3)</c>.</summary>
    public sealed record CassieRestField(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("type")] string DataType);
}
