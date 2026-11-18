namespace Cntryl.Cassie.Commands;

/// <summary>Validates timeouts shared by commands and batches.</summary>
internal static class CommandTimeouts
{
    public static int Validate(int value)
    {
        return value is >= 0 and <= 2_147_483
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value,
                "CommandTimeout must be between 0 and 2147483 seconds.");
    }
}
