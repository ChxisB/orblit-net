namespace Orblit.Net;

/// <summary>
/// A duplex link to one peer.
/// </summary>
/// <remarks>
/// Everything above this interface is about turning a world into bytes and back. Everything below
/// it is about getting bytes to another machine. The seam is narrow, so a game that needs a
/// transport Orblit does not ship can supply one in an afternoon. Callbacks run on whichever thread
/// the transport reads from. <see cref="NetHost"/> and <see cref="NetClient"/> take a lock before
/// they touch their state.
/// </remarks>
public interface ITransport
{
    /// <summary>
    /// Starts hearing the peer. Messages arrive already framed, because framing is the transport's
    /// problem, not replication's. A snapshot is a message with a length, and a link that delivers
    /// half of one has not met this interface. A listener hears only what arrives after it starts.
    /// </summary>
    /// <param name="onMessage">One whole message.</param>
    /// <param name="onDone">The link ended. Called once.</param>
    /// <param name="onError">The link failed. The link ends after it.</param>
    /// <returns>Disposing it stops the listener.</returns>
    IDisposable Listen(Action<byte[]> onMessage, Action? onDone = null, Action<Exception>? onError = null);

    /// <summary>
    /// Sends one message without waiting for it to arrive. Ordering and delivery are the
    /// transport's to promise. Replication is written to need neither: deltas are built against the
    /// tick a client acknowledged, so a message that never arrives costs one larger snapshot rather
    /// than a world that quietly diverges.
    /// </summary>
    void Send(byte[] message);

    Task CloseAsync();
}
