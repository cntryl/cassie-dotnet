using System.Text;

namespace Cntryl.Cassie.Protocol;

/// <summary>Parses a ParameterStatus payload into its name and value.</summary>
internal static class ParameterStatusMessage
{
    public static (string Name, string Value) Parse(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var nameEnd = Array.IndexOf(payload, (byte)0);

        if (nameEnd < 0)
        {
            throw new IOException("Cassie returned an invalid ParameterStatus frame.");
        }


        var valueStart = nameEnd + 1;
        var valueEnd = Array.IndexOf(payload, (byte)0, valueStart);
        return valueEnd != payload.Length - 1
            ? throw new IOException("Cassie returned an invalid ParameterStatus frame.")
            : (Encoding.UTF8.GetString(payload, 0, nameEnd),
                Encoding.UTF8.GetString(payload, valueStart, valueEnd - valueStart));
    }
}
