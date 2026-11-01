using System.Buffers.Binary;

namespace Orblit.Net;

/// <summary>One entity a client is proposing to change.</summary>
/// <param name="NetworkId">The entity's wire identity.</param>
/// <param name="Mask">Which components the client is writing, as bits in the replication set.</param>
/// <param name="Row">The component bytes, in ascending bit order, exactly as a snapshot row.</param>
public sealed record InputEntry(ulong NetworkId, ulong Mask, byte[] Row);

/// <summary>A client's proposed writes to entities it owns.</summary>
/// <param name="Tick">
/// The client's tick when it produced this, so the authority can tell how stale the input is.
/// </param>
/// <param name="Entries">The proposed writes.</param>
public sealed record InputMessage(long Tick, IReadOnlyList<InputEntry> Entries);

public static class InputCodec
{
    private const uint Magic = 0x4F524249; // 'ORBI'
    private const int HeaderSize = 14;
    private const int EntrySize = 20;

    public static byte[] Encode(InputMessage message)
    {
        var size = HeaderSize;
        foreach (var entry in message.Entries)
        {
            size += EntrySize + entry.Row.Length;
        }

        var bytes = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(4), unchecked((ulong)message.Tick));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), unchecked((ushort)message.Entries.Count));

        var offset = HeaderSize;
        foreach (var entry in message.Entries)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), entry.NetworkId);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset + 8), entry.Mask);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 16), (uint)entry.Row.Length);
            offset += EntrySize;
            entry.Row.CopyTo(bytes, offset);
            offset += entry.Row.Length;
        }

        return bytes;
    }

    /// <summary>Decodes an input message, or returns null if this is something else or cut short.</summary>
    public static InputMessage? Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic)
        {
            return null;
        }

        var tick = unchecked((long)BinaryPrimitives.ReadUInt64LittleEndian(bytes[4..]));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]);
        var entries = new List<InputEntry>(count);

        var offset = HeaderSize;
        for (var i = 0; i < count; i++)
        {
            if (ReadEntry(bytes, ref offset) is not { } entry)
            {
                return null;
            }

            entries.Add(entry);
        }

        return new InputMessage(tick, entries);
    }

    private static InputEntry? ReadEntry(ReadOnlySpan<byte> bytes, ref int offset)
    {
        if ((long)offset + EntrySize > bytes.Length)
        {
            return null;
        }

        var networkId = BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);
        var mask = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(offset + 8)..]);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 16)..]);
        offset += EntrySize;
        if (offset + (long)length > bytes.Length)
        {
            return null;
        }

        var row = bytes.Slice(offset, (int)length).ToArray();
        offset += (int)length;
        return new InputEntry(networkId, mask, row);
    }
}
