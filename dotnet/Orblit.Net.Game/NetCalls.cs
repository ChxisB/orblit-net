using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Orblit.Game;

namespace Orblit.Net.Game;

/// <summary>
/// Runs on the end that received a call. <paramref name="args"/> are the values the caller sent, each
/// a bool, an int, a float, a string, a <see cref="Orblit.VectorMath.Vector3"/> or a <see cref="NetEntity"/>.
/// They come from a client, so check their count and types.
/// </summary>
public delegate void NetHandler(NetCaller caller, IReadOnlyList<object> args);

/// <summary>Who made a call. <see cref="ClientId"/> is null when the server or the game itself did.</summary>
public readonly record struct NetCaller(string? ClientId);

/// <summary>An entity named in a call. The receiving end drops the call when its scene has no such entity.</summary>
public readonly record struct NetEntity(string Id);

/// <summary>What either end of a networked game does with a call. A net that is not one of them runs it here.</summary>
internal interface ICallHost
{
    CallRouter Calls { get; }

    /// <summary>Runs the call on the server.</summary>
    void Call(Outgoing call);
}

/// <summary>What only the server can do: call the clients.</summary>
internal interface IServerCalls : ICallHost
{
    void CallClient(string clientId, Outgoing call);

    void CallAll(Outgoing call);
}

/// <summary>
/// A call a game made, as the call itself for an end that runs it here and as the bytes for an end
/// that sends it. Making one is what checks the arguments, so every end refuses the same calls.
/// </summary>
internal readonly record struct Outgoing(CallMessage Call, byte[] Body);

/// <summary>
/// "Run this on the server" and "run this on that client", for any <see cref="IGameNet"/>.
/// </summary>
/// <remarks>
/// A handler is held under a name. The server holds the ones clients call, and a client holds the
/// ones the server calls. A call travels over the same channel as the scene, so the host's inbound
/// budget applies to it, and it runs on the game's thread at the start of the next update.
/// <para>
/// A game that runs offline is its own server and its own client. Its calls run at once, and
/// <see cref="CallClient"/> and <see cref="CallAll"/> run the handler of the one copy there is.
/// Offline handlers belong to the net, and the game host makes one net for each game, so a game that
/// binds itself again replaces its own handlers by name and no other game sees them.
/// </para>
/// <para>
/// A handler that throws on a queued call does not stop the game. The call is counted in
/// <c>FaultedCalls</c>, the rest of the batch runs, and the exception is kept in <c>LastCallFault</c>.
/// A call the game makes to itself, on the server or offline, throws to its caller.
/// </para>
/// </remarks>
public static class NetCalls
{
    private const BindingFlags Methods =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private const string OnlyTheServerCalls = "Only the server calls a client.";

    private static readonly ConditionalWeakTable<IGameNet, CallRouter> Offline = [];

    /// <summary>Holds <paramref name="handler"/> under <paramref name="name"/>. A second one under the same name replaces it.</summary>
    public static void On(this IGameNet net, string name, NetHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);
        RouterOf(net).On(name, handler);
    }

    /// <summary>Runs the handler named <paramref name="name"/> on the server. On the server it runs at once.</summary>
    /// <exception cref="ArgumentException">The name is empty, or an argument is of a type a call cannot carry.</exception>
    public static void Call(this IGameNet net, string name, params object[] args)
    {
        var call = Made(net, name, args);
        if (net is ICallHost host)
        {
            host.Call(call);
        }
        else
        {
            RunHere(net, call);
        }
    }

    /// <summary>Runs the handler named <paramref name="name"/> on one client.</summary>
    /// <exception cref="InvalidOperationException">Called on a client. Only the server calls a client.</exception>
    public static void CallClient(this IGameNet net, string clientId, string name, params object[] args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        var call = Made(net, name, args);
        if (net is IServerCalls server)
        {
            server.CallClient(clientId, call);
        }
        else
        {
            RunBelow(net, call);
        }
    }

    /// <summary>Runs the handler named <paramref name="name"/> on every client.</summary>
    /// <exception cref="InvalidOperationException">Called on a client. Only the server calls a client.</exception>
    public static void CallAll(this IGameNet net, string name, params object[] args)
    {
        var call = Made(net, name, args);
        if (net is IServerCalls server)
        {
            server.CallAll(call);
        }
        else
        {
            RunBelow(net, call);
        }
    }

    /// <summary>
    /// Holds a handler for each method of <paramref name="game"/> marked <see cref="ServerRpcAttribute"/>
    /// on the server and <see cref="ClientRpcAttribute"/> on a client, under the method's name. Offline
    /// it holds both. Call it from <c>Start</c>.
    /// </summary>
    /// <remarks>
    /// Every marked method is checked on every end, so a bad signature fails the first run and not the
    /// first call. The scan is reflection on the game's own type. That is fine here: it runs once, in the
    /// same process as the game, and the game's types are loaded into a context the editor can drop.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// A marked method does not return void, takes a type a call cannot carry, or shares its name with
    /// another marked method. The message names the method.
    /// </exception>
    public static void Bind(this IGameNet net, object game)
    {
        ArgumentNullException.ThrowIfNull(game);
        var router = RouterOf(net);
        var names = new HashSet<string>();
        foreach (var method in game.GetType().GetMethods(Methods).Where(IsRpc))
        {
            var (handler, parameters) = Describe(game, method);
            if (!names.Add(method.Name))
            {
                throw new ArgumentException($"{Named(method)} shares its name with another marked method. A call finds a method by name.");
            }

            if (Listens(net.Role, method))
            {
                router.On(method.Name, handler, parameters);
            }
        }
    }

    // Writing the bytes is what checks the name and the arguments, so every end refuses the same calls.
    private static Outgoing Made(IGameNet net, string name, object[] args)
    {
        ArgumentNullException.ThrowIfNull(net);
        ArgumentNullException.ThrowIfNull(args);
        return new Outgoing(new CallMessage(name, [.. args]), NetWire.Call(name, args));
    }

    private static void RunHere(IGameNet net, Outgoing call) =>
        RouterOf(net).Run(new NetCaller(null), call.Call, null);

    // A client has no one below it to call. Any other net that is not a server is its own client.
    private static void RunBelow(IGameNet net, Outgoing call)
    {
        if (net is ICallHost)
        {
            throw new InvalidOperationException(OnlyTheServerCalls);
        }

        RunHere(net, call);
    }

    private static CallRouter RouterOf(IGameNet net)
    {
        ArgumentNullException.ThrowIfNull(net);
        return net is ICallHost host ? host.Calls : Offline.GetValue(net, static _ => new CallRouter());
    }

    private static bool IsRpc(MethodInfo method) =>
        method.IsDefined(typeof(ServerRpcAttribute)) || method.IsDefined(typeof(ClientRpcAttribute));

    private static bool Listens(GameRole role, MethodInfo method) => role switch
    {
        GameRole.Server => method.IsDefined(typeof(ServerRpcAttribute)),
        GameRole.Client => method.IsDefined(typeof(ClientRpcAttribute)),
        _ => true,
    };

    private static (NetHandler Handler, Type[] Parameters) Describe(object game, MethodInfo method)
    {
        var parameters = method.GetParameters();
        var takesCaller = parameters.Length > 0 && parameters[0].ParameterType == typeof(NetCaller);
        var types = parameters.Skip(takesCaller ? 1 : 0).Select(parameter => parameter.ParameterType).ToArray();
        if (method.ReturnType != typeof(void))
        {
            throw new ArgumentException($"{Named(method)} returns a value. A call has no reply, so it must return void.");
        }

        if (types.FirstOrDefault(type => !NetCodec.Carries(type)) is { } bad)
        {
            throw new ArgumentException($"{Named(method)} takes a {bad.Name}. A call carries a bool, an int, a float, a string, a Vector3 or a NetEntity.");
        }

        return ((caller, args) => Invoke(game, method, takesCaller ? [caller, .. args] : [.. args]), types);
    }

    // The handler throws what the method threw, not the reflection wrapper, so the host reports the
    // game's own fault.
    private static void Invoke(object game, MethodInfo method, object[] values)
    {
        try
        {
            method.Invoke(game, values);
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
        }
    }

    private static string Named(MethodInfo method) => $"{method.DeclaringType?.Name}.{method.Name}";
}
