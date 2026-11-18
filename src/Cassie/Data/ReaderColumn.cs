using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Cntryl.Cassie.Types;

namespace Cntryl.Cassie.Data;

internal sealed record ReaderColumn(
    string Name,
    uint TableOid,
    short AttributeNumber,
    int TypeOid,
    short TypeSize,
    int TypeModifier,
    [param:
        DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties |
                                   DynamicallyAccessedMemberTypes.PublicFields)]
    [property:
        DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties |
                                   DynamicallyAccessedMemberTypes.PublicFields)]
    Type FieldType,
    short FormatCode,
    bool IsVector = false)
{
    internal string DataTypeName => !IsVector
        ? CassieTypeMap.GetDataTypeName(TypeOid)
        : TypeModifier > 0 ? "vector(" + TypeModifier.ToString(CultureInfo.InvariantCulture) + ")" : "vector";
}
