using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace Orblit.Net.Tests;

/// <summary>
/// A WebSocket server for transport tests. It answers every binary message with a text frame and
/// then the same bytes, so a client that does not drop text frames shows it. It also offers a port
/// nothing listens on and a port that accepts a connection and never answers.
/// </summary>
internal sealed class EchoServer : IAsyncDisposable
{
    /// <summary>A one byte message that makes the server close the connection it came on.</summary>
    public const byte HangUp = 0xff;

    /// <summary>A one byte message that makes the server stop reading, as a peer on a dead network does.</summary>
    public const byte Stall = 0xfe;

    private readonly TcpListener _echo = new(IPAddress.Loopback, 0);
    private readonly TcpListener _blackhole = new(IPAddress.Loopback, 0);
    private readonly ConcurrentBag<TcpClient> _held = [];
    private readonly ConcurrentBag<Task> _serving = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task[] _accepting;

    public EchoServer()
    {
        _echo.Start();
        _blackhole.Start();
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        Dead = At(closed);
        closed.Stop();
        Echo = At(_echo);
        Blackhole = At(_blackhole);
        _accepting = [Accept(_echo, client => _serving.Add(Serve(client))), Accept(_blackhole, _held.Add)];
    }

    public Uri Echo { get; }

    public Uri Dead { get; }

    public Uri Blackhole { get; }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _echo.Stop();
        _blackhole.Stop();
        await Task.WhenAll(_accepting);
        await Task.WhenAll(_serving);
        foreach (var client in _held)
        {
            client.Dispose();
        }

        _stop.Dispose();
    }

    private static Uri At(TcpListener listener) =>
        new($"ws://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");

    private async Task Accept(TcpListener listener, Action<TcpClient> keep)
    {
        try
        {
            while (true)
            {
                keep(await listener.AcceptTcpClientAsync(_stop.Token));
            }
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Stopped.
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var key = await Upgrade.ReadKey(stream, _stop.Token) ?? throw new InvalidOperationException("Not an upgrade.");
                await Upgrade.Accept(stream, key, _stop.Token);
                using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });
                while (await Read(socket) is { } message)
                {
                    await Answer(socket, message);
                }
            }
            catch (Exception error) when (error is IOException or WebSocketException or OperationCanceledException)
            {
                // The client left.
            }
        }
    }

    private async Task<byte[]?> Read(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var part = await socket.ReceiveAsync(buffer.AsMemory(), _stop.Token);
            if (part.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, part.Count);
            if (part.EndOfMessage)
            {
                return message.ToArray();
            }
        }
    }

    private async Task Answer(WebSocket socket, byte[] message)
    {
        switch (message)
        {
            case [HangUp]:
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, _stop.Token);
                await Task.Delay(Timeout.Infinite, _stop.Token);
                break;
            case [Stall]:
                await Task.Delay(Timeout.Infinite, _stop.Token);
                break;
            default:
                await socket.SendAsync("noise"u8.ToArray(), WebSocketMessageType.Text, true, _stop.Token);
                await socket.SendAsync(message, WebSocketMessageType.Binary, true, _stop.Token);
                break;
        }
    }
}
