import 'dart:typed_data';

import 'package:orblit_core/orblit_core.dart';
import 'package:orblit_net/orblit_net.dart';
import 'package:orblit_net/src/session_message.dart';
import 'package:test/test.dart';

void main() {
  test('a join carries its credentials', () {
    final message = encodeJoin(Uint8List.fromList([1, 2, 3]));
    expect(decodeJoin(message), [1, 2, 3]);
  });

  test('a join with no credentials is still a join', () {
    expect(decodeJoin(encodeJoin(Uint8List(0))), isEmpty);
  });

  test('a goodbye carries its reason', () {
    expect(decodeGoodbye(encodeGoodbye('alreadyLive')), 'alreadyLive');
  });

  test('each message decodes as null for every other kind', () {
    final join = encodeJoin(Uint8List.fromList([1]));
    final goodbye = encodeGoodbye('x');
    final ack = encodeAck(7);
    final input = encodeInput(const InputMessage(tick: 1, entries: []));

    expect(decodeGoodbye(join), isNull);
    expect(decodeJoin(goodbye), isNull);
    for (final other in [ack, input, Uint8List(0), Uint8List(3)]) {
      expect(decodeJoin(other), isNull);
      expect(decodeGoodbye(other), isNull);
    }
    expect(decodeAck(join), isNull);
    expect(decodeInput(join), isNull);
    expect(decodeInput(goodbye), isNull);
  });

  test('a client reports a goodbye once and keeps its world', () async {
    final world = World();
    addTearDown(world.dispose);
    final networkId = world.registerComponent(
      'NetworkId',
      kind: ComponentKind.int64,
    );
    final link = LoopbackLink();
    addTearDown(link.close);
    final reasons = <String>[];
    NetClient(
      world: world,
      set: ReplicationSet([]),
      networkId: networkId,
      transport: link.client,
      onGoodbye: reasons.add,
    );

    link.host.send(encodeGoodbye('alreadyLive'));
    await Future<void>.delayed(Duration.zero);

    expect(reasons, ['alreadyLive']);
  });
}
