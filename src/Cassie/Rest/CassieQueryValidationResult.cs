using System.Text.Json.Serialization;

namespace Cntryl.Cassie.Rest
{
    /// <summary>The description of a successfully validated SQL statement. Invalid SQL produces a REST error.</summary>
    public sealed record CassieQueryValidationResult(
        [property: JsonPropertyName("valid")] bool Valid,
        [property: JsonPropertyName("command")] string Command,
        [property: JsonPropertyName("columns")] IReadOnlyList<CassieRestColumn>? Columns = null);
}
