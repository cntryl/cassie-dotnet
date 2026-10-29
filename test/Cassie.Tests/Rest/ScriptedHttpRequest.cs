namespace Cntryl.Cassie.Tests.Rest
{
    internal sealed record ScriptedHttpRequest(string Method, string Target, IReadOnlyDictionary<string, string> Headers, string Body);
}
