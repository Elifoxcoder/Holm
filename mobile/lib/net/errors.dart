// Short codes for what went wrong with a transfer or a connection, as on the desktop (Holm.Core/ErrorCodes.cs), so a
// report says exactly which step failed: "Couldn't send · Error HN-R02 · the other side didn't come to the relay".

import 'dart:async';
import 'dart:io';

const relayUnreachable = 'HN-R01';
const partnerNoShow = 'HN-R02';
const dropped = 'HN-R03';
const inboxFailed = 'HN-I01';
const inboxRetired = 'HN-I02';
const cryptoFailed = 'HN-C01';
const protocolError = 'HN-P01';
const localFile = 'HN-F01';
const unknownError = 'HN-X00';

/// The code and a few words for [e].
(String, String) errorOf(Object e) {
  final text = e.toString();
  if (e is TimeoutException) return (partnerNoShow, "the other side didn't come to the relay in time");
  if (e is SocketException) {
    return text.contains('reset') || text.contains('aborted')
        ? (dropped, 'the connection was reset')
        : (relayUnreachable, "couldn't reach the relay");
  }
  if (e is HttpException) {
    if (text.contains("Holm's server")) return (inboxRetired, "their inbox is still on Holm's server");
    final status = RegExp(r'\((\d{3})\)').firstMatch(text)?.group(1);
    return (inboxFailed, status != null ? 'the inbox answered $status' : "couldn't reach their inbox");
  }
  if (e is FileSystemException) return (localFile, "couldn't write the file");
  if (text.contains('MAC') || text.contains('tag') || text.contains('decrypt')) return (cryptoFailed, "the encrypted data didn't check out");
  if (e is FormatException || e is StateError) return (protocolError, 'unexpected data from the other side');
  if (e is IOException) return (dropped, 'the connection dropped');
  return (unknownError, e.runtimeType.toString());
}

/// "Error HN-R02 · the other side didn't come to the relay in time".
String errorCode(Object e) {
  final (code, reason) = errorOf(e);
  return 'Error $code · $reason';
}
