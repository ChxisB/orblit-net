using System.Text;

namespace Orblit.Net.Tests;

internal static class Names
{
    public static byte[] Credentials(string name) => Encoding.UTF8.GetBytes(name);

    /// <summary>Accepts every name.</summary>
    public static ValueTask<Admission> Welcome(byte[] bytes) =>
        new(new Admit(Encoding.UTF8.GetString(bytes)));

    /// <summary>Accepts every name but "banned".</summary>
    public static ValueTask<Admission> ByName(byte[] bytes)
    {
        var name = Encoding.UTF8.GetString(bytes);
        return new(name == "banned" ? new Refuse("banned") : new Admit(name));
    }
}

internal static class Links
{
    /// <summary>
    /// Waits until no link has anything left to deliver. A delivery on one link can send on another,
    /// as when a take over sends the old connection away.
    /// </summary>
    public static async Task SettleAsync(IReadOnlyCollection<LoopbackLink> links)
    {
        do
        {
            foreach (var link in links)
            {
                await link.IdleAsync();
            }
        }
        while (links.Any(link => !link.IsIdle));
    }
}

/// <summary>A transport that records what the host did to it.</summary>
internal sealed class Probe : ITransport
{
    private readonly Inbound _heard = new();
    private volatile bool _closed;

    public List<string> Goodbyes { get; } = [];

    public bool Closed => _closed;

    public IDisposable Listen(Action<byte[]> onMessage, Action? onDone = null, Action<Exception>? onError = null) =>
        _heard.Listen(onMessage, onDone, onError);

    public void Hear(byte[] message) => _heard.Message(message);

    /// <summary>The peer going away.</summary>
    public void HangUp() => _heard.Done();

    public void Send(byte[] message)
    {
        if (SessionMessage.DecodeGoodbye(message) is { } reason)
        {
            Goodbyes.Add(reason);
        }
    }

    public Task CloseAsync()
    {
        _closed = true;
        return Task.CompletedTask;
    }
}

/// <summary>One connection: a loopback link and the client at its far end.</summary>
internal sealed class Visitor : IDisposable
{
    public Visitor(Peer peer)
    {
        Peer = peer;
        Client = new NetClient(new NetClientOptions
        {
            World = peer.World,
            Set = peer.Set,
            NetworkId = peer.NetworkId,
            Transport = Link.Client,
            OnGoodbye = Reasons.Add,
            OnDespawn = (networkId, _) => Despawned.Add(networkId),
        });
    }

    public Peer Peer { get; }

    public ulong NetworkId { get; set; }

    public LoopbackLink Link { get; } = new();

    public List<string> Reasons { get; } = [];

    public List<ulong> Despawned { get; } = [];

    public NetClient Client { get; }

    /// <summary>One input that moves the visitor's own entity.</summary>
    public byte[] Move() =>
        InputCodec.Encode(new InputMessage(
            1,
            [new InputEntry(NetworkId, 1UL << Peer.Set.BitOf(Peer.Position)!.Value, Column.Bytes(1, 2, 3))]));

    public void Flood(int messages)
    {
        var input = Move();
        for (var i = 0; i < messages; i++)
        {
            Link.Client.Send(input);
        }
    }

    public void Dispose()
    {
        Client.Dispose();
        Peer.Dispose();
    }
}

/// <summary>An authority world, a host over it, and the visitors that connect to the host.</summary>
internal sealed class Harness(bool includeOwner = false, IEnumerable<string>? ownerWritable = null) : IAsyncDisposable
{
    private readonly List<Visitor> _visitors = [];
    private readonly string[]? _writable = ownerWritable?.ToArray();

    public Peer Authority { get; } = new(reverseRegistrationOrder: false, includeOwner, ownerWritable);

    public NetHost Host { get; set; } = null!;

    public Visitor NewVisitor()
    {
        var visitor = new Visitor(new Peer(reverseRegistrationOrder: true, includeOwner, _writable));
        _visitors.Add(visitor);
        return visitor;
    }

    public Task Settle() => Links.SettleAsync([.. _visitors.Select(visitor => visitor.Link)]);

    public async ValueTask DisposeAsync()
    {
        Host?.Dispose();
        foreach (var visitor in _visitors)
        {
            visitor.Client.Dispose();
            await visitor.Link.CloseAsync();
            visitor.Dispose();
        }

        Authority.Dispose();
    }
}
