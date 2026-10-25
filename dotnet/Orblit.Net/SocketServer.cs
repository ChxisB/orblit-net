using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace Orblit.Net;

/// <summary>
/// Accepts connections on behalf of an authority.
/// </summary>
/// <remarks>
/// Deliberately thin: it hands out transports and says nothing about who is allowed to connect.
/// Authentication belongs to the game, which knows what an account is. The engine only knows what a
/// socket is. A connection waiting to upgrade is dropped after ten seconds, but how many may wait at
/// once is not capped. Cap it before a public server needs it.
/// </remarks>
public sealed class SocketServer : IAsyncDisposable
{
    private static readonly TimeSpan HeadTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpListener _listener;
    private readonly IPAddress _address;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly Task _accepting;
    private Action<SocketTransport>[] _handlers = [];

    private SocketServer(IPAddress address, int port)
    {
        _address = address;
        _listener = new TcpListener(address, port);
        _listener.Start();
        _accepting = AcceptLoop();
    }

    /// <summary>The port actually bound, which matters when zero was requested.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public Uri Url => new UriBuilder(Uri.UriSchemeWs, _address.ToString(), Port).Uri;

    /// <summary>
    /// Listens on <paramref name="address"/> and <paramref name="port"/>, the loopback address by default.
    /// Pass port zero to let the operating system choose one, which is what tests should do rather
    /// than picking a number and hoping.
    /// </summary>
    public static SocketServer Bind(IPAddress? address = null, int port = 0) => new(address ?? IPAddress.Loopback, port);

    /// <summary>
    /// Calls <paramref name="onConnection"/> with each client as it arrives. The caller gives it to the
    /// host. A client that arrives while nobody listens is turned away.
    /// </summary>
    public IDisposable Listen(Action<SocketTransport> onConnection)
    {
        lock (_gate)
        {
            _handlers = [.. _handlers, onConnection];
        }

        return new Subscription(this, onConnection);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accepting;
        _stop.Dispose();
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (true)
            {
                _ = Serve(await _listener.AcceptTcpClientAsync(_stop.Token));
            }
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The listener was stopped.
        }
    }

    private async Task Serve(TcpClient client)
    {
        var handedOver = false;
        try
        {
            var stream = client.GetStream();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            limit.CancelAfter(HeadTimeout);
            if (await Upgrade.ReadKey(stream, limit.Token) is { } key)
            {
                await Upgrade.Accept(stream, key, limit.Token);
                handedOver = true;
                await Hand(new SocketTransport(
                    WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true })));
            }
            else
            {
                await Upgrade.Refuse(stream, limit.Token);
            }
        }
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // A client that resets or stalls must not take the server down.
        }
        finally
        {
            if (!handedOver)
            {
                client.Dispose();
            }
        }
    }

    private async Task Hand(SocketTransport transport)
    {
        Action<SocketTransport>[] handlers;
        lock (_gate)
        {
            handlers = _handlers;
        }

        try
        {
            if (handlers.Length == 0)
            {
                await transport.CloseAsync();
                return;
            }

            foreach (var handler in handlers)
            {
                handler(transport);
            }
        }
#pragma warning disable CA1031 // A handler that throws would leave this connection open and unowned.
        catch (Exception)
#pragma warning restore CA1031
        {
            await transport.CloseAsync();
        }
    }

    private void Remove(Action<SocketTransport> handler)
    {
        lock (_gate)
        {
            _handlers = [.. _handlers.Where(other => other != handler)];
        }
    }

    private sealed class Subscription(SocketServer owner, Action<SocketTransport> handler) : IDisposable
    {
        public void Dispose() => owner.Remove(handler);
    }
}
