namespace Orblit.Net;

/// <summary>
/// What one client may send the host in a tick.
/// </summary>
/// <remarks>
/// The limits are per tick, not per second, so the host's clock stays a parameter. A game that
/// thinks in seconds divides by its tick rate. The defaults are loose: they stop a flood, not a
/// busy client, for a host that ticks 10 to 60 times a second. A host that ticks slower raises
/// the per-tick limits. A game tightens them from what the host's counters show.
/// Everything a client sends counts, acknowledgements and the join included, so
/// <see cref="BytesPerMessage"/> must fit the game's credentials. A message over a limit has
/// already been received by the transport. This stops the host decoding and applying it, not the
/// transport reading it.
/// </remarks>
public sealed class InboundBudget
{
    /// <param name="messagesPerTick">Messages a client may send between two ticks.</param>
    /// <param name="bytesPerMessage">The largest single message. A larger one is dropped unread.</param>
    /// <param name="bytesPerTick">Bytes a client may send between two ticks, all messages together.</param>
    /// <param name="strikes">
    /// How far over its record a client may get before it is cut off. A tick with something
    /// dropped adds one. A tick with nothing dropped takes one off. A burst shorter than this does
    /// no harm, and a client cannot dodge the limit with the odd quiet tick.
    /// </param>
    public InboundBudget(
        int messagesPerTick = 100,
        int bytesPerMessage = 64 * 1024,
        int bytesPerTick = 256 * 1024,
        int strikes = 10)
    {
        MessagesPerTick = AtLeastOne(messagesPerTick, nameof(messagesPerTick));
        BytesPerMessage = AtLeastOne(bytesPerMessage, nameof(bytesPerMessage));
        BytesPerTick = AtLeastOne(bytesPerTick, nameof(bytesPerTick));
        Strikes = AtLeastOne(strikes, nameof(strikes));
    }

    public int MessagesPerTick { get; }

    public int BytesPerMessage { get; }

    public int BytesPerTick { get; }

    public int Strikes { get; }

    private static int AtLeastOne(int value, string name) =>
        value < 1 ? throw new ArgumentOutOfRangeException(name, value, "Must be 1 or more") : value;
}

/// <summary>One client's count against an <see cref="InboundBudget"/>.</summary>
internal sealed class BudgetMeter
{
    private int _messages;
    private int _bytes;
    private bool _refused;
    private int _strikes;

    /// <summary>Whether a message of <paramref name="bytes"/> fits in what is left of this tick.</summary>
    public bool Allows(InboundBudget budget, int bytes) =>
        bytes <= budget.BytesPerMessage
        && _messages < budget.MessagesPerTick
        && (long)_bytes + bytes <= budget.BytesPerTick;

    public void Count(int bytes)
    {
        _messages++;
        _bytes += bytes;
    }

    /// <summary>Marks this tick as over budget.</summary>
    public void Refuse() => _refused = true;

    /// <summary>Closes the tick: a strike if anything was refused, one forgiven if not.</summary>
    public void EndTick()
    {
        if (_refused)
        {
            _strikes++;
        }
        else if (_strikes > 0)
        {
            _strikes--;
        }

        _messages = 0;
        _bytes = 0;
        _refused = false;
    }

    public bool IsOver(InboundBudget budget) => _strikes >= budget.Strikes;
}
