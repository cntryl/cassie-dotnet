namespace Cntryl.Cassie.Rest
{
    internal sealed record CassieDatabaseCreateRequest(string Name);
    internal sealed record CassieManifestComparisonRequest(IReadOnlyList<CassieProjectionVerificationManifest> Manifests);
    internal sealed record CassieProjectionReportsResult(IReadOnlyList<CassieProjectionConsistencyReport> Reports);
}
