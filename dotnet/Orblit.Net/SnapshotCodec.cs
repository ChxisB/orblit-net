using System.Buffers.Binary;

namespace Orblit.Net;

/// <summary>Thrown when a message is not a snapshot this build can read.</summary>
public sealed class SnapshotFormatException(string message) : FormatException(message);

/// <summary>A snapshot read back off the wire.</summary>
public sealed record DecodedSnapshot
{
    public required long Tick { get; init; }

    /// <summary>The tick this delta was built against. Zero for a full snapshot.</summary>
    public required long BaselineTick { get; init; }

    public required bool IsDelta { get; init; }

    public required IReadOnlyList<SnapshotGroup> Groups { get; init; }

    public required ulong[] Despawned { get; init; }
}

/// <summary>The mask and row count of one group in an encoded snapshot.</summary>
public readonly record struct GroupHeader(ulong Mask, int Rows);

/// <summary>
/// Encodes and decodes snapshots.
/// </summary>
/// <remarks>
/// Component bytes cross the wire exactly as the engine stores them, so a transform costs forty
/// bytes and no conversion. That assumes both ends agree on layout, which they do when they run the
/// same build, and it assumes little-endian peers, which every platform Orblit targets is. A
/// mixed-endian pairing would need byte swapping per component and is not supported rather than
/// silently wrong.
/// </remarks>
public static class SnapshotCodec
{
    private const uint Magic = 0x4F52424E; // 'ORBN'
    private const ushort FormatVersion = 1;
    private const byte KindFull = 0;
    private const byte KindDelta = 1;
    private const int HeaderSize = 26;
    private const int GroupHeaderSize = 16;

    public static byte[] EncodeFull(WorldSnapshot snapshot) =>
        Encode(snapshot.Tick, 0, isDelta: false, snapshot.Groups.Where(group => group.Length > 0).ToList(), []);

    /// <summary>
    /// Encodes only what moved since <paramref name="baseline"/>. Most entities in most frames are
    /// unchanged, so this is where the bandwidth goes. A row is contiguous, so a comparison is one
    /// memory compare per entity.
    /// </summary>
    public static byte[] EncodeDelta(WorldSnapshot current, WorldSnapshot baseline)
    {
        var builders = new OrderedDictionary<ulong, GroupBuilder>();
        foreach (var group in current.Groups)
        {
            for (var i = 0; i < group.Length; i++)
            {
                var networkId = group.NetworkIds[i];
                var row = group.RowAt(i);
                if (baseline.RowFor(networkId) is { } previous && previous.Mask == group.Mask && previous.Bytes.SequenceEqual(row))
                {
                    continue;
                }

                if (!builders.TryGetValue(group.Mask, out var builder))
                {
                    builders[group.Mask] = builder = new GroupBuilder(group.Mask, group.Stride);
                }

                builder.Add(networkId, row);
            }
        }

        var despawned = baseline.NetworkIds.Where(id => current.RowFor(id) is null).ToArray();
        return Encode(current.Tick, baseline.Tick, isDelta: true, builders.Values.Select(b => b.Build()).ToList(), despawned);
    }

    /// <summary>
    /// The mask and row count of each group in <paramref name="message"/>, read without copying the
    /// rows. It trusts the message, which is one this codec just encoded.
    /// </summary>
    public static IReadOnlyList<GroupHeader> GroupHeaders(ReadOnlySpan<byte> message)
    {
        var headers = new List<GroupHeader>();
        var offset = HeaderSize;
        for (var g = BinaryPrimitives.ReadUInt16LittleEndian(message[24..]); g > 0; g--)
        {
            var rows = (int)BinaryPrimitives.ReadUInt32LittleEndian(message[(offset + 8)..]);
            var stride = (int)BinaryPrimitives.ReadUInt32LittleEndian(message[(offset + 12)..]);
            headers.Add(new GroupHeader(BinaryPrimitives.ReadUInt64LittleEndian(message[offset..]), rows));
            offset += GroupHeaderSize + (rows * (8 + stride));
        }

        return headers;
    }

    public static DecodedSnapshot Decode(ReadOnlySpan<byte> message)
    {
        if (message.Length < HeaderSize)
        {
            throw new SnapshotFormatException("Snapshot is too short to hold a header.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(message) != Magic)
        {
            throw new SnapshotFormatException("Not a snapshot.");
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(message[4..]);
        if (version != FormatVersion)
        {
            throw new SnapshotFormatException($"Snapshot format {version}, but this build speaks {FormatVersion}.");
        }

        var groupCount = BinaryPrimitives.ReadUInt16LittleEndian(message[24..]);
        var offset = HeaderSize;
        var groups = new List<SnapshotGroup>(groupCount);
        for (var g = 0; g < groupCount; g++)
        {
            groups.Add(ReadGroup(message, ref offset, g));
        }

        return new DecodedSnapshot
        {
            Tick = unchecked((long)BinaryPrimitives.ReadUInt64LittleEndian(message[8..])),
            BaselineTick = unchecked((long)BinaryPrimitives.ReadUInt64LittleEndian(message[16..])),
            IsDelta = message[6] == KindDelta,
            Groups = groups,
            Despawned = ReadDespawned(message, offset),
        };
    }

    private static SnapshotGroup ReadGroup(ReadOnlySpan<byte> message, ref int offset, int index)
    {
        if ((long)offset + GroupHeaderSize > message.Length)
        {
            throw new SnapshotFormatException($"Snapshot ends inside group {index}.");
        }

        var mask = BinaryPrimitives.ReadUInt64LittleEndian(message[offset..]);
        var rowCount = BinaryPrimitives.ReadUInt32LittleEndian(message[(offset + 8)..]);
        var stride = BinaryPrimitives.ReadUInt32LittleEndian(message[(offset + 12)..]);
        offset += GroupHeaderSize;

        // The sizes come off the wire, so they are summed wide before anything is allocated.
        var idBytes = (long)rowCount * 8;
        var rowBytes = (long)rowCount * stride;
        if (offset + idBytes + rowBytes > message.Length)
        {
            throw new SnapshotFormatException($"Snapshot ends inside group {index}.");
        }

        var networkIds = new ulong[rowCount];
        for (var i = 0; i < networkIds.Length; i++)
        {
            networkIds[i] = BinaryPrimitives.ReadUInt64LittleEndian(message[(offset + (i * 8))..]);
        }

        offset += (int)idBytes;
        var rows = message.Slice(offset, (int)rowBytes).ToArray();
        offset += (int)rowBytes;
        return new SnapshotGroup(mask, (int)stride, networkIds, rows);
    }

    private static ulong[] ReadDespawned(ReadOnlySpan<byte> message, int offset)
    {
        if ((long)offset + 4 > message.Length)
        {
            throw new SnapshotFormatException("Snapshot ends inside the despawn list.");
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(message[offset..]);
        offset += 4;
        if (offset + ((long)count * 8) > message.Length)
        {
            throw new SnapshotFormatException("Snapshot ends inside the despawn list.");
        }

        var despawned = new ulong[count];
        for (var i = 0; i < despawned.Length; i++)
        {
            despawned[i] = BinaryPrimitives.ReadUInt64LittleEndian(message[(offset + (i * 8))..]);
        }

        return despawned;
    }

    private static byte[] Encode(long tick, long baselineTick, bool isDelta, List<SnapshotGroup> groups, ulong[] despawned)
    {
        var size = HeaderSize;
        foreach (var group in groups)
        {
            size += GroupHeaderSize + (group.Length * 8) + group.Rows.Length;
        }

        size += 4 + (despawned.Length * 8);

        var message = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(message, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(4), FormatVersion);
        message[6] = isDelta ? KindDelta : KindFull;
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(8), unchecked((ulong)tick));
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(16), unchecked((ulong)baselineTick));
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(24), (ushort)groups.Count);

        var offset = HeaderSize;
        foreach (var group in groups)
        {
            offset = WriteGroup(message, offset, group);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(offset), (uint)despawned.Length);
        offset += 4;
        foreach (var id in despawned)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(offset), id);
            offset += 8;
        }

        return message;
    }

    private static int WriteGroup(byte[] message, int offset, SnapshotGroup group)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(offset), group.Mask);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(offset + 8), (uint)group.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(offset + 12), (uint)group.Stride);
        offset += GroupHeaderSize;

        foreach (var id in group.NetworkIds)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(offset), id);
            offset += 8;
        }

        group.Rows.CopyTo(message, offset);
        return offset + group.Rows.Length;
    }

    private sealed class GroupBuilder(ulong mask, int stride)
    {
        private readonly List<ulong> _networkIds = [];
        private readonly List<byte> _rows = [];

        public void Add(ulong networkId, ReadOnlySpan<byte> row)
        {
            _networkIds.Add(networkId);
            _rows.AddRange(row);
        }

        public SnapshotGroup Build() => new(mask, stride, [.. _networkIds], [.. _rows]);
    }
}
