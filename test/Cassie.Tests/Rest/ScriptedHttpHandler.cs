using System.Net;
using System.Text;

namespace Cntryl.Cassie.Tests.Rest
{
    internal sealed class ScriptedHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private int _requests;

        internal int Requests => Volatile.Read(ref _requests);
        internal bool IsDisposed { get; private set; }

        internal static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _requests);
            return respond(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
