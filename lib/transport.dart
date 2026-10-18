/// The part of orblit_net a browser can compile.
///
/// `orblit_net.dart` also exports the replication layer, which reaches the
/// engine's native core, and that core has no web build. A browser build
/// imports this library for [Transport] and `SocketTransport`. `SocketServer`
/// is left out on the web.
library;

export 'src/socket_server.dart'
    if (dart.library.js_interop) 'src/socket_server_web.dart';
export 'src/socket_transport_io.dart'
    if (dart.library.js_interop) 'src/socket_transport_web.dart'
    show SocketTransport;
export 'src/transport.dart' show LoopbackLink, Transport;
