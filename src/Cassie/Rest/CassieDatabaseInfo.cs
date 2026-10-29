using System.Text.Json.Serialization;

namespace Cntryl.Cassie.Rest
{
    /// <summary>A database visible to the authenticated REST session.</summary>
    public sealed record CassieDatabaseInfo(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string? Description = null);
}
