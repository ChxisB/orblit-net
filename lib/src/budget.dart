/// What one client may send the host in a tick.
///
/// The limits are per tick, not per second, so the host's clock stays a
/// parameter. A game that thinks in seconds divides by its tick rate. The
/// defaults are loose: they stop a flood, not a busy client. A game tightens
/// them from what the host's counters show.
final class InboundBudget {
  InboundBudget({
    this.messagesPerTick = 100,
    this.bytesPerMessage = 64 * 1024,
    this.bytesPerTick = 256 * 1024,
    this.strikes = 10,
  }) {
    _atLeastOne(messagesPerTick, 'messagesPerTick');
    _atLeastOne(bytesPerMessage, 'bytesPerMessage');
    _atLeastOne(bytesPerTick, 'bytesPerTick');
    _atLeastOne(strikes, 'strikes');
  }

  /// Messages a client may send between two ticks.
  final int messagesPerTick;

  /// The largest single message. A larger one is dropped unread.
  final int bytesPerMessage;

  /// Bytes a client may send between two ticks, all messages together.
  final int bytesPerTick;

  /// Ticks in a row with something dropped before the client is cut off. A
  /// burst shorter than this does no harm.
  final int strikes;

  static void _atLeastOne(int value, String name) {
    if (value < 1) throw ArgumentError.value(value, name, 'Must be 1 or more');
  }
}

/// One client's count against an [InboundBudget].
final class BudgetMeter {
  int _messages = 0;
  int _bytes = 0;
  bool _refused = false;
  int _strikes = 0;

  /// Whether a message of [bytes] fits in what is left of this tick.
  bool allows(InboundBudget budget, int bytes) {
    return bytes <= budget.bytesPerMessage &&
        _messages < budget.messagesPerTick &&
        _bytes + bytes <= budget.bytesPerTick;
  }

  void count(int bytes) {
    _messages++;
    _bytes += bytes;
  }

  /// Marks this tick as over budget.
  void refuse() => _refused = true;

  /// Closes the tick: a strike if anything was refused, none otherwise.
  void endTick() {
    _strikes = _refused ? _strikes + 1 : 0;
    _messages = 0;
    _bytes = 0;
    _refused = false;
  }

  bool isOver(InboundBudget budget) => _strikes >= budget.strikes;
}
