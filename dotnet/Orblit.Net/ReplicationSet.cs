using Orblit.Core;

namespace Orblit.Net;

/// <summary>A component that crosses the wire, and the rules for who may write it.</summary>
/// <param name="Type">The component.</param>
/// <param name="Bit">This component's position in the set, and its bit in a group's mask.</param>
/// <param name="OwnerWritable">
/// Whether the client that owns an entity may write this component and have the authority accept
/// it. Ownership is a security surface rather than bookkeeping: everything not marked here is the
/// authority's alone, and a client that sends it anyway is ignored rather than trusted.
/// </param>
public sealed record ReplicatedComponent(ComponentType Type, int Bit, bool OwnerWritable = false)
{
    public override string ToString() =>
        $"ReplicatedComponent({Type.Name}, bit {Bit}{(OwnerWritable ? ", owner-writable" : "")})";
}

/// <summary>
/// The components that replicate, in an order both ends agree on.
/// </summary>
/// <remarks>
/// The order comes from the component names rather than from registration order, so a host and a
/// client that declare the same components arrive at the same layout without exchanging a schema.
/// Registration order would make the wire format depend on which package happened to load first.
/// </remarks>
public sealed class ReplicationSet
{
    private const int MaxComponents = 64;

    private readonly Dictionary<uint, int> _bitByComponentId = [];

    /// <param name="types">The components to replicate.</param>
    /// <param name="ownerWritable">The names of the ones an owning client is allowed to write.</param>
    public ReplicationSet(IEnumerable<ComponentType> types, IEnumerable<string>? ownerWritable = null)
    {
        var sorted = types.ToList();
        if (sorted.Count > MaxComponents)
        {
            // The group mask is a single 64-bit word, which keeps a group header to sixteen bytes.
            // More than sixty-four replicated component types is a different design, not a bigger number.
            throw new ArgumentException("A replication set holds at most 64 components.", nameof(types));
        }

        sorted.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        var writable = ownerWritable is null ? [] : new HashSet<string>(ownerWritable);
        var names = new HashSet<string>();
        var components = new List<ReplicatedComponent>(sorted.Count);
        for (var bit = 0; bit < sorted.Count; bit++)
        {
            var type = sorted[bit];
            if (!names.Add(type.Name))
            {
                throw new ArgumentException($"Component \"{type.Name}\" is listed twice.", nameof(types));
            }

            components.Add(new ReplicatedComponent(type, bit, writable.Contains(type.Name)));
            _bitByComponentId[type.Id] = bit;
        }

        Components = components;
    }

    public IReadOnlyList<ReplicatedComponent> Components { get; }

    public int Length => Components.Count;

    /// <summary>The bit for a component, or null if it does not replicate.</summary>
    public int? BitOf(ComponentType type) => BitOfId(type.Id);

    public int? BitOfId(uint componentId) =>
        _bitByComponentId.TryGetValue(componentId, out var bit) ? bit : null;

    public ReplicatedComponent AtBit(int bit) => Components[bit];

    /// <summary>The mask for a set of component ids, ignoring any that do not replicate.</summary>
    public ulong MaskOfIds(IEnumerable<uint> componentIds)
    {
        var mask = 0UL;
        foreach (var id in componentIds)
        {
            if (_bitByComponentId.TryGetValue(id, out var bit))
            {
                mask |= 1UL << bit;
            }
        }

        return mask;
    }

    /// <summary>The bits set in <paramref name="mask"/>, ascending: the order a row's components are written in.</summary>
    public IEnumerable<int> BitsOf(ulong mask)
    {
        for (var bit = 0; bit < Components.Count; bit++)
        {
            if ((mask & (1UL << bit)) != 0)
            {
                yield return bit;
            }
        }
    }

    /// <summary>How many bytes one entity occupies under <paramref name="mask"/>.</summary>
    public int StrideOf(ulong mask)
    {
        var stride = 0;
        foreach (var bit in BitsOf(mask))
        {
            stride += Components[bit].Type.ByteSize;
        }

        return stride;
    }
}
