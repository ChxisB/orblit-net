namespace Orblit.Net;

/// <summary>
/// One run of entities sharing the same set of replicated components.
/// </summary>
/// <remarks>
/// Rows are interleaved, so an entity's components sit together, because that is the shape a delta
/// sends and the shape a comparison wants. The column layout still pays for itself on the way in:
/// gathering a group reads each component's storage in one contiguous pass.
/// </remarks>
/// <param name="mask">Which replicated components these entities carry.</param>
/// <param name="stride">Bytes per entity under the mask.</param>
/// <param name="networkIds">The wire identity of each entity.</param>
/// <param name="rows"><c>networkIds.Length * stride</c> bytes, entity by entity.</param>
public sealed class SnapshotGroup(ulong mask, int stride, ulong[] networkIds, byte[] rows)
{
    public ulong Mask { get; } = mask;

    public int Stride { get; } = stride;

    public ulong[] NetworkIds { get; } = networkIds;

    public byte[] Rows { get; } = rows;

    public int Length => NetworkIds.Length;

    public ReadOnlySpan<byte> RowAt(int index) => Rows.AsSpan(index * Stride, Stride);

    /// <summary>A group of the entities at <paramref name="indices"/>, in that order, with their rows copied.</summary>
    public SnapshotGroup Pick(IReadOnlyList<int> indices)
    {
        var picked = new byte[indices.Count * Stride];
        var ids = new ulong[indices.Count];
        for (var i = 0; i < indices.Count; i++)
        {
            RowAt(indices[i]).CopyTo(picked.AsSpan(i * Stride));
            ids[i] = NetworkIds[indices[i]];
        }

        return new SnapshotGroup(Mask, Stride, ids, picked);
    }
}

/// <summary>Where one entity's state sits inside a snapshot.</summary>
public readonly record struct SnapshotRow(ulong Mask, SnapshotGroup Group, int Index)
{
    public ReadOnlySpan<byte> Bytes => Group.RowAt(Index);
}

/// <summary>Everything replicated, as of one tick.</summary>
public sealed class WorldSnapshot
{
    // Insertion order is the order the groups list them, which a delta's despawn list follows.
    private readonly OrderedDictionary<ulong, SnapshotRow> _byNetworkId = [];

    public WorldSnapshot(long tick, IReadOnlyList<SnapshotGroup> groups)
    {
        Tick = tick;
        Groups = groups;
        foreach (var group in groups)
        {
            for (var i = 0; i < group.Length; i++)
            {
                _byNetworkId[group.NetworkIds[i]] = new SnapshotRow(group.Mask, group, i);
            }
        }
    }

    public long Tick { get; }

    public IReadOnlyList<SnapshotGroup> Groups { get; }

    public IEnumerable<ulong> NetworkIds => _byNetworkId.Keys;

    public int EntityCount => _byNetworkId.Count;

    public SnapshotRow? RowFor(ulong networkId) =>
        _byNetworkId.TryGetValue(networkId, out var row) ? row : null;

    /// <summary>
    /// The entities whose network ids pass <paramref name="keep"/>, asked once each. A group that
    /// passes whole is shared rather than copied.
    /// </summary>
    public WorldSnapshot Filtered(Func<ulong, bool> keep)
    {
        var kept = new List<SnapshotGroup>();
        foreach (var group in Groups)
        {
            var indices = Enumerable.Range(0, group.Length).Where(i => keep(group.NetworkIds[i])).ToList();
            if (indices.Count == group.Length)
            {
                kept.Add(group);
            }
            else if (indices.Count > 0)
            {
                kept.Add(group.Pick(indices));
            }
        }

        return new WorldSnapshot(Tick, kept);
    }

    public static WorldSnapshot Empty(long tick) => new(tick, []);
}
