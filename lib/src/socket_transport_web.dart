import 'dart:async';
import 'dart:js_interop';
import 'dart:typed_data';

import 'transport.dart';

/// The browser's WebSocket, reached directly. `package:web` would add a
/// dependency for the handful of members used here.
@JS('WebSocket')
extension type _WebSocket._(JSObject _) implements JSObject {
  external _WebSocket(String url);
  external set binaryType(String value);
  external int get readyState;
  external void send(JSUint8Array data);
  external void close();
  external set onopen(JSFunction? handler);
  external set onmessage(JSFunction? handler);
  external set onerror(JSFunction? handler);
  external set onclose(JSFunction? handler);
}

@JS('MessageEvent')
extension type _MessageEvent._(JSObject _) implements JSObject {
  external JSAny? get data;
}

/// The value of `WebSocket.OPEN`.
const int _open = 1;

/// A transport over the browser's WebSocket. It has the members of the
/// `dart:io` one except the constructor, which takes a socket only a host can
/// have. Use [connect].
// final: the dart:io one stays open so existing code that extends it builds,
// but nothing should extend either.
final class SocketTransport implements Transport {
  SocketTransport._(this._socket) {
    _socket
      ..binaryType = 'arraybuffer'
      ..onmessage = _onMessage.toJS
      ..onclose = _onClose.toJS;
  }

  /// Connects to a host. Fails with an [Exception] when the browser refuses
  /// the address or cannot reach it, and with a [TimeoutException] when it
  /// does not answer within [timeout].
  static Future<SocketTransport> connect(
    Uri url, {
    Duration timeout = const Duration(seconds: 10),
  }) async {
    final _WebSocket socket;
    try {
      socket = _WebSocket(url.toString());
    } catch (_) {
      // The constructor throws for a bad scheme or for `ws` from an `https`
      // page, as a JavaScript error that is not an Exception.
      throw Exception('The browser refused $url.');
    }
    final opened = Completer<SocketTransport>();
    socket.onopen = (() => opened.complete(SocketTransport._(socket))).toJS;
    // The browser says nothing about why, so there is nothing to pass on. Once
    // open, an error does nothing here: a close event always follows it, and
    // that ends the transport.
    socket.onerror = (() {
      if (!opened.isCompleted) {
        opened.completeError(Exception('Could not connect to $url.'));
      }
    }).toJS;
    return await opened.future.timeout(
      timeout,
      onTimeout: () {
        socket.close();
        throw TimeoutException('Connecting to $url timed out.', timeout);
      },
    );
  }

  final _WebSocket _socket;
  final StreamController<Uint8List> _inbound =
      StreamController<Uint8List>.broadcast();
  final Completer<void> _gone = Completer<void>();
  bool _closed = false;

  bool get isOpen => !_closed && _socket.readyState == _open;

  /// Completes when the connection ends, from either side.
  Future<void> get done => _gone.future;

  @override
  Stream<Uint8List> get inbound => _inbound.stream;

  void _onMessage(_MessageEvent event) {
    final data = event.data;
    // A text frame is somebody else's protocol on our port, so it is dropped
    // here as it is on the other platform.
    if (data.isA<JSArrayBuffer>()) {
      _inbound.add((data as JSArrayBuffer).toDart.asUint8List());
    }
  }

  void _onClose() {
    _closed = true;
    if (!_inbound.isClosed) _inbound.close();
    if (!_gone.isCompleted) _gone.complete();
  }

  @override
  void send(Uint8List message) {
    if (!isOpen) return;
    // `send` copies the bytes before it returns, so the caller keeps its
    // buffer.
    _socket.send(message.toJS);
  }

  /// Ends the transport at once. The browser finishes the closing handshake
  /// in its own time, and [done] completes when it has.
  @override
  Future<void> close() async {
    if (_closed) return;
    _closed = true;
    _socket.onmessage = null;
    _socket.close();
    await _inbound.close();
  }
}
