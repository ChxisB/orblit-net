import 'dart:async';
import 'dart:io';

import 'socket_transport_io.dart';

// The package exports this file whole, so it keeps one public name.

/// Accepts connections on behalf of an authority.
///
/// Deliberately thin: it hands out transports and says nothing about who is
/// allowed to connect. Authentication belongs to the game, which knows what an
/// account is; the engine only knows what a socket is.
class SocketServer {
  SocketServer._(this._server, this._connections);

  /// Listens on [address] and [port].
  ///
  /// Pass port zero to let the operating system choose one, which is what
  /// tests should do rather than picking a number and hoping.
  static Future<SocketServer> bind({
    Object address = '127.0.0.1',
    int port = 0,
  }) async {
    final server = await HttpServer.bind(address, port);
    final connections = StreamController<SocketTransport>.broadcast();

    server.listen(
      (request) async {
        if (!WebSocketTransformer.isUpgradeRequest(request)) {
          // Something that is not a client asking to play. Answered rather
          // than ignored, so a browser pointed here sees why nothing happens.
          request.response
            ..statusCode = HttpStatus.badRequest
            ..write('This port speaks the Orblit session protocol.');
          await request.response.close();
          return;
        }
        final socket = await WebSocketTransformer.upgrade(request);
        if (!connections.isClosed) {
          connections.add(SocketTransport(socket));
        }
      },
      onError: connections.addError,
      cancelOnError: false,
    );

    return SocketServer._(server, connections);
  }

  final HttpServer _server;
  final StreamController<SocketTransport> _connections;

  /// The port actually bound, which matters when zero was requested.
  int get port => _server.port;

  Uri get url => Uri.parse('ws://${_server.address.host}:${_server.port}');

  /// Each client as it arrives. The caller gives it to the host.
  Stream<SocketTransport> get connections => _connections.stream;

  Future<void> close() async {
    await _connections.close();
    await _server.close(force: true);
  }
}
