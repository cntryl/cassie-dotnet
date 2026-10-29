using System.Buffers.Binary;

namespace Cntryl.Cassie.Protocol
{
    /// <summary>Messages exchanged outside the normal tagged-frame flow: TLS request, startup, and cancel request.</summary>
    internal static class SessionMessages
    {
        public const int ProtocolVersion3 = 196608;
        private const int SslRequestCode = 80877103;
        private const int CancelRequestCode = 80877102;

        public static byte[] SslRequest()
        {
            byte[] packet = new byte[8];
            BinaryPrimitives.WriteInt32BigEndian(packet, packet.Length);
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4), SslRequestCode);
            return packet;
        }

        public static byte[] CancelRequest(int backendProcessId, int backendSecretKey)
        {
            byte[] packet = new byte[16];
            BinaryPrimitives.WriteInt32BigEndian(packet, packet.Length);
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4), CancelRequestCode);
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8), backendProcessId);
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(12), backendSecretKey);
            return packet;
        }

        public static byte[] Startup(string user, string database, string applicationName, string options)
        {
            using MemoryStream body = new();
            PgWriter.WriteInt32(body, ProtocolVersion3);
            PgWriter.WriteCString(body, "user");
            PgWriter.WriteCString(body, user);
            PgWriter.WriteCString(body, "database");
            PgWriter.WriteCString(body, database);
            PgWriter.WriteCString(body, "client_encoding");
            PgWriter.WriteCString(body, "UTF8");
            PgWriter.WriteCString(body, "application_name");
            PgWriter.WriteCString(body, applicationName);
            if (options.Length > 0)
            {
                PgWriter.WriteCString(body, "options");
                PgWriter.WriteCString(body, options);
            }

            body.WriteByte(0);
            byte[] packet = new byte[checked((int)body.Length + sizeof(int))];
            BinaryPrimitives.WriteInt32BigEndian(packet, packet.Length);
            body.GetBuffer().AsSpan(0, (int)body.Length).CopyTo(packet.AsSpan(sizeof(int)));
            return packet;
        }
    }
}
