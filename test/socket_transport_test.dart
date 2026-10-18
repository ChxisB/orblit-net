import 'dart:async';
import 'dart:typed_data';

import 'package:orblit_net/transport.dart';
import 'package:test/test.dart';

import 'support/signals.dart';

/// Runs on the VM with `dart test` and in a browser with
/// `dart test -p chrome test/socket_transport_test.dart`, so both transports
/// face the same checks. It imports `transport.dart` because the package's
/// main library reaches the engine's native core, which a browser cannot
/// load.
void main() {
  late Uri echo;
  late Uri dead;
  late Uri blackhole;

  setUp(() async {
    final server = spawnHybridUri('support/echo_server.dart');
    addTearDown(() => server.sink.close());
    final ports = await server.stream.first as Map<Object?, Object?>;
    // dart2wasm hands numbers across the channel as doubles.
    Uri at(String name) =>
        Uri.parse('ws://127.0.0.1:${(ports[name]! as num).toInt()}');
    echo = at('port');
    dead = at('deadPort');
    blackhole = at('blackholePort');
  });

  Future<SocketTransport> connected() async {
    final transport = await SocketTransport.connect(echo);
    addTearDown(transport.close);
    return transport;
  }

  test('a binary message comes back whole', () async {
    final transport = await connected();
    final reply = transport.inbound.first;

    transport.send(Uint8List.fromList([1, 2, 3]));

    expect(await reply, [1, 2, 3]);
  });

  test('a large message is not split', () async {
    final transport = await connected();
    final big = Uint8List.fromList([for (var i = 0; i < 200000; i++) i % 251]);
    final reply = transport.inbound.first;

    transport.send(big);

    expect(await reply, big);
  });

  test('a message is copied when sent, so the buffer can be reused', () async {
    final transport = await connected();
    final buffer = Uint8List.fromList([7, 7, 7]);
    final reply = transport.inbound.first;

    transport.send(buffer);
    buffer.fillRange(0, buffer.length, 0);

    expect(await reply, [7, 7, 7]);
  });

  test('text frames are dropped and binary ones keep their order', () async {
    final transport = await connected();
    final replies = transport.inbound.take(2).toList();

    transport
      ..send(Uint8List.fromList([1]))
      ..send(Uint8List.fromList([2]));

    expect(await replies, [
      [1],
      [2],
    ]);
  });

  test('the transport ends when the peer closes', () async {
    final transport = await connected();
    final ended = transport.inbound.drain<void>();

    transport.send(Uint8List.fromList([hangUp]));
    await transport.done;
    await ended;

    expect(transport.isOpen, isFalse);
    expect(() => transport.send(Uint8List(1)), returnsNormally);
  });

  test('closing ends the stream and a second close does nothing', () async {
    final transport = await connected();
    final ended = transport.inbound.drain<void>();

    await transport.close();
    await ended;

    expect(transport.isOpen, isFalse);
    await transport.close();
  });

  test('closing does not wait for a peer that has stopped reading', () async {
    final transport = await connected();
    transport.send(Uint8List.fromList([stall]));

    await transport.close().timeout(const Duration(seconds: 5));

    expect(transport.isOpen, isFalse);
  });

  test('connecting to a port nobody listens on fails before the timeout', () {
    expect(
      SocketTransport.connect(dead, timeout: const Duration(seconds: 3)),
      throwsA(allOf(isException, isNot(isA<TimeoutException>()))),
    );
  });

  test('an address the transport cannot use fails as an exception', () {
    expect(
      SocketTransport.connect(Uri.parse('ftp://127.0.0.1:1')),
      throwsException,
    );
  });

  test('connecting to a peer that never answers times out', () {
    expect(
      SocketTransport.connect(
        blackhole,
        timeout: const Duration(milliseconds: 200),
      ),
      throwsA(isA<TimeoutException>()),
    );
  });
}
