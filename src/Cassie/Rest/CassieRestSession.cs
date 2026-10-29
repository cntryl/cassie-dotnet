using System.Text.Json.Serialization;

namespace Cntryl.Cassie.Rest
{
    /// <summary>The authenticated identity reported by the Cassie REST session.</summary>
    public sealed record CassieRestSession(
        [property: JsonPropertyName("user")] string User,
        [property: JsonPropertyName("role")] string? Role = null);
}
