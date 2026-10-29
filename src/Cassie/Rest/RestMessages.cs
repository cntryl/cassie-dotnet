namespace Cntryl.Cassie.Rest
{
    internal sealed record RestLoginRequest(string Username, string Password);
    internal sealed record RestCollectionResponse(string? Collection);
    internal sealed record RestDocumentResponse(string? Id);
    internal sealed record RestDeleteResponse(bool? Deleted);
    internal sealed record RestLogoutResponse(bool? LoggedOut);
    internal sealed record RestErrorResponse(string? Error);
}
