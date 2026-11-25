import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:holm_mobile/app_state.dart';
import 'package:holm_mobile/net/bounded_io.dart';
import 'package:holm_mobile/net/crypto.dart';
import 'package:holm_mobile/net/friends.dart';
import 'package:holm_mobile/net/nearby.dart';
import 'package:holm_mobile/net/net_security.dart';

String cardWithServer(Identity me, Object? server) {
  final card = {'n': me.name, 't': me.topic, 'k': me.publicKey, 's': server};
  return 'WNF1.${base64Url(utf8.encode(jsonEncode(card)))}';
}

void main() {
  test('seen ids persist timestamps, tolerate legacy entries and prune only after the full window', () {
    const now = 2000000000;
    final window = BoundedIds.defaultRetention.inSeconds;
    final legacy = BoundedIds.restore(limit: 10, maxLength: 256, values: ['old'], now: now);
    expect(legacy.toJson(now: now), ['old']);
    expect(legacy.timesToJson(now: now), [now]);

    final edge = BoundedIds(limit: 10, now: now)..remember('edge', now: now - window + 1);
    expect(edge.toJson(now: now), contains('edge'));
    expect(edge.toJson(now: now + 1), isEmpty);

    final restarted = BoundedIds.restore(
      limit: 10,
      maxLength: 256,
      values: edge.toJson(now: now),
      times: edge.timesToJson(now: now),
      now: now + 2,
    );
    expect(restarted.toJson(now: now + 2), isEmpty);
    expect(BoundedIds().limit, 20000);
  });

  test('seen-id retention is bounded and duplicate restores keep the newest entry', () {
    const now = 2000000000;
    final store = BoundedIds(limit: 3, now: now)
      ..remember('a', now: now - 3)
      ..remember('b', now: now - 2)
      ..remember('c', now: now - 1)
      ..remember('d', now: now);
    expect(store.toJson(now: now), ['b', 'c', 'd']);
    final restored = BoundedIds.restore(limit: 3, maxLength: 256, values: ['a', 'b', 'a'], times: [now - 3, now - 2, now - 1], now: now);
    expect(restored.toJson(now: now), ['b', 'a']);
    expect(restored.timesToJson(now: now), [now - 2, now - 1]);
  });

  test('new notes replay by authenticated id; old notes canonicalize padded outer fields', () {
    final a = Identity.create('Alex'), b = Identity.create('Lena');
    final toB = parseFriendCode(b.friendCode)!;
    final id = base64Url(randomBytes(16));
    final sealed = Notes.seal(a, toB, 'chat', {'M': Notes.chatText('hello'), 'I': newGuidN(), 'J': id});
    final opened = Notes.open(b, sealed)!;
    expect(opened.$1.noteId, id);
    expect(Notes.replayId(sealed, opened.$1), 'note:$id');

    final legacy = Notes.seal(a, toB, 'chat', {'M': Notes.chatText('old'), 'I': newGuidN()}, withNoteId: false);
    final old = Notes.open(b, legacy)!;
    expect(old.$1.noteId, isNull);
    final parts = legacy.substring(Notes.prefix.length).split(' ');
    String padded(String value) => value + '=' * ((4 - value.length % 4) % 4);
    final withPadding = '${Notes.prefix}${padded(parts[0])} ${padded(parts[1])}';
    expect(Notes.open(b, withPadding), isNotNull);
    expect(Notes.replayId(withPadding, old.$1), Notes.replayId(legacy, old.$1));
  });

  test('freshness uses authenticated Sent, not a fresh server event time', () {
    final now = unixNow();
    final note = Note('phone', 'PC', now - 120);
    final freshServerTime = now;
    expect(freshServerTime, greaterThanOrEqualTo(now - 1));
    expect(Notes.isSentTimePlausible(note.sent, now), isTrue); // old enough to reject real-time actions, new enough to keep
    expect(authenticatedNoteAge(note, now), greaterThan(60));
    expect(isFreshOwnDeviceNote(note, now), isFalse);
    final oldPing = Note('ping', 'Lena', now - 200);
    expect(authenticatedNoteAge(oldPing, now) < 180, isFalse);
  });

  test('friend codes require public HTTPS inboxes without credentials, query or fragment', () {
    final me = Identity.create('Alex');
    for (final server in [
      'http://example.com',
      'https://user@example.com',
      'https://example.com?x=1',
      'https://example.com#fragment',
      'https://127.0.0.1',
      'https://10.0.0.1',
      'https://192.168.1.1',
      'https://[::1]',
      'https://localhost',
      'https://relay.example.test',
    ]) {
      expect(parseFriendCode(cardWithServer(me, server)), isNull, reason: server);
    }
    expect(parseFriendCode(cardWithServer(me, 'https://relay.holmnotch.ch'))?.server, 'https://relay.holmnotch.ch');
    expect(parseFriendCode(cardWithServer(me, 'https://example.com:8443/base/'))?.server, 'https://example.com:8443/base');
    expect(parseFriendCode(cardWithServer(me, null))?.server, isNull); // old card: compatible ntfy reader
  });

  test('public-address policy rejects private, translated and special-purpose IPv6 forms', () {
    for (final host in [
      '0.0.0.0', '10.0.0.1', '127.0.0.1', '169.254.169.254', '172.16.0.1', '192.168.1.1',
      '100.64.0.1', '198.18.0.1', '224.0.0.1', '255.255.255.255',
      '::', '::1', '::ffff:127.0.0.1', '::ffff:10.0.0.1', '::10.0.0.1', 'fc00::1', 'fe80::1',
      'fec0::1', '64:ff9b::7f00:1', '2002:7f00:1::', '2001:0000:4136:e378:8000:63bf:3fff:fdd2',
      '2001:db8::1', 'ff02::1',
    ]) {
      expect(isPublicAddress(InternetAddress(host)), isFalse, reason: host);
    }
    for (final host in ['8.8.8.8', '1.1.1.1', '2606:4700:4700::1111']) {
      expect(isPublicAddress(InternetAddress(host)), isTrue, reason: host);
    }
  });

  test('resolved private addresses are rejected before connect', () async {
    await expectLater(
      connectPublic(Uri.parse('https://relay.example.com'), resolve: (_) async => [InternetAddress.loopbackIPv4]),
      throwsA(isA<HttpException>()),
    );
  });

  test('inbox bodies have a fixed byte cap and a deadline', () async {
    await expectLater(
      readBoundedUtf8(Stream.value(utf8.encode('12345')), maxBytes: 4),
      throwsA(isA<HttpException>()),
    );
    final slow = StreamController<List<int>>();
    slow.add(utf8.encode('x'));
    await expectLater(readBoundedUtf8(slow.stream, maxBytes: 4, timeout: const Duration(milliseconds: 20)), throwsA(isA<TimeoutException>()));
    await slow.close();
  });

  test('nearby ids are Guids and a nearby name cannot shadow an account device', () {
    expect(NearbyService.isPeerId(newGuid()), isTrue);
    expect(NearbyService.isPeerId(newGuid().replaceAll('-', '')), isTrue);
    expect(NearbyService.isPeerId('not-a-guid'), isFalse);
    expect(NearbyService.isPeerId('${newGuid()}0'), isFalse);

    final account = OwnDevice('a' * 32, 'Laptop', DateTime.now());
    final peer = NearbyPeer('b' * 32, 'laptop', InternetAddress.loopbackIPv4, 1234, DateTime.now());
    final state = _TargetState([account], [peer]);
    expect(state.targets.single.device, same(account));
    expect(state.targets.single.authenticated, isTrue);
    state.setClipboardSync(true, SendTarget.nearby(peer));
    expect(state.clipboardSync, isFalse);
  });

  test('file sends refuse to start without the explicit name-and-size confirmation', () async {
    final state = AppState();
    final target = SendTarget.nearby(NearbyPeer('b' * 32, 'PC', InternetAddress.loopbackIPv4, 1, DateTime.now()));
    expect(await state.sendFiles(target, ['file.bin']), isFalse);
    state.confirmFiles = (_, _) async => false;
    expect(await state.sendFiles(target, ['file.bin']), isFalse);
  });
}

class _FakeNearby extends NearbyService {
  final List<NearbyPeer> values;
  _FakeNearby(this.values) : super('phone');
  @override
  List<NearbyPeer> get peers => values;
}

class _TargetState extends AppState {
  final List<OwnDevice> devices;
  _TargetState(this.devices, List<NearbyPeer> peers) {
    nearby = _FakeNearby(peers);
    me = Identity.create('phone');
  }
  @override
  List<OwnDevice> get ownDevices => devices;
}
