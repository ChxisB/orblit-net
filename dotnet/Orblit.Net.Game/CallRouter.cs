using Orblit.Scene;

namespace Orblit.Net.Game;

/// <summary>
/// The handlers one end holds and the calls that have reached it. A call arrives on the transport's
/// thread, waits in a queue, and runs on the game's thread at the start of its next update.
/// </summary>
/// <remarks>
/// Running on the game's thread means a handler touches the scene, the variables and the game's own
/// state without a lock, as any other game code does.
/// </remarks>
internal sealed class CallRouter
{
    private readonly Dictionary<string, Handler> _handlers = [];
    private readonly Queue<(NetCaller Caller, CallMessage Call)> _inbound = new();
    private readonly object _lock = new();
    private int _malformed;
    private int _unhandled;

    /// <summary>Calls that could not be read, or named an entity the scene lacks, or did not fit their handler.</summary>
    public int Malformed
    {
        get
        {
            lock (_lock)
            {
                return _malformed;
            }
        }
    }

    /// <summary>Calls for a name nothing handles.</summary>
    public int Unhandled
    {
        get
        {
            lock (_lock)
            {
                return _unhandled;
            }
        }
    }

    /// <summary>Holds <paramref name="handler"/> under a name. A second one under the same name replaces it.</summary>
    /// <param name="parameters">The argument types the handler takes, or null when it checks them itself.</param>
    public void On(string name, NetHandler handler, Type[]? parameters = null) => _handlers[name] = new Handler(handler, parameters);

    /// <summary>Queues a call that arrived. Any thread.</summary>
    public void Enqueue(NetCaller caller, CallMessage call)
    {
        lock (_lock)
        {
            _inbound.Enqueue((caller, call));
        }
    }

    /// <summary>Counts a message that could not be used as a call. Any thread.</summary>
    public void Reject()
    {
        lock (_lock)
        {
            _malformed++;
        }
    }

    /// <summary>Runs every queued call. Game thread.</summary>
    /// <param name="document">The scene the entities in a call must be in, or null when there is none to check.</param>
    public void Dispatch(SceneDocument? document)
    {
        foreach (var (caller, call) in TakeInbound())
        {
            Deliver(caller, call, document);
        }
    }

    /// <summary>Runs a call now, as it is delivered once it has been through the queue. Game thread.</summary>
    public void Run(NetCaller caller, CallMessage call, SceneDocument? document) => Deliver(caller, call, document);

    private (NetCaller Caller, CallMessage Call)[] TakeInbound()
    {
        lock (_lock)
        {
            var batch = _inbound.ToArray();
            _inbound.Clear();
            return batch;
        }
    }

    private void Deliver(NetCaller caller, CallMessage call, SceneDocument? document)
    {
        if (!_handlers.TryGetValue(call.Name, out var handler))
        {
            lock (_lock)
            {
                _unhandled++;
            }
        }
        else if (!handler.Accepts(call.Args) || !InScene(call.Args, document))
        {
            Reject();
        }
        else
        {
            handler.Run(caller, call.Args);
        }
    }

    private static bool InScene(IEnumerable<object> args, SceneDocument? document) =>
        document is null || args.OfType<NetEntity>().All(entity => document.Contains(entity.Id));

    private sealed record Handler(NetHandler Run, Type[]? Parameters)
    {
        public bool Accepts(object[] args) =>
            Parameters is null || (Parameters.Length == args.Length && args.Zip(Parameters).All(pair => pair.First.GetType() == pair.Second));
    }
}
