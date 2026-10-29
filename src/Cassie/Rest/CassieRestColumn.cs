using System.Text.Json.Serialization;

namespace Cntryl.Cassie.Rest
{
    /// <summary>Describes a column in a Cassie REST query or vector-search result.</summary>
    public sealed record CassieRestColumn(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("data_type")] string DataType,
        [property: JsonPropertyName("type_oid")] long TypeOid,
        [property: JsonPropertyName("typlen")] short TypeLength,
        [property: JsonPropertyName("atttypmod")] int TypeModifier,
        [property: JsonPropertyName("format_code")] short FormatCode,
        [property: JsonPropertyName("nullable")] bool IsNullable);
}
