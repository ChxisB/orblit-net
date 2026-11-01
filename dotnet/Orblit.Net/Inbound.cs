namespace Orblit.Net;

/// <summary>
/// The listeners of one transport. Every listener hears what arrives while it is listening, and
/// one that was stopped never hears another call.
/// </summary>
internal sealed class Inbound
{
    private readonly object _gate = new();
    private Listener[] _listeners = [];

    internal IDisposable Listen(Action<byte[]> onMessage, Action? onDone, Action<Exception>? onError)
    {
        var listener = new Listener(this, onMessage, onDone, onError);
        lock (_gate)
        {
            _listeners = [.. _listeners, listener];
        }

        return listener;
    }

    internal void Message(byte[] message)
    {
        foreach (var listener in Current())
        {
            listener.Hear(message);
        }
    }

    internal void Done()
    {
        foreach (var listener in Current())
        {
            listener.End();
        }
    }

    internal void Error(Exception error)
    {
        foreach (var listener in Current())
        {
            listener.Fail(error);
        }
    }

    private Listener[] Current()
    {
        lock (_gate)
        {
            return _listeners;
        }
    }

    private void Remove(Listener listener)
    {
        lock (_gate)
        {
            _listeners = [.. _listeners.Where(other => other != listener)];
        }
    }

    private sealed class Listener(
        Inbound owner, Action<byte[]> onMessage, Action? onDone, Action<Exception>? onError) : IDisposable
    {
        private volatile bool _stopped;

        public void Hear(byte[] message)
        {
            if (!_stopped)
            {
                onMessage(message);
            }
        }

        public void End()
        {
            if (!_stopped)
            {
                onDone?.Invoke();
            }
        }

        public void Fail(Exception error)
        {
            if (!_stopped)
            {
                onError?.Invoke(error);
            }
        }

        public void Dispose()
        {
            _stopped = true;
            owner.Remove(this);
        }
    }
}
