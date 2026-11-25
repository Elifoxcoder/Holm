import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:holm_mobile/net/crypto.dart';
import 'package:holm_mobile/net/nearby.dart';

Future<void> sendPacket(NearbyService service, String type, String id, String name,
    {InternetAddress? source}) async {
  final socket = await RawDatagramSocket.bind(source ?? InternetAddress.loopbackIPv4, 0);
  try {
    socket.send(utf8.encode(jsonEncode({
      'App': 'WinNotch',
      'V': 1,
      'T': type,
      'Id': id,
      'Name': name,
      'Port': service.tcpPort,
    })), InternetAddress.loopbackIPv4, service.boundDiscoveryPort);
  } finally {
    socket.close();
  }
}

Future<void> waitFor(bool Function() ready) async {
  for (var i = 0; i < 100 && !ready(); i++) {
    await Future<void>.delayed(const Duration(milliseconds: 10));
  }
}

void main() {
  test('a live peer id stays bound to its IP and a forged bye cannot remove it', () async {
    final service = NearbyService('phone', discoveryPort: 0);
    await service.start();
    try {
      final id = newGuid();
      final key = id.replaceAll('-', '');
      NearbyPeer? peer() => service.peers.where((p) => p.id == key).firstOrNull;
      await sendPacket(service, 'hello', id, 'Real PC');
      await waitFor(() => peer() != null);
      expect(peer()!.name, 'Real PC');

      await sendPacket(service, 'hello', id, 'Impostor', source: InternetAddress('127.0.0.2'));
      await Future<void>.delayed(const Duration(milliseconds: 50));
      expect(peer()!.name, 'Real PC');

      await sendPacket(service, 'bye', id, 'Impostor', source: InternetAddress('127.0.0.2'));
      await Future<void>.delayed(const Duration(milliseconds: 50));
      expect(peer(), isNotNull);

      await sendPacket(service, 'bye', id, 'Real PC');
      await waitFor(() => peer() == null);
      expect(peer(), isNull);
    } finally {
      await service.stop();
    }
  });

  test('the nearby peer map is capped even under a packet flood', () async {
    final service = NearbyService('phone', discoveryPort: 0);
    await service.start();
    try {
      final socket = await RawDatagramSocket.bind(InternetAddress.loopbackIPv4, 0);
      try {
        for (var i = 0; i < NearbyService.maxPeers + 40; i++) {
          socket.send(utf8.encode(jsonEncode({
            'App': 'WinNotch', 'V': 1, 'T': 'hello', 'Id': newGuid(), 'Name': 'PC $i', 'Port': service.tcpPort,
          })), InternetAddress.loopbackIPv4, service.boundDiscoveryPort);
        }
      } finally {
        socket.close();
      }
      await waitFor(() => service.peers.length >= NearbyService.maxPeers);
      expect(service.peers.length, lessThanOrEqualTo(NearbyService.maxPeers));
      expect(service.peers.length, greaterThan(0));
    } finally {
      await service.stop();
    }
  });

  test('a handshake timeout releases the single incoming slot', () async {
    final service = NearbyService('phone', discoveryPort: 0, handshakeTimeout: const Duration(milliseconds: 40))
      ..incoming = (_) async => throw StateError('not reached');
    await service.start();
    Socket? socket;
    try {
      socket = await Socket.connect(InternetAddress.loopbackIPv4, service.tcpPort);
      await waitFor(() => service.isBusy);
      expect(service.isBusy, isTrue);
      await waitFor(() => !service.isBusy);
      expect(service.isBusy, isFalse);
    } finally {
      await socket?.close();
      await service.stop();
    }
  });
}
