using System.Net.WebSockets;
using System.Threading.Channels;

namespace Orblit.Net;

/// <summary>
/// A transport over a WebSocket.
/// </summary>
/// <remarks>
/// WebSockets rather than raw UDP, for now, and the reason is framing: a snapshot is a message with
/// a length, and WebSocket already delivers whole messages in order. Building sequencing,
/// acknowledgement and reassembly on top of datagrams is a real piece of work, and it is not the
/// piece that makes Orblit worth using. When the frame budget says otherwise, this interface is
/// where a datagram transport goes. Reading starts with the first <see cref="Listen"/>, so a
/// message that arrives before the host or client is listening waits instead of being lost.
/// </remarks>
public sealed class SocketTransport : ITransport, IAsyncDisposable
{
    /// <summary>The longest message a peer may send. A longer one ends the link.</summary>
    internal const int MaxMessage = 16 << 20;

    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(1);

    private readonly WebSocket _socket;
    private readonly Inbound _inbound = new();
    private readonly Channel<byte[]> _outbox =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _sending;
    private int _reading;
    private int _closing;
    private int _finished;

    public SocketTransport(WebSocket socket)
    {
        _socket = socket;
        _sending = Send();
    }

    /// <summary>Whether a message sent now would go out.</summary>
    public bool IsOpen => Volatile.Read(ref _closing) == 0 && _socket.State == WebSocketState.Open;

    /// <summary>Completes when the link has ended, whichever side ended it.</summary>
    public Task Done => _ended.Task;

    /// <summary>Connects to a host. Throws <see cref="TimeoutException"/> when nothing answers in time.</summary>
    public static async Task<SocketTransport> ConnectAsync(Uri url, TimeSpan? timeout = null)
    {
        var socket = new ClientWebSocket();
        try
        {
            using var limit = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
            try
            {
                await socket.ConnectAsync(url, limit.Token);
            }
            catch (OperationCanceledException) when (limit.IsCancellationRequested)
            {
                throw new TimeoutException($"{url} did not answer.");
            }
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new SocketTransport(socket);
    }

    public IDisposable Listen(Action<byte[]> onMessage, Action? onDone = null, Action<Exception>? onError = null)
    {
        var listener = _inbound.Listen(onMessage, onDone, onError);
        if (Interlocked.Exchange(ref _reading, 1) == 0)
        {
            _ = Task.Run(Receive);
        }

        return listener;
    }

    /// <summary>Queues a copy of <paramref name="message"/>, so the caller can reuse its buffer. Does nothing once the link is not open.</summary>
    public void Send(byte[] message)
    {
        if (IsOpen)
        {
            _outbox.Writer.TryWrite([.. message]);
        }
    }

    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closing, 1) == 1)
        {
            return;
        }

        // What is queued goes first, such as a goodbye. A peer that stopped reading cannot hold this up.
        _outbox.Writer.TryComplete();
        await Task.WhenAny(_sending, Task.Delay(CloseWait));
        await Farewell(WebSocketCloseStatus.NormalClosure);
        if (Volatile.Read(ref _reading) == 0)
        {
            Finish();
        }

        await Task.WhenAny(_ended.Task, Task.Delay(CloseWait));
        await _stop.CancelAsync();
        await Task.WhenAll(_sending, _ended.Task);
        _socket.Dispose();
        _stop.Dispose();
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    private static bool IsOver(Exception error) =>
        error is IOException or System.Net.Sockets.SocketException or WebSocketException
            or OperationCanceledException or ObjectDisposedException;

    private async Task Send()
    {
        try
        {
            await foreach (var message in _outbox.Reader.ReadAllAsync(_stop.Token))
            {
                await _socket.SendAsync(message, WebSocketMessageType.Binary, true, _stop.Token);
            }
        }
        catch (Exception error) when (IsOver(error))
        {
            // A failed send ends the socket, and the read loop reports it.
            _socket.Abort();
        }
    }

    private async Task Receive()
    {
        try
        {
            await ReadMessages();
        }
#pragma warning disable CA1031 // Whatever stops the loop, listeners hear of it once and the link ends.
        catch (Exception error)
#pragma warning restore CA1031
        {
            if (!_stop.IsCancellationRequested)
            {
                _inbound.Error(error);
            }

            _socket.Abort();
        }
        finally
        {
            Finish();
        }
    }

    private async Task ReadMessages()
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var part = await _socket.ReceiveAsync(buffer.AsMemory(), _stop.Token);
            if (part.MessageType == WebSocketMessageType.Close)
            {
                await Farewell(WebSocketCloseStatus.NormalClosure);
                return;
            }

            if (message.Length + part.Count > MaxMessage)
            {
                throw new InvalidDataException($"A message is over {MaxMessage} bytes.");
            }

            message.Write(buffer, 0, part.Count);
            if (!part.EndOfMessage)
            {
                continue;
            }

            // A text frame is not something replication sends, so it is somebody else's protocol on
            // our port. Dropping it is kinder than ending the link.
            if (part.MessageType == WebSocketMessageType.Binary)
            {
                _inbound.Message(message.ToArray());
            }

            message.SetLength(0);
        }
    }

    private async Task Farewell(WebSocketCloseStatus status)
    {
        if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        using var limit = new CancellationTokenSource(CloseWait);
        try
        {
            await _socket.CloseOutputAsync(status, null, limit.Token);
        }
        catch (Exception error) when (IsOver(error))
        {
            // The peer is gone or stuck, and the socket is dropped either way.
        }
    }

    private void Finish()
    {
        if (Interlocked.Exchange(ref _finished, 1) == 0)
        {
            _inbound.Done();
            _ended.SetResult();
        }
    }
}
