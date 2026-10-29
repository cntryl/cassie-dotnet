using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cntryl.Cassie.Rest
{
    /// <summary>Column metadata and positional JSON rows returned by Cassie REST queries and vector searches.</summary>
    public sealed record CassieRestQueryResult(
        [property: JsonPropertyName("columns")] IReadOnlyList<CassieRestColumn> Columns,
        [property: JsonPropertyName("rows")] IReadOnlyList<IReadOnlyList<JsonElement>> Rows,
        [property: JsonPropertyName("command")] string Command);
}
