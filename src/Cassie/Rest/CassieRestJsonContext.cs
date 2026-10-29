using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Cntryl.Cassie.Rest
{
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(CassieRestSession))]
    [JsonSerializable(typeof(CassieCollectionDefinition))]
    [JsonSerializable(typeof(CassieVectorIndexDefinition))]
    [JsonSerializable(typeof(CassieVectorIndexResult))]
    [JsonSerializable(typeof(CassieVectorSearchRequest))]
    [JsonSerializable(typeof(CassieRestQueryResult))]
    [JsonSerializable(typeof(string[]))]
    [JsonSerializable(typeof(JsonObject))]
    [JsonSerializable(typeof(RestLoginRequest))]
    [JsonSerializable(typeof(RestCollectionResponse))]
    [JsonSerializable(typeof(RestDocumentResponse))]
    [JsonSerializable(typeof(RestDeleteResponse))]
    [JsonSerializable(typeof(RestLogoutResponse))]
    [JsonSerializable(typeof(RestErrorResponse))]
    [JsonSerializable(typeof(CassieDatabaseInfo[]))]
    [JsonSerializable(typeof(CassieDatabaseInfo))]
    [JsonSerializable(typeof(CassieDatabaseCreateRequest))]
    [JsonSerializable(typeof(CassieAdminQueryRequest))]
    [JsonSerializable(typeof(CassieCatalogSchema))]
    [JsonSerializable(typeof(CassieQueryValidationResult))]
    [JsonSerializable(typeof(CassieQueryExplanation))]
    [JsonSerializable(typeof(CassieQueryCancellation))]
    [JsonSerializable(typeof(CassieManifestExportOptions))]
    [JsonSerializable(typeof(CassieProjectionVerificationManifest))]
    [JsonSerializable(typeof(CassieManifestComparisonRequest))]
    [JsonSerializable(typeof(CassieProjectionConsistencyReport))]
    [JsonSerializable(typeof(CassieProjectionReportsResult))]
    internal sealed partial class CassieRestJsonContext : JsonSerializerContext
    {
    }
}
