namespace Cntryl.Cassie.DependencyInjection.Tests.Infrastructure
{
    internal sealed record ScriptedHttpRequest(string Method, string Target, IReadOnlyDictionary<string, string> Headers, string Body);
}
