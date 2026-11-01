using System.Runtime.ExceptionServices;

namespace Orblit.Net;

/// <summary>
/// Two transports wired to each other in this process.
/// </summary>
/// <remarks>
/// Useful well beyond tests: a single-player build and a listen server can run the real
/// replication path over a loopback link, so the networked code path is the only code path and does
/// not rot when nobody is testing multiplayer. Messages are delivered one at a time, in the order
/// they were sent, on a pool thread. A handler that throws does not stop delivery. The exception
/// comes out of the next <see cref="IdleAsync"/>.
/// </remarks>
public sealed class LoopbackLink
{
    private readonly object _gate = new();
    private readonly Queue<Action> _queue = [];
    private readonly List<TaskCompletionSource> _idle = [];
    private Task _lastDelayed = Task.CompletedTask;
    private int _pending;
    private bool _draining;
    private ExceptionDispatchInfo? _fault;

    /// <param name="latency">
    /// Delay applied to every message in each direction, for exercising interpolation and
    /// reconciliation without a network.
    /// </param>
    public LoopbackLink(TimeSpan latency = default)
    {
        Latency = latency;
        var left = new Side(this);
        var right = new Side(this);
        left.Peer = right;
        right.Peer = left;
        Host = left;
        Client = right;
    }

    public TimeSpan Latency { get; }

    /// <summary>Whether nothing is waiting to be delivered.</summary>
    internal bool IsIdle
    {
        get
        {
            lock (_gate)
            {
                return _pending == 0;
            }
        }
    }

    public ITransport Host { get; }

    public ITransport Client { get; }

    /// <summary>Closes both ends.</summary>
    public async Task CloseAsync()
    {
        await Host.CloseAsync();
        await Client.CloseAsync();
    }

    /// <summary>
    /// Completes once everything sent so far, and everything that sending caused, has been
    /// delivered. Tests await it, because a handler runs on a pool thread.
    /// </summary>
    public async Task IdleAsync()
    {
        Task wait;
        lock (_gate)
        {
            if (_pending == 0)
            {
                wait = Task.CompletedTask;
            }
            else
            {
                var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _idle.Add(waiter);
                wait = waiter.Task;
            }
        }

        await wait;
        lock (_gate)
        {
            var fault = _fault;
            _fault = null;
            fault?.Throw();
        }
    }

    private void Schedule(Action work)
    {
        lock (_gate)
        {
            _pending++;
            if (Latency != TimeSpan.Zero)
            {
                _lastDelayed = DelayThenEnqueue(_lastDelayed, work);
                return;
            }
        }

        Enqueue(work);
    }

    // Timers that fire together run their continuations in any order, so each message also waits
    // for the one sent before it.
    private async Task DelayThenEnqueue(Task before, Action work)
    {
        await Task.Delay(Latency);
        await before;
        Enqueue(work);
    }

    private void Enqueue(Action work)
    {
        bool start;
        lock (_gate)
        {
            _queue.Enqueue(work);
            start = !_draining;
            _draining = true;
        }

        if (start)
        {
            _ = Task.Run(Drain);
        }
    }

    private void Drain()
    {
        while (true)
        {
            Action work;
            lock (_gate)
            {
                if (!_queue.TryDequeue(out work!))
                {
                    _draining = false;
                    return;
                }
            }

            Run(work);
        }
    }

    private void Run(Action work)
    {
        try
        {
            work();
        }
#pragma warning disable CA1031 // A handler's fault must not stop delivery. IdleAsync rethrows it.
        catch (Exception error)
#pragma warning restore CA1031
        {
            lock (_gate)
            {
                _fault ??= ExceptionDispatchInfo.Capture(error);
            }
        }
        finally
        {
            Finished();
        }
    }

    private void Finished()
    {
        TaskCompletionSource[] release;
        lock (_gate)
        {
            _pending--;
            if (_pending > 0)
            {
                return;
            }

            release = [.. _idle];
            _idle.Clear();
        }

        foreach (var waiter in release)
        {
            waiter.SetResult();
        }
    }

    private sealed class Side(LoopbackLink link) : ITransport
    {
        private readonly Inbound _inbound = new();
        private volatile bool _closed;

        internal Side Peer { get; set; } = null!;

        public IDisposable Listen(Action<byte[]> onMessage, Action? onDone = null, Action<Exception>? onError = null) =>
            _inbound.Listen(onMessage, onDone, onError);

        public void Send(byte[] message)
        {
            if (_closed)
            {
                return;
            }

            // Copied on the way out: the caller is free to reuse its buffer, which the capture path
            // does on every tick.
            var copy = (byte[])message.Clone();
            link.Schedule(() => Peer.Deliver(copy));
        }

        public Task CloseAsync()
        {
            if (_closed)
            {
                return Task.CompletedTask;
            }

            _closed = true;
            link.Schedule(_inbound.Done);
            return Task.CompletedTask;
        }

        private void Deliver(byte[] message)
        {
            if (!_closed)
            {
                _inbound.Message(message);
            }
        }
    }
}
