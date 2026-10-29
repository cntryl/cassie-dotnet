using System.Diagnostics.CodeAnalysis;
using System.Net.NetworkInformation;

namespace Cntryl.Cassie
{
    internal static class CassieTypeMap
    {
        internal static int GetArrayParameterOid(Array array)
        {
            Type? elementType = array.GetType().GetElementType();
            if (elementType is null)
            {
                return 0;
            }

            Type type = Nullable.GetUnderlyingType(elementType) ?? elementType;
            return type == typeof(CassieNetworkAddress) ? GetArrayVariantOid(array, static value => ((CassieNetworkAddress)value).IsCidr ? 651 : 1041, "inet[] or cidr[]")
                : type == typeof(PhysicalAddress) ? GetArrayVariantOid(array, GetMacAddressArrayOid, "macaddr[] or macaddr8[]")
                : type == typeof(CassieBitString) ? GetArrayVariantOid(array, static value => ((CassieBitString)value).IsVarying ? 1563 : 1561, "bit[] or varbit[]")
                : type == typeof(CassieTemporalValue) ? GetArrayVariantOid(array, static value => GetArrayOid(((CassieTemporalValue)value).PostgresTypeOid) ?? throw new NotSupportedException("The temporal value has no registered PostgreSQL array type."), "temporal arrays")
                : type == typeof(bool) ? 1000
                : type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) ? 1005
                : type == typeof(int) || type == typeof(ushort) ? 1007
                : type == typeof(long) ? 1016
                : type == typeof(uint) ? 1028
                : type == typeof(float) ? 1021
                : type == typeof(double) ? 1022
                : type == typeof(byte[]) ? 1001
                : type == typeof(string) || type == typeof(char) ? 1009
                : type == typeof(Guid) ? 2951
                : type == typeof(ulong) || type == typeof(decimal) || type == typeof(CassieNumeric) ? 1231
                : type == typeof(DateOnly) ? 1182
                : type == typeof(TimeOnly) || type == typeof(TimeSpan) ? 1183
                : type == typeof(DateTime) ? 1115
                : type == typeof(DateTimeOffset) ? 1185
                : type == typeof(CassieTimeWithTimeZone) ? 1270
                : type == typeof(CassieInterval) ? 1187
                : type == typeof(CassieLsn) ? 3221
                : throw new NotSupportedException($"Array parameters with element type '{type.FullName}' are not supported.");
        }

        private static int GetArrayVariantOid(Array array, Func<object, int> getOid, string typeNames)
        {
            int? arrayOid = null;
            foreach (object? value in array)
            {
                if (value is null or DBNull)
                {
                    continue;
                }

                int valueOid = getOid(value);
                if (arrayOid is int priorOid && priorOid != valueOid)
                {
                    throw new ArgumentException($"Array parameters cannot mix values from {typeNames}.", nameof(array));
                }

                arrayOid = valueOid;
            }

            return arrayOid ?? throw new NotSupportedException($"The PostgreSQL type for an empty or NULL-only {typeNames} parameter cannot be inferred; set CassieParameter.PostgresTypeOid.");
        }

        private static int GetMacAddressArrayOid(object value)
        {
            return ((PhysicalAddress)value).GetAddressBytes().Length switch
            {
                6 => 1040,
                8 => 775,
                _ => throw new ArgumentException("MAC address parameters must contain six or eight bytes.", nameof(value))
            };
        }

        internal static int GetArrayParameterOid(CassieArray array)
        {
            return GetArrayOid(array.ElementTypeOid)
                ?? throw new NotSupportedException($"Arrays with element type OID {array.ElementTypeOid} are not supported.");
        }

        internal static string GetDataTypeName(int oid)
        {
            return oid is >= 100001 and <= 132767
                ? $"vector({oid - 100000})"
                : oid switch
                {
                    16 => "bool",
                    17 => "bytea",
                    18 => "char",
                    19 => "name",
                    20 => "int8",
                    21 => "int2",
                    23 => "int4",
                    24 => "regproc",
                    25 => "text",
                    26 => "oid",
                    705 => "unknown",
                    2249 => "record",
                    2278 => "void",
                    2287 => "record[]",
                    114 => "json",
                    650 => "cidr",
                    700 => "float4",
                    701 => "float8",
                    774 => "macaddr8",
                    829 => "macaddr",
                    869 => "inet",
                    1042 => "bpchar",
                    1043 => "varchar",
                    1082 => "date",
                    1083 => "time",
                    1114 => "timestamp",
                    1184 => "timestamptz",
                    1186 => "interval",
                    1266 => "timetz",
                    1560 => "bit",
                    1562 => "varbit",
                    1700 => "numeric",
                    2202 => "regprocedure",
                    2203 => "regoper",
                    2204 => "regoperator",
                    2205 => "regclass",
                    2206 => "regtype",
                    2950 => "uuid",
                    3220 => "pg_lsn",
                    3734 => "regconfig",
                    3769 => "regdictionary",
                    3802 => "jsonb",
                    4072 => "jsonpath",
                    4089 => "regnamespace",
                    4096 => "regrole",
                    4191 => "regcollation",
                    775 => "macaddr8[]",
                    _ => GetArrayTypeName(oid) ?? oid.ToString(System.Globalization.CultureInfo.InvariantCulture)
                };
        }

        internal static bool IsVectorOid(int oid)
        {
            return oid is >= 100001 and <= 132767;
        }

        internal static int GetVectorDimension(int oid)
        {
            return IsVectorOid(oid) ? oid - 100000 : throw new ArgumentOutOfRangeException(nameof(oid));
        }

        internal static int GetVectorOid(int dimension)
        {
            return dimension is >= 1 and <= short.MaxValue
                ? 100000 + dimension
                : throw new ArgumentOutOfRangeException(nameof(dimension));
        }

        internal static bool IsArrayOid(int oid)
        {
            return GetArrayElementOid(oid) is not null;
        }

        internal static bool IsObjectIdentifierOid(int oid)
        {
            return oid is 26 or 24 or 2202 or 2203 or 2204 or 2205 or 2206 or 3734 or 3769 or 4089 or 4096 or 4191;
        }

        internal static int? GetArrayElementOid(int arrayOid)
        {
            return arrayOid switch
            {
                1000 => 16,
                1001 => 17,
                1002 => 18,
                1003 => 19,
                1005 => 21,
                1007 => 23,
                1009 => 25,
                1014 => 1042,
                1015 => 1043,
                1016 => 20,
                1021 => 700,
                1022 => 701,
                1028 => 26,
                1040 => 829,
                1041 => 869,
                1115 => 1114,
                1182 => 1082,
                1183 => 1083,
                1185 => 1184,
                1187 => 1186,
                1231 => 1700,
                1270 => 1266,
                1561 => 1560,
                1563 => 1562,
                199 => 114,
                651 => 650,
                2951 => 2950,
                3221 => 3220,
                3807 => 3802,
                775 => 774,
                _ => null
            };
        }

        private static int? GetArrayOid(int elementOid)
        {
            return elementOid switch
            {
                16 => 1000,
                17 => 1001,
                18 => 1002,
                19 => 1003,
                20 => 1016,
                21 => 1005,
                23 => 1007,
                25 => 1009,
                26 => 1028,
                114 => 199,
                650 => 651,
                700 => 1021,
                701 => 1022,
                774 => 775,
                829 => 1040,
                869 => 1041,
                1042 => 1014,
                1043 => 1015,
                1082 => 1182,
                1083 => 1183,
                1114 => 1115,
                1184 => 1185,
                1186 => 1187,
                1266 => 1270,
                1560 => 1561,
                1562 => 1563,
                1700 => 1231,
                2950 => 2951,
                3220 => 3221,
                3802 => 3807,
                _ => null
            };
        }

        [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)]
        internal static Type GetClrType(int oid)
        {
            return oid switch
            {
                16 => typeof(bool),
                17 => typeof(byte[]),
                20 => typeof(long),
                21 => typeof(short),
                23 => typeof(int),
                26 => typeof(uint),
                700 => typeof(float),
                701 => typeof(double),
                1700 => typeof(CassieNumeric),
                2950 => typeof(Guid),
                1082 => typeof(DateOnly),
                1083 => typeof(TimeOnly),
                1114 => typeof(DateTime),
                1184 => typeof(DateTimeOffset),
                1266 => typeof(CassieTimeWithTimeZone),
                _ => typeof(string)
            };
        }

        [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)]
        internal static Type? GetBinaryClrType(int oid)
        {
            return IsVectorOid(oid) ? typeof(CassieVector) : IsObjectIdentifierOid(oid) ? typeof(uint) : oid switch
            {
                650 or 869 => typeof(CassieNetworkAddress),
                774 or 829 => typeof(PhysicalAddress),
                1186 => typeof(CassieInterval),
                1266 => typeof(CassieTimeWithTimeZone),
                1560 or 1562 => typeof(CassieBitString),
                3220 => typeof(CassieLsn),
                _ => null
            };
        }

        private static string? GetArrayTypeName(int oid)
        {
            return oid switch
            {
                1000 => "bool[]",
                1001 => "bytea[]",
                1002 => "char[]",
                1003 => "name[]",
                1005 => "int2[]",
                1007 => "int4[]",
                1009 => "text[]",
                1014 => "bpchar[]",
                1015 => "varchar[]",
                1016 => "int8[]",
                1021 => "float4[]",
                1022 => "float8[]",
                1028 => "oid[]",
                1040 => "macaddr[]",
                1041 => "inet[]",
                1115 => "timestamp[]",
                1182 => "date[]",
                1183 => "time[]",
                1185 => "timestamptz[]",
                1187 => "interval[]",
                1231 => "numeric[]",
                1270 => "timetz[]",
                1561 => "bit[]",
                1563 => "varbit[]",
                199 => "json[]",
                2951 => "uuid[]",
                651 => "cidr[]",
                3221 => "pg_lsn[]",
                3807 => "jsonb[]",
                _ => null
            };
        }
    }
}
