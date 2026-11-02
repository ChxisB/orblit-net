using Orblit.Game;
using Orblit.Scene;

namespace Orblit.Net.Game;

/// <summary>A named value on a tagged entity that the server writes and every client reads.</summary>
/// <remarks>
/// Get one from <see cref="NetVars.Var{T}"/>. A handle stays good while its entity lives. If the
/// entity is removed and added again, ask for a new one.
/// </remarks>
public sealed class NetVar<T>
    where T : notnull
{
    private readonly VarCell _cell;
    private readonly Func<bool> _mayWrite;

    internal NetVar(VarCell cell, Func<bool> mayWrite)
    {
        _cell = cell;
        _mayWrite = mayWrite;
    }

    /// <summary>
    /// The value. On a client it is the last one the server sent, shown the way the entity's place is.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A client sets a variable that the server holds the authority over, or an owner entity it does
    /// not own.
    /// </exception>
    public T Value
    {
        get => NetVarCodec.Decode<T>(_cell.Bytes);
        set
        {
            if (!_mayWrite())
            {
                throw new InvalidOperationException("Only the server writes this variable, or the client that owns its entity.");
            }

            _cell.Bytes = NetVarCodec.Encode(value);
        }
    }
}

/// <summary>What the two ends of a networked game keep their variables in.</summary>
internal interface IVarHost
{
    VarStore Vars { get; }

    SceneDocument Document { get; }

    bool MayWrite(string entityId);
}

public static class NetVars
{
    /// <summary>
    /// The variable <paramref name="name"/> on an entity. The name must be in the schema both ends
    /// were built from, and the entity's <c>net</c> component must list it in <c>sync</c>.
    /// </summary>
    /// <remarks>
    /// With a net that shares nothing, such as <see cref="OfflineNet"/>, the handle holds its own
    /// value and nothing else sees it, so a game written for a network still runs on its own.
    /// </remarks>
    /// <exception cref="ArgumentException">The name is not declared, or is declared with another type.</exception>
    /// <exception cref="InvalidOperationException">The entity is in the scene and does not share the name.</exception>
    public static NetVar<T> Var<T>(this IGameNet net, string entityId, string name)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(net);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var kind = NetVarCodec.KindOf<T>();
        if (net is not IVarHost host)
        {
            return new NetVar<T>(new VarCell(kind), static () => true);
        }

        Declared(host.Vars.Schema, name, kind);
        Shared(host.Document, entityId, name);
        return new NetVar<T>(host.Vars.Cell(entityId, name, kind), () => host.MayWrite(entityId));
    }

    private static void Declared(NetVarSchema schema, string name, NetVarKind kind)
    {
        if (schema.KindOf(name) is not { } declared)
        {
            throw new ArgumentException($"\"{name}\" is not in the schema this game was built with.", nameof(name));
        }

        if (declared != kind)
        {
            throw new ArgumentException($"\"{name}\" is a {declared} variable, not a {kind}.", nameof(name));
        }
    }

    // An entity the scene does not hold yet may be one the server is about to spawn, so it is let
    // through. One that is there and does not share the name would never send it.
    private static void Shared(SceneDocument scene, string entityId, string name)
    {
        if (scene[entityId] is { } entity && !(entity[SceneComponents.Net] is NetComponent tag && tag.Sync.Contains(name)))
        {
            throw new InvalidOperationException($"\"{entityId}\" does not share \"{name}\". Add it to the sync list of its net component.");
        }
    }
}
