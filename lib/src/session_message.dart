import 'dart:convert';
import 'dart:typed_data';

/// 'ORBJ'.
const int _joinMagic = 0x4F52424A;

/// 'ORBG'.
const int _goodbyeMagic = 0x4F524247;

/// 'ORBM'.
const int _messageMagic = 0x4F52424D;

const int _headerSize = 4;

/// A client telling the authority who it is.
///
/// The credentials are the game's: a token, a ticket, a name. The engine
/// carries them to the admission function and reads nothing from them.
Uint8List encodeJoin(Uint8List credentials) => _tagged(_joinMagic, credentials);

/// The credentials in a join, or null if this is not one.
Uint8List? decodeJoin(Uint8List message) => _untagged(_joinMagic, message);

/// A message of the game's own, in either direction. The engine carries the
/// bytes and reads nothing from them.
Uint8List encodeMessage(Uint8List body) => _tagged(_messageMagic, body);

/// The body of a game message, or null if this is not one.
Uint8List? decodeMessage(Uint8List message) =>
    _untagged(_messageMagic, message);

/// The authority telling a client why it is being sent away.
Uint8List encodeGoodbye(String reason) =>
    _tagged(_goodbyeMagic, Uint8List.fromList(utf8.encode(reason)));

/// The reason in a goodbye, or null if this is not one.
String? decodeGoodbye(Uint8List message) {
  final body = _untagged(_goodbyeMagic, message);
  return body == null ? null : utf8.decode(body, allowMalformed: true);
}

Uint8List _tagged(int magic, Uint8List body) {
  final message = Uint8List(_headerSize + body.length);
  ByteData.sublistView(message).setUint32(0, magic, Endian.little);
  message.setRange(_headerSize, message.length, body);
  return message;
}

Uint8List? _untagged(int magic, Uint8List message) {
  if (message.length < _headerSize) return null;
  final data = ByteData.sublistView(message);
  if (data.getUint32(0, Endian.little) != magic) return null;
  return Uint8List.sublistView(message, _headerSize);
}
