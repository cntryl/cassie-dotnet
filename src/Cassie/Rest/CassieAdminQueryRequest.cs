using System.Text.Json.Serialization;

namespace Cntryl.Cassie.Rest
{
    /// <summary>A database-scoped SQL operation, optionally registered for explicit cancellation.</summary>
    public sealed record CassieAdminQueryRequest(
        [property: JsonPropertyName("database")] string Database,
        [property: JsonPropertyName("sql")] string Sql,
        [property: JsonPropertyName("operation_id")] Guid? OperationId = null);
}
