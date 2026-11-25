// Transfers over the internet, as Holm sends to friends (Holm.Core/Sharing/RelayLink.cs and ShareCode.cs): both sides
// meet on a transit relay under a channel name made from a shared secret, and talk through a SecureChannel keyed with
// another value made from it. The relay only ever sees encrypted bytes.
import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'crypto.dart';
import 'secure_channel.dart';
import 'server.dart';

class ShareSecret {
  static const length = 16;

  static Uint8List create() => randomBytes(length);

  /// The AES-256 key both sides encrypt with (the label keeps Holm's former name, as on the desktop).
  static Uint8List key(List<int> secret) => hkdf(secret, 32, const [], ascii.encode('WinNotch transfer key v1'));

  /// The channel's name on the relay; different from the key, so the relay learns nothing about it.
  static Uint8List relayToken(List<int> secret) => hkdf(secret, 32, const [], ascii.encode('WinNotch relay token v1'));
}

class RelayLink {
  /// HolyNotch's own transit relay (server/, on the VPS).
  static const host = holyNotchHost;
  static const port = holyNotchTransitPort;

  /// The public relay of the magic-wormhole project, which Holm uses: tried at the same time as ours, so a friend on
  /// Holm still gets our files. As Holm.Core/Sharing/RelayLink.cs.
  static const holmHost = 'transit.magic-wormhole.io';

  /// How long a pair on Holm's relay waits for one on ours (a HolyNotch partner pairs on both at once).
  static const oursGrace = Duration(seconds: 2);

  /// Connects and waits until the other side is on the same channel too; the channel then carries the transfer.
  /// On the default relay: ours and Holm's at once. Ours wins when the partner is there (a HolyNotch partner pairs on
  /// both, and both sides then take ours), Holm's when only a Holm partner came.
  static Future<SecureChannel> connect(List<int> secret, {required bool initiator, String relayHost = host, int relayPort = port}) {
    if (relayHost != host || relayPort != port) return _connectOne(secret, initiator, relayHost, relayPort);
    return _connectEither(secret, initiator);
  }

  static Future<SecureChannel> _connectEither(List<int> secret, bool initiator) async {
    final ours = _Attempt(_connectOne(secret, initiator, host, port));
    final theirs = _Attempt(_connectOne(secret, initiator, holmHost, port));
    SecureChannel? chosen;
    try {
      final first = await Future.any([ours.done, theirs.done]);
      if (first == ours && ours.channel != null) return chosen = ours.channel!;
      if (first == theirs && theirs.channel != null) {
        await Future.any([ours.done, Future<void>.delayed(oursGrace)]);
        if (ours.channel != null) return chosen = ours.channel!;
        return chosen = theirs.channel!;
      }
      // One relay failed (down, unreachable): the other one alone; when both fail, ours says why.
      final other = first == ours ? theirs : ours;
      await other.done;
      if (other.channel != null) return chosen = other.channel!;
      throw ours.error ?? theirs.error ?? const SocketException("The relay couldn't be reached.");
    } finally {
      // The other one closes, now or whenever it pairs after all (it waits at most as long as the relay keeps it).
      for (final a in [ours, theirs]) {
        a.done.then((_) {
          if (a.channel != null && !identical(a.channel, chosen)) a.channel!.close();
        });
      }
    }
  }

  static Future<SecureChannel> _connectOne(List<int> secret, bool initiator, String relayHost, int relayPort) async {
    final token = ShareSecret.relayToken(secret);
    final socket = await _reach(relayHost, relayPort);
    try {
      socket.setOption(SocketOption.tcpNoDelay, true);
      final side = toHex(randomBytes(8));
      socket.add(ascii.encode('please relay ${toHex(token)} for side $side\n'));
      await socket.flush();
      final reader = ByteReader(socket);
      // The relay answers only once both sides are there.
      final reply = await _readLine(reader);
      if (reply != 'ok') throw SocketException('The relay said: $reply');
      return SecureChannel.withKey(socket, reader, ShareSecret.key(secret), initiator: initiator);
    } catch (_) {
      socket.destroy();
      rethrow;
    }
  }

  /// Keeps trying until the other side shows up or [patience] runs out (the relay may drop a long wait).
  static Future<SecureChannel> connectWithRetry(List<int> secret, {required bool initiator, required Duration patience,
      String relayHost = host, int relayPort = port}) async {
    final deadline = DateTime.now().add(patience);
    while (true) {
      final left = deadline.difference(DateTime.now());
      if (left <= Duration.zero) throw TimeoutException('The other device did not come to the relay.');
      try {
        return await connect(secret, initiator: initiator, relayHost: relayHost, relayPort: relayPort).timeout(left);
      } on TimeoutException {
        rethrow;
      } on IOException {
        if (deadline.difference(DateTime.now()) < const Duration(seconds: 3)) rethrow;
        await Future<void>.delayed(const Duration(seconds: 3));
      }
    }
  }

  /// IPv4 addresses first, five seconds each: a network that drops IPv6 must not spend the whole wait on one address.
  static Future<Socket> _reach(String host, int port) async {
    final addresses = InternetAddress.tryParse(host) != null ? [InternetAddress(host)] : await InternetAddress.lookup(host);
    addresses.sort((a, b) => (a.type == InternetAddressType.IPv4 ? 0 : 1) - (b.type == InternetAddressType.IPv4 ? 0 : 1));
    Object? last;
    for (final address in addresses) {
      try {
        return await Socket.connect(address, port, timeout: const Duration(seconds: 5));
      } catch (e) {
        last = e;
      }
    }
    throw SocketException("The relay couldn't be reached. ${last ?? ''}".trim());
  }

  static Future<String> _readLine(ByteReader reader) async {
    final line = StringBuffer();
    while (line.length < 200) {
      final b = (await reader.read(1))[0];
      if (b == 0x0a) return line.toString();
      line.writeCharCode(b);
    }
    throw const SocketException('The relay sent something unexpected.');
  }
}

/// One relay attempt of [RelayLink._connectEither]: settles with itself, holding the channel or the error.
class _Attempt {
  SecureChannel? channel;
  Object? error;
  late final Future<_Attempt> done;

  _Attempt(Future<SecureChannel> connecting) {
    done = connecting.then((c) {
      channel = c;
      return this;
    }, onError: (Object e) {
      error = e;
      return this;
    });
  }
}
