namespace Orblit.Net;

/// <summary>What a client is told about. <see cref="NetHost"/> asks it, so it is declared beside the host.</summary>
public interface IInterestRule
{
    /// <summary>
    /// Reads the capture every client's update is built from. Called once a tick, before any
    /// <see cref="IsRelevant"/>.
    /// </summary>
    /// <param name="world">The capture.</param>
    /// <param name="focus">The network ids each connected client owns, by client id.</param>
    void Observe(WorldSnapshot world, IReadOnlyDictionary<string, IReadOnlyList<ulong>> focus);

    /// <summary>
    /// Whether <paramref name="networkId"/> belongs in <paramref name="clientId"/>'s update. Called
    /// once per entity per client per tick, and never for an entity the client owns, which the
    /// client always sees.
    /// </summary>
    bool IsRelevant(string clientId, ulong networkId);
}
