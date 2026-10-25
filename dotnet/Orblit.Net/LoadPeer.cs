using System.Runtime.InteropServices;
using Orblit.Core;

namespace Orblit.Net;

/// <summary>The components both sides declare. Each client has a world of its own.</summary>
internal sealed class LoadSchema : IDisposable
{
    public LoadSchema()
    {
        NetworkId = World.RegisterComponent("NetworkId", ComponentKind.Int64);
        Position = World.RegisterComponent("Position", ComponentKind.Float32, 3);
        Set = new ReplicationSet([Position], ["Position"]);
    }

    public World World { get; } = new();

    public ComponentType NetworkId { get; }

    public ComponentType Position { get; }

    public ReplicationSet Set { get; }

    public void Dispose() => World.Dispose();
}

/// <summary>One scripted client with a world of its own.</summary>
internal sealed class LoadPeer : IDisposable
{
    private readonly LoadSchema _schema = new();
    private readonly ulong _entityId;
    private readonly NetClient _client;

    public LoadPeer(ITransport transport, ulong entityId)
    {
        _entityId = entityId;
        _client = new NetClient(new NetClientOptions
        {
            World = _schema.World,
            Set = _schema.Set,
            NetworkId = _schema.NetworkId,
            Transport = transport,
        });
    }

    public void SendInput(float[] position) =>
        _client.SendInput(new Dictionary<ulong, IReadOnlyDictionary<ComponentType, byte[]>>
        {
            [_entityId] = new Dictionary<ComponentType, byte[]>
            {
                [_schema.Position] = MemoryMarshal.AsBytes(position.AsSpan()).ToArray(),
            },
        });

    public void Dispose()
    {
        _client.Dispose();
        _schema.Dispose();
    }
}
