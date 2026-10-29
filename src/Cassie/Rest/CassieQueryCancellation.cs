using System.Text.Json.Serialization;

namespace Cntryl.Cassie.Rest
{
    /// <summary>Acknowledges cleanup of a registered SQL operation belonging to the same REST session.</summary>
    public sealed record CassieQueryCancellation(
        [property: JsonPropertyName("operation_id")] Guid OperationId,
        [property: JsonPropertyName("cancelled")] bool Cancelled);
}
