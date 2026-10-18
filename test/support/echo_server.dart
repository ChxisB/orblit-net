import 'dart:async';
import 'dart:io';

import 'package:stream_channel/stream_channel.dart';

import 'signals.dart';

/// A WebSocket server on the VM for a test that runs in a browser. It answers
/// every binary message with a text frame and then the same bytes, so a
/// client that does not drop text frames shows it. It sends the port it
/// listens on, a port nothing listens on and a port that accepts a
/// connection and never answers.
Future<void> hybridMain(StreamChannel<Object?> channel) async {
  final echo = await HttpServer.bind('127.0.0.1', 0);
  final closed = await HttpServer.bind('127.0.0.1', 0);
  final blackhole = await ServerSocket.bind('127.0.0.1', 0);
  final deadPort = closed.port;
  await closed.close();

  final held = <Socket>[];
  blackhole.listen(held.add);
  echo.listen((request) async {
    final socket = await WebSocketTransformer.upgrade(request);
    late final StreamSubscription<Object?> subscription;
    subscription = socket.listen((message) {
      if (message is! List<int>) return;
      if (message.length == 1 && message.first == hangUp) {
        socket.close();
        return;
      }
      if (message.length == 1 && message.first == stall) {
        subscription.pause();
        return;
      }
      socket
        ..add('noise')
        ..add(message);
    });
  });

  channel.sink.add({
    'port': echo.port,
    'deadPort': deadPort,
    'blackholePort': blackhole.port,
  });
  await channel.sink.done;
  await echo.close(force: true);
  await blackhole.close();
  for (final socket in held) {
    socket.destroy();
  }
}
