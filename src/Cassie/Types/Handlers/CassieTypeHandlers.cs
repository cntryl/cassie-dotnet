using System.Collections.Frozen;

namespace Cntryl.Cassie.Types.Handlers
{
    /// <summary>Resolves the built-in handler for a PostgreSQL type OID. Registration is explicit, so it is trimming and AOT safe.</summary>
    internal static class CassieTypeHandlers
    {
        private static readonly FrozenDictionary<int, CassieTypeHandler> ByOid = Build();

        /// <summary>Returns the handler for a scalar OID, or null when the type has no special conversion.</summary>
        internal static CassieTypeHandler? Find(int oid)
        {
            return ByOid.GetValueOrDefault(oid);
        }

        private static FrozenDictionary<int, CassieTypeHandler> Build()
        {
            Dictionary<int, CassieTypeHandler> handlers = [];
            Register(handlers, new BooleanHandler(), 16);
            Register(handlers, new ByteaHandler(), 17);
            Register(handlers, new Int8Handler(), 20);
            Register(handlers, new Int2Handler(), 21);
            Register(handlers, new Int4Handler(), 23);
            Register(handlers, new Float4Handler(), 700);
            Register(handlers, new Float8Handler(), 701);
            Register(handlers, new UuidHandler(), 2950);
            Register(handlers, new NumericHandler(), 1700);
            Register(handlers, new DateHandler(), 1082);
            Register(handlers, new TimeHandler(), 1083);
            Register(handlers, new TimestampHandler(), 1114);
            Register(handlers, new TimestampTzHandler(), 1184);
            Register(handlers, new TimeTzHandler(), 1266);
            Register(handlers, new IntervalHandler(), 1186);
            Register(handlers, new StringHandler(), 18, 19, 25, 114, 1042, 1043);
            Register(handlers, new JsonbHandler(), 3802);
            Register(handlers, new JsonPathHandler(), 4072);
            Register(handlers, new VoidHandler(), 2278);
            Register(handlers, new ObjectIdentifierHandler(), [.. CassieTypeMap.ObjectIdentifierOids]);
            return handlers.ToFrozenDictionary();
        }

        private static void Register(Dictionary<int, CassieTypeHandler> handlers, CassieTypeHandler handler, params int[] oids)
        {
            foreach (int oid in oids)
            {
                handlers.Add(oid, handler);
            }
        }
    }
}
