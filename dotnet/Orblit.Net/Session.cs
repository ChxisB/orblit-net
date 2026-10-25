namespace Orblit.Net;

/// <summary>What the game decides about a join.</summary>
public abstract record Admission
{
    private protected Admission()
    {
    }
}

/// <summary>
/// The join is welcome. <paramref name="Key"/> names the player for as long as the session lives,
/// usually an account id, and is the client id the host uses.
/// </summary>
public sealed record Admit(string Key) : Admission;

/// <summary>The join is turned away. <paramref name="Reason"/> is sent to the client as text.</summary>
public sealed record Refuse(string Reason) : Admission;

public enum SessionState
{
    /// <summary>A connection is attached.</summary>
    Live,

    /// <summary>
    /// The connection is gone. The entities stay for <see cref="SessionPolicy.LingerTicks"/> ticks,
    /// and a join with the same key takes them back.
    /// </summary>
    Lingering,
}

/// <summary>Why a session ended.</summary>
public enum GoneReason
{
    /// <summary>It lingered for <see cref="SessionPolicy.LingerTicks"/> ticks and nobody came back.</summary>
    Disconnect,

    /// <summary>The game called <see cref="NetHost.RemoveClient"/>.</summary>
    Removed,

    /// <summary>The host cut the client off for staying over its <see cref="InboundBudget"/>.</summary>
    OverBudget,
}

/// <summary>
/// The reasons a host puts in a goodbye. A game's own refusals may say anything else. The words are
/// on the wire, so they match the Dart build.
/// </summary>
public static class GoodbyeReason
{
    /// <summary>A second join for a key that is attached, on a host without take over.</summary>
    public const string AlreadyLive = "alreadyLive";

    /// <summary>The same key joined again on a host with take over.</summary>
    public const string Replaced = "replaced";

    /// <summary>The game called <see cref="NetHost.RemoveClient"/>.</summary>
    public const string Removed = "removed";

    /// <summary>The game's admit threw.</summary>
    public const string Refused = "refused";

    /// <summary>No join arrived within <see cref="SessionPolicy.JoinTicks"/>.</summary>
    public const string JoinTimeout = "joinTimeout";

    /// <summary>The client stayed over its <see cref="InboundBudget"/>.</summary>
    public const string OverBudget = "overBudget";
}

/// <summary>
/// How a host treats the clients it admits.
/// </summary>
/// <remarks>
/// Orblit checks nothing about who someone is. <see cref="Admit"/> is given the bytes the client
/// sent and answers for the game. A task that is already done is acted on at once. One that is not
/// may take longer, for a password hash or a database, and the host keeps ticking while it waits.
/// The connection stays unjoined, so <see cref="JoinTicks"/> still counts down, and a second join
/// from it is ignored.
/// </remarks>
public sealed class SessionPolicy
{
    /// <summary>
    /// Turns what a client sent at join into a key or a refusal. A throw, or a task that fails,
    /// refuses the join, since the bytes come from a client.
    /// </summary>
    public required Func<byte[], ValueTask<Admission>> Admit { get; init; }

    /// <summary>Host ticks a dropped session keeps its entities.</summary>
    public int LingerTicks
    {
        get;
        init => field = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Must be 0 or more");
    } = 600;

    /// <summary>
    /// Host ticks the session of one key keeps its entities, asked when the connection drops. A
    /// game uses it to keep a player in a fight in the world for longer. Without it every session
    /// gets <see cref="LingerTicks"/>. A negative answer is zero, and a throw gives
    /// <see cref="LingerTicks"/>.
    /// </summary>
    public Func<string, int>? LingerFor { get; init; }

    /// <summary>
    /// Host ticks a connection may stay silent before it sends a join. After that it hears
    /// <see cref="GoodbyeReason.JoinTimeout"/> and is closed.
    /// </summary>
    public int JoinTicks
    {
        get;
        init => field = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Must be 1 or more");
    } = 300;

    /// <summary>
    /// What a second join for a live key does: false refuses it with
    /// <see cref="GoodbyeReason.AlreadyLive"/>, true sends the old connection away with
    /// <see cref="GoodbyeReason.Replaced"/> and resumes the session on the new one.
    /// </summary>
    public bool TakeOver { get; init; }

    /// <summary>A key joined for the first time. The game creates its entities here.</summary>
    public Action<string>? OnJoin { get; init; }

    /// <summary>
    /// A session ended, once. The key still owns its entities during the call, so the game can find
    /// them, save them and despawn them. A throw leaves the session ended and its entities
    /// released, and propagates out of <see cref="NetHost.Publish"/>. The next publish carries on.
    /// </summary>
    public Action<string, GoneReason>? OnGone { get; init; }
}
