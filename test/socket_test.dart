import 'dart:io';
import 'dart:typed_data';

import 'package:orblit_core/orblit_core.dart';
import 'package:orblit_net/orblit_net.dart';
import 'package:test/test.dart';

/// A world and the components it replicates, registered in opposite orders on
/// the two sides so the wire cannot depend on local ids.
class Peer {
  Peer({required bool reversed}) {
    world = World();
    if (reversed) {
      position = world.registerComponent(
        'Position',
        kind: ComponentKind.float32,
        arity: 3,
      );
      networkId = world.registerComponent(
        'NetworkId',
        kind: ComponentKind.int64,
      );
    } else {
      networkId = world.registerComponent(
        'NetworkId',
        kind: ComponentKind.int64,
      );
      position = world.registerComponent(
        'Position',
        kind: ComponentKind.float32,
        arity: 3,
      );
    }
    set = ReplicationSet([position]);
  }

  late final World world;
  late final ComponentType networkId;
  late final ComponentType position;
  late final ReplicationSet set;

  void dispose() => world.dispose();
}

/// Waits for a condition rather than sleeping a guessed amount, so the test
/// neither flakes on a slow machine nor wastes time on a fast one.
Future<void> until(bool Function() done, {String reason = 'condition'}) async {
  for (var attempt = 0; attempt < 200; attempt++) {
    if (done()) return;
    await Future<void>.delayed(const Duration(milliseconds: 10));
  }
  fail('Timed out waiting for $reason.');
}

void main() {
  test('a session replicates over a real socket', () async {
    final server = await SocketServer.bind();
    addTearDown(server.close);

    // Port zero means the operating system picks, so two tests never collide.
    expect(server.port, greaterThan(0));

    final host = Peer(reversed: false);
    final client = Peer(reversed: true);
    addTearDown(host.dispose);
    addTearDown(client.dispose);

    final accepted = server.connections.first;
    final clientTransport = await SocketTransport.connect(server.url);
    final hostTransport = await accepted;
    addTearDown(clientTransport.close);
    addTearDown(hostTransport.close);

    final netHost = NetHost(
      world: host.world,
      set: host.set,
      networkId: host.networkId,
    );
    final netClient = NetClient(
      world: client.world,
      set: client.set,
      networkId: client.networkId,
      transport: clientTransport,
    );
    netHost.addClient('player-1', hostTransport);
    addTearDown(() async {
      await netClient.dispose();
      await netHost.dispose();
    });

    final entity = host.world.createEntity();
    final networkId = netHost.spawn(entity);
    host.world.add(entity, host.position, Float32List.fromList([1, 2, 3]));

    netHost.publish();
    await until(
      () => netClient.entityFor(networkId) != null,
      reason: 'the spawn to arrive',
    );

    final replica = netClient.entityFor(networkId)!;
    expect(client.world.float32Of(replica, client.position), [1, 2, 3]);

    // The acknowledgement travelled back, so the next publish is a delta.
    await until(() => netClient.lastAppliedTick == 1, reason: 'the first tick');
    host.world.float32Of(entity, host.position)![0] = 9;
    netHost.publish();
    await until(
      () => client.world.float32Of(replica, client.position)![0] == 9,
      reason: 'the update to arrive',
    );
  });

  test('a plain HTTP request is answered, not ignored', () async {
    final server = await SocketServer.bind();
    addTearDown(server.close);

    final client = HttpClientForTest();
    final status = await client.get(server.port);
    expect(
      status,
      400,
      reason:
          'a browser pointed at the game port should be told why '
          'nothing happens',
    );
  });

  test('sending on a closed transport is a no-op, not a throw', () async {
    final server = await SocketServer.bind();
    addTearDown(server.close);

    final transport = await SocketTransport.connect(server.url);
    await transport.close();

    expect(transport.isOpen, isFalse);
    expect(() => transport.send(Uint8List(4)), returnsNormally);
  });

  test('a session survives a dropped socket and resumes', () async {
    final server = await SocketServer.bind();
    addTearDown(server.close);

    final authority = Peer(reversed: false);
    final replica = Peer(reversed: true);
    addTearDown(authority.dispose);
    addTearDown(replica.dispose);

    final netHost = NetHost(
      world: authority.world,
      set: authority.set,
      networkId: authority.networkId,
      sessions: SessionPolicy(
        admit: (bytes) => Admit(String.fromCharCodes(bytes)),
        lingerTicks: 100,
      ),
    );
    addTearDown(netHost.dispose);
    final accepted = server.connections.listen(netHost.accept);
    addTearDown(accepted.cancel);

    NetClient newClient(Transport transport) => NetClient(
      world: replica.world,
      set: replica.set,
      networkId: replica.networkId,
      transport: transport,
    );

    final first = await SocketTransport.connect(server.url);
    final firstClient = newClient(first);
    firstClient.join(Uint8List.fromList('ann'.codeUnits));
    await until(
      () => netHost.sessionState('ann') == SessionState.live,
      reason: 'the join to be admitted',
    );

    await first.close();
    await firstClient.dispose();
    await until(
      () => netHost.sessionState('ann') == SessionState.lingering,
      reason: 'the drop to be noticed',
    );

    final second = await SocketTransport.connect(server.url);
    final secondClient = newClient(second);
    addTearDown(second.close);
    addTearDown(secondClient.dispose);
    secondClient.join(Uint8List.fromList('ann'.codeUnits));
    await until(
      () => netHost.sessionState('ann') == SessionState.live,
      reason: 'the rejoin to resume the session',
    );
    expect(netHost.clientCount, 1);
  });
}

/// A one-request HTTP client, kept here so the test needs no dependency.
class HttpClientForTest {
  Future<int> get(int port) async {
    final client = HttpClient();
    try {
      final request = await client.get('127.0.0.1', port, '/');
      final response = await request.close();
      await response.drain<void>();
      return response.statusCode;
    } finally {
      client.close(force: true);
    }
  }
}
