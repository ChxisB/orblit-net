using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Orblit.Core;

namespace Orblit.Net.Tests;

/// <summary>A host and a client joined by a loopback link, each with its own world.</summary>
internal sealed class Rig : IAsyncDisposable
{
    public Rig(
        bool includeOwner = false,
        IEnumerable<string>? ownerWritable = null,
        TimeSpan interpolationDelay = default,
        Func<TimeSpan>? clock = null)
    {
        Host = new Peer(reverseRegistrationOrder: false, includeOwner, ownerWritable);
        Client = new Peer(reverseRegistrationOrder: true, includeOwner, ownerWritable);
        NetHost = new NetHost(new NetHostOptions
        {
            World = Host.World,
            Set = Host.Set,
            NetworkId = Host.NetworkId,
            OwnerComponent = includeOwner ? Host.Owner : null,
        });
        NetClient = new NetClient(new NetClientOptions
        {
            World = Client.World,
            Set = Client.Set,
            NetworkId = Client.NetworkId,
            Transport = Link.Client,
            InterpolationDelay = interpolationDelay,
            Clock = clock,
        });
        NetHost.AddClient("player-1", Link.Host);
    }

    public Peer Host { get; }

    public Peer Client { get; }

    public LoopbackLink Link { get; } = new();

    public NetHost NetHost { get; }

    public NetClient NetClient { get; }

    public Task Settle() => Link.IdleAsync();

    public async ValueTask DisposeAsync()
    {
        NetClient.Dispose();
        NetHost.Dispose();
        await Link.CloseAsync();
        Client.Dispose();
        Host.Dispose();
    }
}

/// <summary>Reads and writes component columns without holding a span across an await.</summary>
internal static class Column
{
    public static float[] Floats(World world, ulong entity, ComponentType type) =>
        world.TryGetFloat32(entity, type, out var values)
            ? values.ToArray()
            : throw new InvalidOperationException($"{type.Name} is not on entity {entity}.");

    public static void SetFloat(World world, ulong entity, ComponentType type, int index, float value)
    {
        world.TryGetFloat32(entity, type, out var values);
        values[index] = value;
    }

    public static int Int(World world, ulong entity, ComponentType type)
    {
        world.TryGetBytes(entity, type, out var bytes);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    public static void SetInt(World world, ulong entity, ComponentType type, int value)
    {
        world.TryGetBytes(entity, type, out var bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
    }

    public static uint UInt(World world, ulong entity, ComponentType type)
    {
        world.TryGetBytes(entity, type, out var bytes);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    public static byte[] Bytes(params float[] values) => MemoryMarshal.AsBytes<float>(values).ToArray();

    public static IReadOnlyDictionary<ulong, IReadOnlyDictionary<ComponentType, byte[]>> Write(
        ulong networkId,
        ComponentType type,
        params float[] values) =>
        new Dictionary<ulong, IReadOnlyDictionary<ComponentType, byte[]>>
        {
            [networkId] = new Dictionary<ComponentType, byte[]> { [type] = Bytes(values) },
        };
}
