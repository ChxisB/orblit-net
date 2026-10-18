import 'dart:async';
import 'dart:io';
import 'dart:typed_data';

import 'transport.dart';

/// A transport over a WebSocket, for every platform with `dart:io`. The
/// browser's version is in `socket_transport_web.dart`, and the package
/// exports whichever one the platform can build.
///
/// WebSockets rather than raw UDP, for now, and the reason is framing: a
/// snapshot is a message with a length, and WebSocket already delivers whole
/// messages in order. Building sequencing, acknowledgement and reassembly on
/// top of datagrams is a real piece of work, and it is not the piece that
/// makes Orblit worth using. When the frame budget says otherwise, this
/// interface is where a datagram transport goes.
class SocketTransport implements Transport {
  SocketTransport(this._socket) {
    _subscription = _socket.listen(
      (message) {
        // A text frame is not something replication sends, so it is somebody
        // else's protocol on our port. Dropping it is kinder than crashing.
        if (message is List<int>) {
          _inbound.add(Uint8List.fromList(message));
        }
      },
      onError: (Object error, StackTrace stack) =>
          _inbound.addError(error, stack),
      onDone: () {
        _closed = true;
        if (!_inbound.isClosed) _inbound.close();
      },
      cancelOnError: false,
    );
  }

  /// Connects to a host.
  static Future<SocketTransport> connect(
    Uri url, {
    Duration timeout = const Duration(seconds: 10),
  }) async {
    final socket = await WebSocket.connect(url.toString()).timeout(timeout);
    return SocketTransport(socket);
  }

  final WebSocket _socket;
  final StreamController<Uint8List> _inbound =
      StreamController<Uint8List>.broadcast();
  late final StreamSubscription<dynamic> _subscription;
  bool _closed = false;

  bool get isOpen => !_closed && _socket.readyState == WebSocket.open;

  /// Completes when the peer goes away.
  Future<void> get done => _socket.done;

  @override
  Stream<Uint8List> get inbound => _inbound.stream;

  @override
  void send(Uint8List message) {
    if (!isOpen) return;
    _socket.add(message);
  }

  @override
  Future<void> close() async {
    if (_closed) return;
    _closed = true;
    await _subscription.cancel();
    if (!_inbound.isClosed) await _inbound.close();
    await _socket.close();
  }
}
