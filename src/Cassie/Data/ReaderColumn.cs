using System.Diagnostics.CodeAnalysis;

namespace Cntryl.Cassie.Data
{
    internal sealed record ReaderColumn(
        string Name,
        uint TableOid,
        short AttributeNumber,
        int TypeOid,
        short TypeSize,
        int TypeModifier,
        [param: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)]
        [property: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)] Type FieldType,
        short FormatCode);
}
