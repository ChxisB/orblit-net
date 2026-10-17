import 'dart:typed_data';

import 'package:orblit_core/orblit_core.dart';
import 'package:orblit_net/orblit_net.dart';
import 'package:test/test.dart';

import 'support/loopback.dart';

Uint8List bytes(List<int> values) => Uint8List.fromList(values);

void main() {
  late World authority;
  late World replica;
  late LoopbackLink link;
  late NetHost host;
  late NetClient client;
  late List<(String, Uint8List)> atHost;
  late List<Uint8List> atClient;

  void build({InboundBudget? budget, bool listening = true}) {
    atHost = [];
    atClient = [];
    authority = World();
    replica = World();
    link = LoopbackLink();
    host = NetHost(
      world: authority,
      set: ReplicationSet([]),
      networkId: authority.registerComponent(
        'NetworkId',
        kind: ComponentKind.int64,
      ),
      budget: budget,
      onMessage: listening ? (id, body) => atHost.add((id, body)) : null,
    );
    client = NetClient(
      world: replica,
      set: ReplicationSet([]),
      networkId: replica.registerComponent(
        'NetworkId',
        kind: ComponentKind.int64,
      ),
      transport: link.client,
      onMessage: listening ? atClient.add : null,
    );
    host.addClient('a', link.host);
  }

  tearDown(() async {
    await host.dispose();
    await link.close();
    authority.dispose();
    replica.dispose();
  });

  test(
    'a client message reaches the host with the sender and the bytes',
    () async {
      build();
      client.sendMessage(bytes([1, 2, 3]));
      await settle();
      expect(atHost, hasLength(1));
      expect(atHost.single.$1, 'a');
      expect(atHost.single.$2, [1, 2, 3]);
    },
  );

  test('a host message reaches the client', () async {
    build();
    host.sendMessage('a', bytes([9, 8]));
    await settle();
    expect(atClient, [
      [9, 8],
    ]);
  });

  test('an empty message arrives empty', () async {
    build();
    client.sendMessage(bytes([]));
    host.sendMessage('a', bytes([]));
    await settle();
    expect(atHost.single.$2, isEmpty);
    expect(atClient.single, isEmpty);
  });

  test('messages arrive in the order they were sent', () async {
    build();
    for (var i = 0; i < 5; i++) {
      host.sendMessage('a', bytes([i]));
    }
    await settle();
    expect([for (final m in atClient) m.single], [0, 1, 2, 3, 4]);
  });

  test('a message to a client that is not attached is dropped', () async {
    build();
    host.sendMessage('nobody', bytes([1]));
    await settle();
    expect(atClient, isEmpty);
  });

  test(
    'messages and snapshots share a link without disturbing each other',
    () async {
      build();
      host.sendMessage('a', bytes([1]));
      host.publish();
      host.sendMessage('a', bytes([2]));
      client.sendMessage(bytes([3]));
      await settle();
      expect(atClient, hasLength(2));
      expect(client.lastAppliedTick, 1);
      expect(atHost, hasLength(1));
    },
  );

  test('a host and client with no handler ignore messages', () async {
    build(listening: false);
    client.sendMessage(bytes([1]));
    host.sendMessage('a', bytes([2]));
    host.publish();
    await settle();
    expect(client.lastAppliedTick, 1);
  });

  test('a message over the byte budget is dropped and counted', () async {
    build(budget: InboundBudget(bytesPerMessage: 8));
    client.sendMessage(Uint8List(100));
    await settle();
    expect(atHost, isEmpty);
    expect(host.rejections[InputRejection.overBudget], 1);
  });

  test('messages over the count budget are dropped and counted', () async {
    build(budget: InboundBudget(messagesPerTick: 3));
    for (var i = 0; i < 10; i++) {
      client.sendMessage(bytes([i]));
    }
    await settle();
    expect(atHost, hasLength(3));
    expect(host.rejections[InputRejection.overBudget], 7);
  });
}
