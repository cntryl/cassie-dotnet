using System.Net;

namespace Cntryl.Cassie.Rest
{
    /// <summary>An unsuccessful REST response, preserving its HTTP status and server error text.</summary>
    public sealed class CassieRestException(HttpStatusCode statusCode, string error) : HttpRequestException(error, null, statusCode)
    {
        /// <summary>Gets the server's error text, or the HTTP reason when no valid error envelope was returned.</summary>
        public string Error { get; } = error;
    }
}
