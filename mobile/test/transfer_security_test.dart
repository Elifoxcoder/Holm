import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:holm_mobile/net/secure_channel.dart';
import 'package:holm_mobile/net/transfer.dart';

Future<({SecureChannel sender, SecureChannel receiver, ServerSocket server})> channels() async {
  final server = await ServerSocket.bind(InternetAddress.loopbackIPv4, 0);
  final accepted = Completer<SecureChannel>();
  server.listen((socket) async {
    accepted.complete(await SecureChannel.open(socket, initiator: false));
  });
  final sender = await SecureChannel.connect(InternetAddress.loopbackIPv4, server.port);
  final receiver = await accepted.future;
  return (sender: sender, receiver: receiver, server: server);
}

List<int> rawOffer({List<Map<String, Object>> files = const [{'Name': 'file.bin', 'Size': 0}]}) =>
    [1, ...utf8.encode(jsonEncode({'Sender': 'Peer', 'Files': files}))];

void main() {
  test('an int64-sized offer is rejected before its size is added', () async {
    final pair = await channels();
    final dir = await Directory.systemTemp.createTemp('holm-overflow');
    try {
      await pair.sender.send(rawOffer(files: [{'Name': 'huge.bin', 'Size': 0x7fffffffffffffff}]));
      await expectLater(
        receiveTransfer(pair.receiver,(_) async => false, dir.path),
        throwsA(isA<FormatException>()),
      );
    } finally {
      await pair.sender.close();
      await pair.receiver.close();
      await pair.server.close();
      await dir.delete(recursive: true);
    }
  });

  test('the initial offer has a deadline', () async {
    final pair = await channels();
    final dir = await Directory.systemTemp.createTemp('holm-offer-timeout');
    try {
      await expectLater(
        receiveTransfer(pair.receiver,(_) async => true, dir.path, offerTimeout: const Duration(milliseconds: 30)),
        throwsA(isA<TimeoutException>()),
      );
    } finally {
      await pair.sender.close();
      await pair.receiver.close();
      await pair.server.close();
      await dir.delete(recursive: true);
    }
  });

  test('a decision that never comes declines and releases the receiver', () async {
    final pair = await channels();
    final dir = await Directory.systemTemp.createTemp('holm-decision-timeout');
    final decision = Completer<bool>();
    try {
      await pair.sender.send(rawOffer());
      final result = receiveTransfer(pair.receiver,(_) => decision.future, dir.path, decisionTimeout: const Duration(milliseconds: 30));
      expect(await result, isNull);
      expect(await pair.sender.receive(), [2, 0]);
    } finally {
      await pair.sender.close();
      await pair.receiver.close();
      await pair.server.close();
      await dir.delete(recursive: true);
    }
  });

  test('an idle data frame times out and removes the partial file', () async {
    final pair = await channels();
    final dir = await Directory.systemTemp.createTemp('holm-idle-timeout');
    try {
      final receiving = receiveTransfer(pair.receiver,(_) async => true, dir.path, idleTimeout: const Duration(milliseconds: 30));
      await pair.sender.send(rawOffer(files: [{'Name': 'partial.bin', 'Size': 4}]));
      expect(await pair.sender.receive(), [2, 1]);
      await pair.sender.send([3, 65]);
      await expectLater(receiving, throwsA(isA<TimeoutException>()));
      expect(dir.listSync(), isEmpty);
    } finally {
      await pair.sender.close();
      await pair.receiver.close();
      await pair.server.close();
      await dir.delete(recursive: true);
    }
  });

  test('a sender does not wait forever for an answer', () async {
    final pair = await channels();
    try {
      await expectLater(
        sendTransfer(pair.sender, 'Phone', [Outgoing.memory('Text.txt', utf8.encode('hi'))],
            answerTimeout: const Duration(milliseconds: 30)),
        throwsA(isA<FormatException>()),
      );
    } finally {
      await pair.sender.close();
      await pair.receiver.close();
      await pair.server.close();
    }
  });

  test('a lost final confirmation has a bounded deadline', () async {
    final pair = await channels();
    try {
      final receiving = pair.receiver.receive();
      final sending = sendTransfer(pair.sender, 'Phone', [Outgoing.memory('empty.bin', Uint8List(0))],
          confirmationTimeout: const Duration(milliseconds: 30));
      expect((await receiving).first, 1); // offer
      await pair.receiver.send([2, 1]);
      expect(await pair.receiver.receive(), [4]); // done; deliberately no received confirmation
      await expectLater(sending, throwsA(isA<FormatException>()));
    } finally {
      await pair.sender.close();
      await pair.receiver.close();
      await pair.server.close();
    }
  });
}
