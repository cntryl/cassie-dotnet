using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Cntryl.Cassie.Rest
{
    /// <summary>A query explanation with positional rows and the server's versioned plan representation.</summary>
    public sealed record CassieQueryExplanation(
        [property: JsonPropertyName("columns")] IReadOnlyList<CassieRestColumn> Columns,
        [property: JsonPropertyName("rows")] IReadOnlyList<IReadOnlyList<JsonElement>> Rows,
        [property: JsonPropertyName("command")] string Command,
        [property: JsonPropertyName("plan")] JsonObject Plan);
}
