using System.Buffers.Binary;

namespace Orblit.Net;

/// <summary>
/// A client telling the authority which tick it has applied.
/// </summary>
/// <remarks>
/// Without this the authority has to assume every snapshot arrived, and one dropped packet
/// desynchronises every delta after it. With it, deltas are built against something the client
/// has actually confirmed, so a loss costs one larger message rather than a broken world.
/// </remarks>
public static class Ack
{
    private const uint Magic = 0x4F524241; // 'ORBA'
    private const int Size = 12;

    public static byte[] Encode(long tick)
    {
        var message = new byte[Size];
        BinaryPrimitives.WriteUInt32LittleEndian(message, Magic);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(4), unchecked((ulong)tick));
        return message;
    }

    /// <summary>The acknowledged tick, or null if this is not an ack.</summary>
    public static long? Decode(ReadOnlySpan<byte> message)
    {
        if (message.Length != Size || BinaryPrimitives.ReadUInt32LittleEndian(message) != Magic)
        {
            return null;
        }

        return unchecked((long)BinaryPrimitives.ReadUInt64LittleEndian(message[4..]));
    }
}
