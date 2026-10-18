/// A browser cannot accept connections, so there is no `SocketServer` here.
/// The package exports this file in place of `socket_server.dart` when it is
/// built for the web, and code that names a server fails to compile.
library;
