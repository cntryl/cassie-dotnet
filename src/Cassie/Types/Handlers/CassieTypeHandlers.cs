using System.Collections.Frozen;

namespace Cntryl.Cassie.Types.Handlers;

/// <summary>
///     Resolves the built-in handler for a PostgreSQL type OID. Registration is explicit, so it is trimming and AOT
///     safe.
/// </summary>
internal static class CassieTypeHandlers
{
    private static readonly FrozenDictionary<int, CassieTypeHandler> ByOid = Build();

    // Types whose GetFieldValue<T> conversion differs from a plain cast of the boxed value.
    private static readonly FrozenDictionary<Type, object> FieldReaders = new Dictionary<Type, object>
    {
        [typeof(decimal)] = new DelegateFieldReader<decimal>(static (reader, ordinal) => reader.GetDecimal(ordinal)),
        [typeof(CassieTemporalValue)] =
            new DelegateFieldReader<CassieTemporalValue>(static (reader, ordinal) => reader.GetTemporalValue(ordinal)),
        [typeof(DateTime)] = new DelegateFieldReader<DateTime>(static (reader, ordinal) => reader.GetDateTime(ordinal)),
        [typeof(TimeSpan)] =
            new DelegateFieldReader<TimeSpan>(static (reader, ordinal) => reader.GetTimeSpanValue(ordinal)),
        [typeof(ulong)] = new DelegateFieldReader<ulong>(static (reader, ordinal) => reader.GetUInt64Value(ordinal))
    }.ToFrozenDictionary();

    /// <summary>Returns the handler for a scalar or vector OID, or null when the type has no special conversion.</summary>
    internal static CassieTypeHandler? Find(int oid)
    {
        return ByOid.GetValueOrDefault(oid);
    }

    /// <summary>Returns the reader for a CLR type that needs a conversion beyond casting the boxed column value, or null.</summary>
    internal static CassieFieldReader<T>? FindFieldReader<T>()
    {
        return FieldReaders.GetValueOrDefault(typeof(T)) as CassieFieldReader<T>;
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
        Register(handlers, new NetworkAddressHandler(), 650, 869);
        Register(handlers, new MacAddressHandler(), 774, 829);
        Register(handlers, new BitStringHandler(), 1560, 1562);
        Register(handlers, new LsnHandler(), 3220);
        Register(handlers, new StringHandler(), 18, 19, 25, 114, 1042, 1043);
        Register(handlers, new JsonbHandler(), 3802);
        Register(handlers, new JsonPathHandler(), 4072);
        Register(handlers, new VoidHandler(), 2278);
        Register(handlers, new ObjectIdentifierHandler(), [.. CassieTypeMap.ObjectIdentifierOids]);
        return handlers.ToFrozenDictionary();
    }

    private static void Register(
        Dictionary<int, CassieTypeHandler> handlers,
        CassieTypeHandler handler,
        params int[] oids
    )
    {
        foreach (var oid in oids)
        {
            handlers.Add(oid, handler);
        }
    }
}
