/// Byte that tells the echo server to close the connection it came on.
const int hangUp = 0xff;

/// Byte that tells the echo server to stop reading from its connection, as a
/// peer on a dead network does.
const int stall = 0xfe;
