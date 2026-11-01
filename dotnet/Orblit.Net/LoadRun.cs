using System.Diagnostics;
using System.Threading.Channels;
using Orblit.Core;

namespace Orblit.Net;

/// <summary>A host and its scripted clients, over loopback links or real sockets.</summary>
internal sealed class LoadRun : IAsyncDisposable
{
    /// <summary>How long a socket client waits to be accepted before the run gives up.</summary>
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(10);

    // PhaseTimes keeps 300 ticks, so a longer window would return the same.
    private const int Measured = 300;

    private readonly LoadOptions _options;
    private readonly SocketServer? _server;
    private readonly IDisposable? _listening;
    private readonly Channel<SocketTransport> _accepted = Channel.CreateUnbounded<SocketTransport>();
    private readonly TickClock _clock;
    private readonly LoadSchema _schema = new();
    private readonly NetHost _host;
    private readonly List<Func<Task>> _closers = [];
    private readonly List<LoadPeer> _peers = [];

    public LoadRun(LoadOptions options)
    {
        _options = options;
        _clock = new TickClock(options.Period);
        _host = new NetHost(new NetHostOptions
        {
            World = _schema.World,
            Set = _schema.Set,
            NetworkId = _schema.NetworkId,
            Clock = _clock,
            Interest = options.CellSize is { } cell
                ? new GridInterest(_schema.Set, _schema.Position, new Grid(cell))
                : null,
        });

        // Bound last, so a constructor that throws does not leave a listener open.
        _server = options.Sockets ? SocketServer.Bind() : null;
        _listening = _server?.Listen(transport => _accepted.Writer.TryWrite(transport));
    }

    public async Task JoinAsync(int index, CancellationToken cancellationToken)
    {
        var id = $"c{index}";
        var entity = _schema.World.CreateEntity();
        var entityId = _host.Spawn(entity, id);
        _schema.World.Add<float>(entity, _schema.Position, new float[3]);

        var (host, client) = _server is null ? Loopback() : await SocketPairAsync(_server, cancellationToken);
        _host.AddClient(id, host);
        _peers.Add(new LoadPeer(client, entityId));
    }

    public async Task TickAsync(int number, CancellationToken cancellationToken)
    {
        // The wait is a stopwatch loop. Task.Delay and Thread.Sleep round up to the operating
        // system's timer step, about 15 ms on Windows, which would turn a 20 Hz tick into 16 Hz.
        // shortcut: this spins a core between ticks. Fine for a harness. A host that must idle
        // should sleep to just short of the due time and spin the rest.
        while (_clock.UntilNext() > TimeSpan.Zero)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        for (var i = 0; i < _peers.Count; i++)
        {
            if (_options.Script(i, number) is { } position)
            {
                _peers[i].SendInput(position);
            }
        }

        if (_options.ServerWork is { } work)
        {
            _host.Phase("system", () => work(number));
        }

        _host.Publish();
    }

    public LoadReport Report(TimeSpan elapsed) => new()
    {
        Clients = _options.Clients,
        Ticks = _options.Ticks,
        Period = _options.Period,
        Elapsed = elapsed,
        Summary = _host.Summary(Measured),
        Accepted = _host.AcceptedInputs,
        Rejected = _host.Rejections.Values.Sum(),
    };

    public async ValueTask DisposeAsync()
    {
        // Close first and wait for the last delivery, so no thread is still applying a snapshot to
        // a world that is about to be freed.
        await Task.WhenAll(_closers.Select(close => close()));
        _host.Dispose();
        foreach (var peer in _peers)
        {
            peer.Dispose();
        }

        _listening?.Dispose();
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        _schema.Dispose();
    }

    private (ITransport Host, ITransport Client) Loopback()
    {
        var link = new LoopbackLink();
        _closers.Add(async () =>
        {
            await link.CloseAsync();
            await link.IdleAsync();
        });
        return (link.Host, link.Client);
    }

    // The server's next connection is the one this client made, because clients join one at a
    // time and the host's ids follow that order.
    private async Task<(ITransport Host, ITransport Client)> SocketPairAsync(
        SocketServer server,
        CancellationToken cancellationToken)
    {
        var connecting = SocketTransport.ConnectAsync(server.Url, JoinTimeout);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(JoinTimeout);
        var accepted = await _accepted.Reader.ReadAsync(limit.Token);
        var client = await connecting;
        _closers.Add(() => Task.WhenAll(accepted.CloseAsync(), client.CloseAsync()));
        return (accepted, client);
    }
}

/// <summary>Runs a host with scripted clients and reports where the time went.</summary>
public static class LoadHarness
{
    /// <summary>
    /// Runs <see cref="LoadOptions.Ticks"/> ticks of a host with <see cref="LoadOptions.Clients"/>
    /// scripted clients, one entity each.
    /// </summary>
    public static async Task<LoadReport> RunAsync(LoadOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await using var run = new LoadRun(options);
        for (var i = 0; i < options.Clients; i++)
        {
            await run.JoinAsync(i, cancellationToken);
        }

        var watch = Stopwatch.StartNew();
        for (var tick = 0; tick < options.Ticks; tick++)
        {
            await run.TickAsync(tick, cancellationToken);
        }

        return run.Report(watch.Elapsed);
    }
}
