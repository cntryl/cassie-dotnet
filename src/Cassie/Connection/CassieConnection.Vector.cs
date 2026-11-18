using System.Data;
using Cntryl.Cassie.Commands;
using Cntryl.Cassie.Types;

namespace Cntryl.Cassie.Connection;

public sealed partial class CassieConnection
{
    internal const string VectorCatalogQuery = """
        SELECT t.oid
        FROM pg_catalog.pg_extension e
        JOIN pg_catalog.pg_depend d ON d.refobjid = e.oid
        JOIN pg_catalog.pg_type t ON t.oid = d.objid
        WHERE e.extname = 'vector' AND t.typname = 'vector'
          AND d.classid = 1247::oid AND d.refclassid = 3079::oid AND d.deptype = 'e'
        """;

    internal async Task<CassiePreparedCommand> ResolveVectorTypesAsync(CassiePreparedCommand prepared,
        bool describeResults, CommandBudget budget, CancellationToken cancellationToken)
    {
        int[]? resolvedOids = null;
        var oid = 0;
        for (var index = 0; index < prepared.ParameterTypeOids.Count; index++)
        {
            if (prepared.ParameterTypeOids[index] != CassieTypeMap.VectorParameterOid &&
                prepared.Values[index] is not CassieVector)
            {
                continue;
            }

            if (resolvedOids is null)
            {
                oid = await DiscoverVectorOidAsync(budget, cancellationToken).ConfigureAwait(false);
                if (oid == 0)
                {
                    throw new InvalidOperationException(
                        "Install the vector extension in the current database before binding a CassieVector.");
                }

                resolvedOids = [.. prepared.ParameterTypeOids];
            }

            resolvedOids[index] = oid;
        }

        if (describeResults && resolvedOids is null)
        {
            _ = await DiscoverVectorOidAsync(budget, cancellationToken).ConfigureAwait(false);
        }

        return resolvedOids is null ? prepared : prepared with { ParameterTypeOids = resolvedOids };
    }

    private async ValueTask<int> DiscoverVectorOidAsync(CommandBudget budget, CancellationToken cancellationToken)
    {
        var session = RequireSession("The connection is not open.");
        if (session.VectorOid is int cached)
        {
            return cached;
        }

        var reader = await ExecuteQueryCoreAsync(VectorCatalogQuery, budget, CommandBehavior.Default, null, false,
            cancellationToken).ConfigureAwait(false);
        var oid = 0;
        await using (reader.ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                oid = unchecked((int)await reader.GetFieldValueAsync<uint>(0, cancellationToken).ConfigureAwait(false));
                if (oid == 0 || await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new IOException("Cassie returned an invalid vector extension type OID.");
                }
            }

            _ = await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        }

        session.VectorOid = oid;
        return oid;
    }
}
