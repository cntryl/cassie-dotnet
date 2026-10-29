namespace Cntryl.Cassie.DependencyInjection.Tests.Infrastructure
{
    internal sealed record ScriptedHttpReply(int StatusCode, string Body, IReadOnlyDictionary<string, string>? Headers = null);
}
