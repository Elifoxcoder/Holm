// Holm.Core/Sharing/NearbyService.cs in Dart: finds Holm apps (desktop and mobile) on the same network with a small
// UDP broadcast on port 47811, and accepts their encrypted transfers over TCP.

import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'crypto.dart';
import 'secure_channel.dart';

class NearbyPeer {
  final String id;
  final String name;
  final InternetAddress address;
  final int port;
  final DateTime lastSeen;
  const NearbyPeer(this.id, this.name, this.address, this.port, this.lastSeen);
}

class NearbyService {
  static const discoveryPort = 47811;
  static const maxPeers = 256;
  static const _app = 'WinNotch'; // the desktop app's former name, still what every Holm announces
  static const _lifetime = Duration(minutes: 3);
  static final _guid = RegExp(r'^(?:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[0-9a-f]{32})$', caseSensitive: false);

  /// Guid's dashed JSON form and the compact "N" form are accepted, then canonicalized to one map key.
  static bool isPeerId(String? value) => value != null && _guid.hasMatch(value);

  final String id = newGuid();
  String name;
  final Map<String, NearbyPeer> _peers = {};
  final _changes = StreamController<void>.broadcast();
  final Set<String> _manual = {}; // addresses added by hand, for networks that drop broadcasts

  RawDatagramSocket? _udp;
  ServerSocket? _tcp;
  Timer? _announce;
  bool _busy = false;
  final int _discoveryPort;
  final Duration _handshakeTimeout;

  /// Handles an incoming connection. Only one at a time; others are turned away meanwhile.
  Future<void> Function(SecureChannel channel)? incoming;

  NearbyService(this.name, {int? discoveryPort, Duration? handshakeTimeout})
      : _discoveryPort = discoveryPort ?? NearbyService.discoveryPort,
        _handshakeTimeout = handshakeTimeout ?? const Duration(seconds: 10);

  Stream<void> get changes => _changes.stream;
  bool get isRunning => _udp != null;
  bool get isBusy => _busy;
  int get tcpPort => _tcp?.port ?? 0;
  int get boundDiscoveryPort => _udp?.port ?? _discoveryPort;

  List<NearbyPeer> get peers {
    final now = DateTime.now();
    final list = _peers.values.where((p) => now.difference(p.lastSeen) < _lifetime).toList()
      ..sort((a, b) => a.name.toLowerCase().compareTo(b.name.toLowerCase()));
    return list;
  }

  Future<void> start() async {
    if (_udp != null) return;
    _tcp = await ServerSocket.bind(InternetAddress.anyIPv4, 0);
    _tcp!.listen(_accept, onError: (_) {});
    try {
      _udp = await RawDatagramSocket.bind(InternetAddress.anyIPv4, _discoveryPort, reuseAddress: true);
    } catch (_) {
      await _tcp?.close();
      _tcp = null;
      rethrow;
    }
    _udp!.broadcastEnabled = true;
    _udp!.listen(_onUdp, onError: (_) {});
    await _broadcast('query');
    Timer(const Duration(milliseconds: 400), () {
      if (_udp != null) unawaited(_broadcast('query'));
    });
    _announce = Timer.periodic(const Duration(seconds: 30), (_) {
      _broadcast('query');
      _prune();
    });
  }

  Future<void> stop() async {
    if (_udp == null) return;
    try {
      await _broadcast('bye');
    } catch (_) {}
    _announce?.cancel();
    _udp?.close();
    _udp = null;
    await _tcp?.close();
    _tcp = null;
    if (_peers.isNotEmpty) {
      _peers.clear();
      _changes.add(null);
    }
  }

  /// Asks everyone to answer now.
  Future<void> refresh() async {
    await _broadcast('query');
    await Future<void>.delayed(const Duration(milliseconds: 350));
    await _broadcast('query');
    await Future<void>.delayed(const Duration(milliseconds: 350));
    _prune();
  }

  /// Asks one address directly (a laptop on a network that drops broadcasts, or the emulator's host at 10.0.2.2).
  Future<void> addAddress(String host) async {
    final address = InternetAddress(host.trim());
    _manual.add(address.address);
    await _send('query', [address]);
  }

  void _prune() {
    final now = DateTime.now();
    final before = _peers.length;
    _peers.removeWhere((_, p) => now.difference(p.lastSeen) >= _lifetime);
    if (_peers.length != before) _changes.add(null);
  }

  List<int> _packet(String type) =>
      utf8.encode(jsonEncode({'App': _app, 'V': 1, 'T': type, 'Id': id, 'Name': name, 'Port': tcpPort}));

  Future<void> _broadcast(String type) async {
    final targets = <String>{'255.255.255.255'};
    try {
      for (final nic in await NetworkInterface.list(type: InternetAddressType.IPv4)) {
        for (final a in nic.addresses) {
          if (a.isLoopback || a.isLinkLocal) continue;
          final parts = a.address.split('.');
          // Dart doesn't give the netmask; home and office Wi-Fi is nearly always a /24.
          targets.add('${parts[0]}.${parts[1]}.${parts[2]}.255');
        }
      }
    } catch (_) {}
    targets.addAll(_manual);
    await _send(type, targets.map(InternetAddress.new));
  }

  Future<void> _send(String type, Iterable<InternetAddress> targets) async {
    final udp = _udp;
    if (udp == null) return;
    final bytes = _packet(type);
    for (final t in targets) {
      try {
        udp.send(bytes, t, _discoveryPort);
      } catch (_) {} // an interface that went away
    }
  }

  void _onUdp(RawSocketEvent event) {
    if (event != RawSocketEvent.read) return;
    final udp = _udp;
    final datagram = udp?.receive();
    if (udp == null || datagram == null || datagram.data.length > 2048) return;
    Map<String, dynamic> packet;
    try {
      packet = jsonDecode(utf8.decode(datagram.data)) as Map<String, dynamic>;
    } catch (_) {
      return;
    }
    if (packet['App'] != _app || packet['V'] != 1 || packet['Id'] is! String || !isPeerId(packet['Id'] as String)) return;
    final peerId = (packet['Id'] as String).replaceAll('-', '').toLowerCase();
    if (peerId == id.replaceAll('-', '')) return;
    final now = DateTime.now();
    _prune();
    final existing = _peers[peerId];
    // Discovery is unauthenticated. A live device id belongs to the address that announced it; a stranger on another
    // IP may neither take over that id nor remove it with a forged "bye".
    if (existing != null && now.difference(existing.lastSeen) < _lifetime && existing.address.address != datagram.address.address) {
      return;
    }
    if (packet['T'] == 'bye') {
      if (existing != null && _peers.remove(peerId) != null) _changes.add(null);
      return;
    }
    final port = packet['Port'];
    if (port is! int || port <= 0 || port > 65535) return;
    var peerName = ((packet['Name'] as String?) ?? '')
        .runes
        .where((r) => r >= 0x20)
        .take(40)
        .map(String.fromCharCode)
        .join()
        .trim();
    if (peerName.isEmpty) peerName = 'HolyNotch';
    final isNew = existing == null;
    // The same device started again (an update, a restart): same name and address, new id. Its old entry goes now,
    // not after three minutes, or it shows twice.
    if (isNew) {
      _peers.removeWhere((id, p) => id != peerId && p.name == peerName && p.address == datagram.address);
      if (_peers.length >= maxPeers) {
        var oldest = _peers.keys.first;
        for (final key in _peers.keys) {
          if (_peers[key]!.lastSeen.isBefore(_peers[oldest]!.lastSeen)) oldest = key;
        }
        _peers.remove(oldest);
      }
    }
    _peers[peerId] = NearbyPeer(peerId, peerName, datagram.address, port, now);
    if (isNew || existing.name != peerName || existing.address != datagram.address || existing.port != port) {
      _changes.add(null);
    }
    if (packet['T'] == 'query') {
      try {
        udp.send(_packet('hello'), datagram.address, datagram.port);
      } catch (_) {}
    }
  }

  Future<void> _accept(Socket socket) async {
    if (_busy) {
      socket.destroy(); // already busy with someone
      return;
    }
    _busy = true;
    SecureChannel? channel;
    try {
      socket.setOption(SocketOption.tcpNoDelay, true);
      channel = await SecureChannel.open(socket, initiator: false).timeout(_handshakeTimeout);
      final handler = incoming;
      if (handler != null) await handler(channel);
    } catch (_) {
      // a failed handshake, offer timeout or dropped connection
    } finally {
      // Release first: even a broken close must not leave every later peer turned away.
      _busy = false;
      try {
        await channel?.close();
      } catch (_) {}
      socket.destroy();
    }
  }

  static Future<SecureChannel> connect(NearbyPeer peer) => SecureChannel.connect(peer.address, peer.port);
}
