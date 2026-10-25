using System.Runtime.InteropServices;
using Orblit.Core;

namespace Orblit.Net;

/// <summary>
/// Reads the replicated state of a world.
/// </summary>
/// <remarks>
/// Holds its query across ticks so the matching archetypes are resolved once rather than every frame.
/// </remarks>
public sealed class SnapshotCapture : IDisposable
{
    private readonly ReplicationSet _set;
    private readonly ComponentType _networkId;
    private readonly Query _query;

    /// <param name="world">The world to read.</param>
    /// <param name="set">The components that replicate.</param>
    /// <param name="networkId">The component holding each replicated entity's stable wire identity.</param>
    public SnapshotCapture(World world, ReplicationSet set, ComponentType networkId)
    {
        if (set.BitOf(networkId) is not null)
        {
            throw new ArgumentException(
                "The network id must not be part of the replication set: it keys the rows rather than travelling inside them.",
                nameof(set));
        }

        _set = set;
        _networkId = networkId;
        _query = world.Query(networkId);
    }

    public WorldSnapshot Capture(long tick)
    {
        var groups = new List<SnapshotGroup>();
        foreach (var chunk in _query.Chunks)
        {
            var mask = _set.MaskOfIds(chunk.ComponentIds);
            if (mask != 0)
            {
                groups.Add(Gather(chunk, mask));
            }
        }

        return new WorldSnapshot(tick, groups);
    }

    public void Dispose() => _query.Dispose();

    private SnapshotGroup Gather(Chunk chunk, ulong mask)
    {
        var length = chunk.Length;
        var stride = _set.StrideOf(mask);
        var rows = new byte[length * stride];

        var offset = 0;
        foreach (var bit in _set.BitsOf(mask))
        {
            var type = _set.AtBit(bit).Type;
            var size = type.ByteSize;
            chunk.TryGetBytes(type, out var column);
            for (var row = 0; row < length; row++)
            {
                column.Slice(row * size, size).CopyTo(rows.AsSpan(row * stride + offset));
            }

            offset += size;
        }

        chunk.TryGetBytes(_networkId, out var ids);
        return new SnapshotGroup(mask, stride, MemoryMarshal.Cast<byte, ulong>(ids).ToArray(), rows);
    }
}
