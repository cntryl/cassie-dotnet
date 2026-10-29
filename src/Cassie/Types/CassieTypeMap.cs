using Cntryl.Cassie.Commands;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.NetworkInformation;

namespace Cntryl.Cassie.Types
{
    /// <summary>Fixed built-in metadata shared by parameter inference, schema, and codecs.</summary>
    internal static class CassieTypeMap
    {
        // A pair is declared once. Reverse lookup and array names are derived from this catalog.
        // Text arrays intentionally remain strings; binary arrays retain their shape in CassieArray.
        private static readonly BuiltInType[] Types =
        [
            new(16, "bool", 1000, typeof(bool), typeof(bool), BinaryNull: true, ParameterTypes: [typeof(bool)], DbTypes: [DbType.Boolean]),
            new(17, "bytea", 1001, typeof(byte[]), typeof(byte[]), BinaryNull: true, ParameterTypes: [typeof(byte[])], DbTypes: [DbType.Binary]),
            new(18, "char", 1002, typeof(string), typeof(string)),
            new(19, "name", 1003, typeof(string), typeof(string), BinaryNull: true),
            new(20, "int8", 1016, typeof(long), typeof(long), BinaryNull: true, ParameterTypes: [typeof(long)], DbTypes: [DbType.Int64]),
            new(21, "int2", 1005, typeof(short), typeof(short), BinaryNull: true, ParameterTypes: [typeof(byte), typeof(sbyte), typeof(short)], DbTypes: [DbType.Byte, DbType.SByte, DbType.Int16]),
            new(23, "int4", 1007, typeof(int), typeof(int), BinaryNull: true, ParameterTypes: [typeof(int), typeof(ushort)], DbTypes: [DbType.Int32, DbType.UInt16]),
            new(24, "regproc", null, typeof(string), typeof(uint), ObjectIdentifier: true, BinaryNull: true),
            new(25, "text", 1009, typeof(string), typeof(string), ParameterTypes: [typeof(string), typeof(char)], DbTypes: [DbType.AnsiString, DbType.String]),
            new(26, "oid", 1028, typeof(uint), typeof(uint), ObjectIdentifier: true, BinaryNull: true, ParameterTypes: [typeof(uint)], DbTypes: [DbType.UInt32]),
            new(114, "json", 199, typeof(string), typeof(string)),
            new(650, "cidr", 651, typeof(string), typeof(CassieNetworkAddress)),
            new(700, "float4", 1021, typeof(float), typeof(float), BinaryNull: true, ParameterTypes: [typeof(float)], DbTypes: [DbType.Single]),
            new(701, "float8", 1022, typeof(double), typeof(double), BinaryNull: true, ParameterTypes: [typeof(double)], DbTypes: [DbType.Double]),
            new(705, "unknown", null, typeof(string), typeof(string), BinaryResult: false),
            new(774, "macaddr8", 775, typeof(string), typeof(PhysicalAddress)),
            new(829, "macaddr", 1040, typeof(string), typeof(PhysicalAddress)),
            new(869, "inet", 1041, typeof(string), typeof(CassieNetworkAddress)),
            new(1042, "bpchar", 1014, typeof(string), typeof(string), DbTypes: [DbType.AnsiStringFixedLength, DbType.StringFixedLength]),
            new(1043, "varchar", 1015, typeof(string), typeof(string)),
            new(1082, "date", 1182, typeof(DateOnly), typeof(DateOnly), BinaryNull: true, ParameterTypes: [typeof(DateOnly)], DbTypes: [DbType.Date]),
            new(1083, "time", 1183, typeof(TimeOnly), typeof(TimeOnly), BinaryNull: true, ParameterTypes: [typeof(TimeOnly), typeof(TimeSpan)], DbTypes: [DbType.Time]),
            new(1114, "timestamp", 1115, typeof(DateTime), typeof(DateTime), BinaryNull: true, ParameterTypes: [typeof(DateTime)], DbTypes: [DbType.DateTime, DbType.DateTime2]),
            new(1184, "timestamptz", 1185, typeof(DateTimeOffset), typeof(DateTimeOffset), BinaryNull: true, ParameterTypes: [typeof(DateTimeOffset)], DbTypes: [DbType.DateTimeOffset]),
            new(1186, "interval", 1187, typeof(string), typeof(CassieInterval), BinaryNull: true, ParameterTypes: [typeof(CassieInterval)]),
            new(1266, "timetz", 1270, typeof(CassieTimeWithTimeZone), typeof(CassieTimeWithTimeZone), BinaryNull: true, ParameterTypes: [typeof(CassieTimeWithTimeZone)]),
            new(1560, "bit", 1561, typeof(string), typeof(CassieBitString)),
            new(1562, "varbit", 1563, typeof(string), typeof(CassieBitString)),
            new(1700, "numeric", 1231, typeof(CassieNumeric), typeof(CassieNumeric), BinaryNull: true, ParameterTypes: [typeof(ulong), typeof(decimal), typeof(CassieNumeric)], DbTypes: [DbType.UInt64, DbType.Decimal, DbType.Currency, DbType.VarNumeric]),
            new(2202, "regprocedure", null, typeof(string), typeof(uint), ObjectIdentifier: true, BinaryNull: true),
            new(2203, "regoper", null, typeof(string), typeof(uint), ObjectIdentifier: true, BinaryNull: true),
            new(2204, "regoperator", null, typeof(string), typeof(uint), ObjectIdentifier: true, BinaryNull: true),
            new(2205, "regclass", null, typeof(string), typeof(uint), ObjectIdentifier: true, BinaryNull: true),
            new(2206, "regtype", null, typeof(string), typeof(uint), ObjectIdentifier: true, BinaryNull: true),
            new(2249, "record", null, typeof(string), typeof(string), BinaryResult: false),
            new(2278, "void", null, typeof(string), typeof(string)),
            // record[] has metadata only. No record array codec is declared by assigning an ArrayOid.
            new(2287, "record[]", null, typeof(string), typeof(string), BinaryResult: false),
            new(2950, "uuid", 2951, typeof(Guid), typeof(Guid), BinaryNull: true, ParameterTypes: [typeof(Guid)], DbTypes: [DbType.Guid]),
            new(3220, "pg_lsn", 3221, typeof(string), typeof(CassieLsn), ParameterTypes: [typeof(CassieLsn)]),
            new(3734, "regconfig", null, typeof(string), typeof(uint), ObjectIdentifier: true, BinaryNull: true),
            new(3769, "regdictionary", null, typeof(string), typeof(uint), ObjectIdentifier: true, BinaryNull: true),
            new(3802, "jsonb", 3807, typeof(string), typeof(string), BinaryNull: true),
            new(4072, "jsonpath", null, typeof(string), typeof(string), BinaryNull: true),
            new(4089, "regnamespace", null, typeof(string), typeof(uint), ObjectIdentifier: true, BinaryNull: true),
            new(4096, "regrole", null, typeof(string), typeof(uint), ObjectIdentifier: true, BinaryNull: true),
            new(4191, "regcollation", null, typeof(string), typeof(uint), ObjectIdentifier: true, BinaryNull: true)
        ];

        private static readonly Dictionary<int, BuiltInType> Scalars = Types.ToDictionary(static type => type.Oid);
        private static readonly Dictionary<int, BuiltInType> Arrays = Types.Where(static type => type.ArrayOid.HasValue).ToDictionary(static type => type.ArrayOid!.Value);
        private static readonly Dictionary<Type, int> ParameterTypes = Types.SelectMany(static type => (type.ParameterTypes ?? []).Select(parameterType => (Type: parameterType, type.Oid))).ToDictionary(static pair => pair.Type, static pair => pair.Oid);
        private static readonly Dictionary<DbType, int> ParameterDbTypes = Types.SelectMany(static type => (type.DbTypes ?? []).Select(dbType => (DbType: dbType, type.Oid))).ToDictionary(static pair => pair.DbType, static pair => pair.Oid);

        internal static int GetParameterOid(DbParameter parameter)
        {
            ArgumentNullException.ThrowIfNull(parameter);
            if (parameter is CassieParameter cassieParameter)
            {
                if (cassieParameter.PostgresTypeOid < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(parameter), "PostgreSQL type OID cannot be negative.");
                }

                if (cassieParameter.PostgresTypeOid != 0)
                {
                    return parameter.Value is CassieTemporalValue temporal && temporal.PostgresTypeOid != cassieParameter.PostgresTypeOid
                        ? throw new ArgumentException("The temporal value does not match the explicitly selected PostgreSQL type OID.", nameof(parameter))
                        : cassieParameter.PostgresTypeOid;
                }
            }

            object? value = parameter.Value;
            if (value is Array parameterArray and not byte[])
            {
                return GetArrayParameterOid(parameterArray);
            }

            if (value is CassieArray cassieArray)
            {
                return GetArrayParameterOid(cassieArray);
            }

            if (parameter.DbType == DbType.Xml)
            {
                throw new NotSupportedException("Cassie does not support XML parameters.");
            }

            int explicitOid = ParameterDbTypes.GetValueOrDefault(parameter.DbType);
            return explicitOid != 0 ? explicitOid : GetValueOid(value);
        }

        private static int GetValueOid(object? value)
        {
            return value switch
            {
                CassieTemporalValue temporal => temporal.PostgresTypeOid,
                CassieVector vector => GetVectorOid(vector.Dimension),
                CassieNetworkAddress address => address.IsCidr ? 650 : 869,
                CassieBitString bits => bits.IsVarying ? 1562 : 1560,
                PhysicalAddress address => GetMacAddressOid(address),
                null => 0,
                _ => ParameterTypes.GetValueOrDefault(value.GetType())
            };
        }

        internal static int GetArrayParameterOid(Array array)
        {
            Type? elementType = array.GetType().GetElementType();
            if (elementType is null)
            {
                return 0;
            }

            Type type = Nullable.GetUnderlyingType(elementType) ?? elementType;
            if (type == typeof(CassieNetworkAddress) || type == typeof(PhysicalAddress) || type == typeof(CassieBitString) || type == typeof(CassieTemporalValue))
            {
                return GetArrayVariantOid(array, type == typeof(CassieNetworkAddress) ? "inet[] or cidr[]"
                    : type == typeof(PhysicalAddress) ? "macaddr[] or macaddr8[]"
                    : type == typeof(CassieBitString) ? "bit[] or varbit[]" : "temporal arrays");
            }

            int scalarOid = ParameterTypes.GetValueOrDefault(type);
            return GetArrayOid(scalarOid)
                ?? throw new NotSupportedException($"Array parameters with element type '{type.FullName}' are not supported.");
        }

        private static int GetArrayVariantOid(Array array, string typeNames)
        {
            int? arrayOid = null;
            foreach (object? value in array)
            {
                if (value is null or DBNull)
                {
                    continue;
                }

                int valueOid = GetArrayOid(GetValueOid(value))
                    ?? throw new NotSupportedException("The temporal value has no registered PostgreSQL array type.");
                if (arrayOid is int priorOid && priorOid != valueOid)
                {
                    throw new ArgumentException($"Array parameters cannot mix values from {typeNames}.", nameof(array));
                }

                arrayOid = valueOid;
            }

            return arrayOid ?? throw new NotSupportedException($"The PostgreSQL type for an empty or NULL-only {typeNames} parameter cannot be inferred; set CassieParameter.PostgresTypeOid.");
        }

        private static int GetMacAddressOid(PhysicalAddress value)
        {
            return value.GetAddressBytes().Length switch
            {
                6 => 829,
                8 => 774,
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
            return IsVectorOid(oid) ? $"vector({GetVectorDimension(oid)})"
                : Scalars.TryGetValue(oid, out BuiltInType? scalar) ? scalar.Name
                : Arrays.TryGetValue(oid, out BuiltInType? element) ? element.ArrayName
                : oid.ToString(CultureInfo.InvariantCulture);
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
            return dimension is >= 1 and <= short.MaxValue ? 100000 + dimension : throw new ArgumentOutOfRangeException(nameof(dimension));
        }

        internal static bool IsArrayOid(int oid)
        {
            return Arrays.ContainsKey(oid);
        }

        internal static bool IsObjectIdentifierOid(int oid)
        {
            return Scalars.TryGetValue(oid, out BuiltInType? type) && type.ObjectIdentifier;
        }

        internal static int? GetArrayElementOid(int arrayOid)
        {
            return Arrays.TryGetValue(arrayOid, out BuiltInType? type) ? type.Oid : null;
        }

        internal static int? GetArrayOid(int elementOid)
        {
            return Scalars.TryGetValue(elementOid, out BuiltInType? type) ? type.ArrayOid : null;
        }

        internal static bool SupportsBinaryNullParameter(int oid)
        {
            return IsArrayOid(oid) || (Scalars.TryGetValue(oid, out BuiltInType? type) && type.BinaryNull);
        }

        internal static bool SupportsBinaryResult(int oid)
        {
            return IsVectorOid(oid) || IsArrayOid(oid) || (Scalars.TryGetValue(oid, out BuiltInType? type) && type.BinaryResult);
        }

        [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)]
        internal static Type GetClrType(int oid)
        {
            return Scalars.TryGetValue(oid, out BuiltInType? type) ? type.TextClrType : typeof(string);
        }

        [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)]
        internal static Type GetClrType(int oid, short formatCode)
        {
            return formatCode != 1 ? GetClrType(oid)
                : IsVectorOid(oid) ? typeof(CassieVector)
                : IsArrayOid(oid) ? typeof(CassieArray)
                : Scalars.TryGetValue(oid, out BuiltInType? type) ? type.BinaryClrType
                : typeof(string);
        }

        private sealed record BuiltInType(
            int Oid,
            string Name,
            int? ArrayOid,
            [param: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)]
            [property: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)] Type TextClrType,
            [param: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)]
            [property: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)] Type BinaryClrType,
            bool ObjectIdentifier = false,
            bool BinaryNull = false,
            bool BinaryResult = true,
            Type[]? ParameterTypes = null,
            DbType[]? DbTypes = null)
        {
            internal string ArrayName { get; } = Name + "[]";
        }
    }
}
