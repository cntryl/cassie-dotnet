namespace Cntryl.Cassie.Protocol
{
    /// <summary>One column entry of a RowDescription message.</summary>
    internal readonly record struct FieldDescription(
        string Name,
        uint TableOid,
        short AttributeNumber,
        int TypeOid,
        short TypeSize,
        int TypeModifier,
        short FormatCode);
}
