import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

/// Reads an inbox response without allowing either bytes or a quiet peer to grow without bound. Cancelling the stream
/// on failure also stops the socket work promptly instead of leaving a timed-out join running in the background.
Future<String> readBoundedUtf8(Stream<List<int>> source,
    {required int maxBytes, Duration timeout = const Duration(seconds: 20)}) async {
  if (maxBytes <= 0) throw ArgumentError.value(maxBytes, 'maxBytes');
  final reader = StreamIterator<List<int>>(source);
  final bytes = BytesBuilder(copy: false);
  final watch = Stopwatch()..start();
  try {
    while (true) {
      final left = timeout - watch.elapsed;
      if (left <= Duration.zero) throw TimeoutException('The inbox response timed out.');
      if (!await reader.moveNext().timeout(left)) break;
      final chunk = reader.current;
      if (chunk.length > maxBytes - bytes.length) throw const HttpException('The inbox response is too large.');
      bytes.add(chunk);
    }
    return utf8.decode(bytes.takeBytes(), allowMalformed: true);
  } finally {
    await reader.cancel();
  }
}
