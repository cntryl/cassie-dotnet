namespace Cntryl.Cassie.Tests.Rest
{
    internal sealed record ScriptedHttpReply(int StatusCode, string Body, IReadOnlyDictionary<string, string>? Headers = null);
}
