// Holm.Core/Sharing/Friends.cs and FriendInvite.cs in Dart: friend codes, notes sealed end to end, the inbox on
// HolyNotch's server, and short invite codes. Holm's servers (its relay, ntfy.sh) are never contacted: a friend whose
// inbox is there is unreachable until added again with a new code.

import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'bounded_io.dart';
import 'crypto.dart';
import 'net_security.dart';
import 'server.dart';

/// HolyNotch's own server, where our inbox is (server.dart).
const relayServer = holyNotchUrl;

/// What an old card without a server means (ntfy.sh). Like Holm's relay, never contacted.
const legacyServer = 'https://ntfy.sh';

/// Holm's servers, which HolyNotch no longer uses (as FriendIdentity.RetiredServers on the desktop).
const retiredServers = {'https://relay.holmnotch.ch', legacyServer};

/// Whether an inbox on [server] can't be reached any more (null: an old card, ntfy.sh).
bool isRetiredServer(String? server) {
  final n = normalizeHttpsServer(server ?? legacyServer);
  return n != null && retiredServers.contains(n.toLowerCase());
}
const _userAgent = 'Holm-Friends/1';

class Friend {
  String name;
  final String topic;
  final String key; // base64url SubjectPublicKeyInfo
  final String? server; // null: an old card that meant ntfy.sh

  Friend({required this.name, required this.topic, required this.key, this.server});

  String get serverUrl => normalizeHttpsServer(server ?? legacyServer)!;

  /// Their inbox is on Holm's server: they have to send a new code.
  bool get retired => isRetiredServer(server);

  Map<String, dynamic> toJson() => {'name': name, 'topic': topic, 'key': key, 'server': server};
  factory Friend.fromJson(Map<String, dynamic> j) =>
      Friend(name: j['name'] as String, topic: j['topic'] as String, key: j['key'] as String, server: j['server'] as String?);
}

class Identity {
  final String name;
  final String topic;
  final EcKey key;

  Identity(this.name, this.topic, this.key);

  factory Identity.create(String name) {
    const alphabet = 'abcdefghijklmnopqrstuvwxyz234567';
    final topic = 'wnf-${randomBytes(26).map((b) => alphabet[b & 31]).join()}';
    return Identity(name, topic, EcKey.generate());
  }

  Identity withName(String name) => Identity(name, topic, key);

  String get publicKey => base64Url(key.spki());

  /// What you give a friend so they can add you: "WNF1." and base64url(JSON), as Holm's desktop app writes it.
  String get friendCode {
    final card = {'n': name, 't': topic, 'k': publicKey, 's': relayServer};
    return 'WNF1.${base64Url(utf8.encode(jsonEncode(card)))}';
  }

  Map<String, dynamic> toJson() => {'name': name, 'topic': topic, 'd': key.d.toRadixString(16)};
  factory Identity.fromJson(Map<String, dynamic> j) =>
      Identity(j['name'] as String, j['topic'] as String, EcKey.fromPrivate(BigInt.parse(j['d'] as String, radix: 16)));
}

int _unixNow() => DateTime.now().millisecondsSinceEpoch ~/ 1000;

/// A persisted, time-bounded set. Transport ids and authenticated note replay ids share one hard cap. Timestamps are
/// stored in parallel (legacy settings without them are migrated conservatively for one full retention window).
class BoundedIds {
  static const defaultLimit = 20000;
  static const defaultRetention = Duration(days: 7, minutes: 10); // Holm's past window plus two clock-skew allowances
  static const _clockSkew = 300;
  static const _fullPruneInterval = 60;

  final int limit, maxLength;
  final Duration retention;
  int Function() _now;
  final List<String> _values = [];
  final List<int> _times = [];
  int _lastPrune = 0;

  BoundedIds({this.limit = defaultLimit, this.maxLength = 256, this.retention = defaultRetention, int? now})
      : assert(limit > 0),
        _now = _unixNow {
    final fixed = now;
    if (fixed != null) _now = () => fixed;
  }

  factory BoundedIds.restore(
      {required int limit, required int maxLength, Iterable<String> values = const [], Iterable<int> times = const [], int? now}) {
    final store = BoundedIds(limit: limit, maxLength: maxLength, now: now);
    store.restore(values, times: times, at: now);
    return store;
  }

  bool _valid(String value) => value.trim().isNotEmpty && value.length <= maxLength;

  void restore(Iterable<String> values, {Iterable<int> times = const [], int? at}) {
    final source = values.toList(), storedTimes = times.toList(), now = at ?? _now();
    final last = <String, int>{};
    for (var i = 0; i < source.length; i++) {
      if (_valid(source[i])) last[source[i]] = i;
    }
    final entries = <({String value, int time, int order})>[];
    final cutoff = now - retention.inSeconds;
    for (var i = 0; i < source.length; i++) {
      final value = source[i];
      if (!_valid(value) || last[value] != i) continue;
      var time = i < storedTimes.length ? storedTimes[i] : 0;
      if (time <= 0 || time > now + _clockSkew) time = now; // legacy or corrupt: retain conservatively
      if (time <= cutoff) continue;
      entries.add((value: value, time: time, order: i));
    }
    entries.sort((a, b) => a.time != b.time ? a.time.compareTo(b.time) : a.order.compareTo(b.order));
    _values
      ..clear()
      ..addAll([for (final e in entries) e.value]);
    _times
      ..clear()
      ..addAll([for (final e in entries) e.time]);
    _trimCount();
    _lastPrune = now;
  }

  void _trimCount() {
    if (_values.length > limit) _values.removeRange(0, _values.length - limit);
    if (_times.length > limit) _times.removeRange(0, _times.length - limit);
  }

  void _removeAt(int index) {
    _values.removeAt(index);
    _times.removeAt(index);
  }

  void _pruneAll(int now) {
    restore(_values, times: _times, at: now);
  }

  void _maintain(int now) {
    if (_lastPrune == 0 || now < _lastPrune || now - _lastPrune >= _fullPruneInterval) {
      _pruneAll(now);
      return;
    }
    final cutoff = now - retention.inSeconds;
    var removed = 0;
    while (removed < _times.length && _times[removed] <= cutoff) {
      removed++;
    }
    if (removed > 0) {
      _values.removeRange(0, removed);
      _times.removeRange(0, removed);
    }
  }

  bool contains(String value) {
    _maintain(_now());
    return _values.contains(value);
  }

  bool remember(String? value, {int? now}) {
    final at = now ?? _now();
    _maintain(at);
    if (value == null || !_valid(value) || _values.contains(value)) return false;
    while (_values.length >= limit) {
      _removeAt(0);
    }
    _values.add(value);
    _times.add(at);
    return true;
  }

  void clear() {
    _values.clear();
    _times.clear();
    _lastPrune = 0;
  }

  List<String> toJson({int? now}) {
    _maintain(now ?? _now());
    return List.unmodifiable(_values);
  }

  List<int> timesToJson({int? now}) {
    _maintain(now ?? _now());
    return List.unmodifiable(_times);
  }
}

final _codeInText = RegExp(r'WNF1\.[A-Za-z0-9_-]{40,2000}');

/// Reads a friend code anywhere in the text; null if there is none or it is damaged.
Friend? parseFriendCode(String? text) {
  if (text == null || text.length > 10000) return null;
  final match = _codeInText.firstMatch(text);
  if (match == null) return null;
  try {
    final card = jsonDecode(utf8.decode(fromBase64Url(match.group(0)!.substring(5)))) as Map<String, dynamic>;
    final t = card['t'] as String?, k = card['k'] as String?;
    if (t == null || k == null || !RegExp(r'^[A-Za-z0-9_-]{1,64}$').hasMatch(t)) return null;
    parseSpki(fromBase64Url(k));
    final suppliedServer = card['s'];
    if (suppliedServer != null && suppliedServer is! String) return null;
    final server = normalizeHttpsServer(suppliedServer as String?);
    if (suppliedServer != null && server == null) return null;
    var name = ((card['n'] as String?) ?? '').runes.where((r) => r >= 0x20).map(String.fromCharCode).join().trim();
    if (name.isEmpty) name = 'Friend';
    if (name.length > 40) name = name.substring(0, 40);
    return Friend(name: name, topic: t, key: k, server: server);
  } catch (_) {
    return null;
  }
}

/// A note as it travels, keyed by the one-letter names of Friends.cs's NoteDto.
class Note {
  final String kind; // chat, react, request, response, cancel-request, removed, ping, here, wave, ...
  final String from;
  final int sent;
  final Map<String, dynamic> fields;
  Note(this.kind, this.from, this.sent, [this.fields = const {}]);

  String? get text {
    final m = fields['M'];
    if (m is! String) return null;
    try {
      return utf8.decode(base64Decode(m));
    } catch (_) {
      return null;
    }
  }

  String? get messageId => fields['I'] as String?;
  String? get requestId => fields['R'] as String?;
  String? get code => fields['C'] as String?;
  String? get noteId => fields['J'] as String?;
  bool? get accepted => fields['A'] as bool?;
  bool get isGif => fields['G'] == true;
}

int unixNow() => DateTime.now().millisecondsSinceEpoch ~/ 1000;

/// Freshness comes only from the sender's authenticated Sent time and our local UTC clock, never the inbox server's.
int authenticatedNoteAge(Note note, int now) => (now - note.sent).clamp(0, 1 << 31);

/// How old a live note (an offer, presence, an own device) is: by the sender's clock, or by when it reached the server
/// ([arrived], 0 if unknown), whichever is younger. A device whose clock is off still gets through; replays stay caught
/// by the seen ids, and an old offer only leads to a relay nobody waits on. As FriendsService.cs on the desktop.
int liveNoteAge(Note note, int now, int arrived) {
  final age = authenticatedNoteAge(note, now);
  if (arrived <= 0) return age;
  final since = (now - arrived).clamp(0, 1 << 31);
  return since < age ? since : age;
}

/// Own-device notes can cause real-time actions: only live ones (by either clock, see [liveNoteAge]).
bool isFreshOwnDeviceNote(Note note, int now, {int arrived = 0}) {
  final age = liveNoteAge(note, now, arrived);
  return switch (note.kind) {
    'device' => age <= 30,
    'phone' || 'files' => age <= 60,
    _ => true,
  };
}

class Notes {
  static const prefix = 'wnf1 ';
  static final _label = utf8.encode('WinNotch friends v1');

  static List<int> _sharedKey(EcKey mine, String theirKey) =>
      mine.deriveKeyFromHash(parseSpki(fromBase64Url(theirKey)), _label);

  /// Seals a note for [to]. [from] is the name in it: our name for friends, the device's name between our devices.
  /// The optional authenticated id is inside the sealed JSON; the wnf1 outer format and its nonce do not change.
  static String seal(Identity me, Friend to, String kind, Map<String, dynamic> fields,
      {String? from, bool withNoteId = true}) {
    final dto = <String, dynamic>{
      'K': kind,
      'F': from ?? me.name,
      'T': unixNow(),
      ...fields,
      if (withNoteId) 'J': isNoteId(fields['J'] as String?) ? fields['J'] : base64Url(randomBytes(16)),
    }..removeWhere((_, v) => v == null);
    final plain = utf8.encode(jsonEncode(dto));
    final nonce = randomBytes(12);
    final sealed = aesGcm(true, Uint8List.fromList(_sharedKey(me.key, to.key)), nonce, Uint8List.fromList(plain), utf8.encode(to.topic));
    // Holm lays a note out as nonce | tag | ciphertext; GCM gives ciphertext | tag.
    final ct = sealed.sublist(0, sealed.length - 16), tag = sealed.sublist(sealed.length - 16);
    return '$prefix${me.publicKey} ${base64Url(concat([nonce, tag, ct]))}';
  }

  static bool isNoteId(String? id) {
    if (id == null || !RegExp(r'^[A-Za-z0-9_-]{22}$').hasMatch(id)) return false;
    try {
      return fromBase64Url(id).length == 16;
    } catch (_) {
      return false;
    }
  }

  /// Mirrored notes have different transport ids. New notes carry an authenticated id; old notes without one use a
  /// hash of their canonical sealed text. Both forms are persisted by the receiver.
  static String replayId(String text, Note note) {
    final id = note.noteId;
    if (id != null && isNoteId(id)) return 'note:$id';
    // Open accepts optional base64 padding. Canonicalize both outer fields so adding '=' cannot evade the old fallback.
    final parts = text.substring(prefix.length).split(' ');
    final canonical = '$prefix${base64Url(fromBase64Url(parts[0]))} ${base64Url(fromBase64Url(parts[1]))}';
    return 'hash:${base64Url(sha256(utf8.encode(canonical)))}';
  }

  /// Future clock skew is tight; the past allowance covers the relay's week of retained notes plus skew.
  static bool isSentTimePlausible(int sent, int now) =>
      sent >= now - (7 * 24 * 60 * 60 + 300) && sent <= now + 300;

  static String chatText(String text) => base64Encode(utf8.encode(text));

  /// Opens a note from our inbox; null for anything that isn't a valid note for us. Returns the sender's key too.
  static (Note, String)? open(Identity me, String text) {
    if (!text.startsWith(prefix) || text.length > 4096) return null;
    final parts = text.substring(prefix.length).split(' ');
    if (parts.length != 2) return null;
    try {
      final sealed = fromBase64Url(parts[1]);
      if (sealed.length < 12 + 16 + 2) return null;
      final nonce = sealed.sublist(0, 12), tag = sealed.sublist(12, 28), ct = sealed.sublist(28);
      final plain = aesGcm(false, Uint8List.fromList(_sharedKey(me.key, parts[0])), nonce, concat([ct, tag]), utf8.encode(me.topic));
      final dto = jsonDecode(utf8.decode(plain)) as Map<String, dynamic>;
      final from = ((dto['F'] as String?) ?? '').runes.where((r) => r >= 0x20).take(40).map(String.fromCharCode).join().trim();
      final kind = dto['K'] as String?;
      if (kind == null) return null;
      final noteId = dto['J'];
      final fields = Map<String, dynamic>.from(dto);
      if (noteId is! String || !isNoteId(noteId)) fields.remove('J');
      return (Note(kind, from, (dto['T'] as num?)?.toInt() ?? 0, fields), parts[0]);
    } catch (_) {
      return null;
    }
  }
}

/// Short invite codes like HOLM-7KQ2X-9XF4M (FriendInvite.cs).
class Invite {
  static const _alphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';
  static final _salt = ascii.encode('Holm friend invite v1');
  static final _inText = RegExp(r'(?<![A-Za-z0-9])HOLM[-\s]?([0-9A-Za-z]{5})[-\s]?([0-9A-Za-z]{5})(?![A-Za-z0-9])', caseSensitive: false);

  static String? find(String? text) {
    if (text == null || text.trim().isEmpty || text.length > 10000) return null;
    String raw;
    final m = _inText.firstMatch(text);
    if (m != null) {
      raw = m.group(1)! + m.group(2)!;
    } else {
      raw = text.trim().replaceAll('-', '').replaceAll(' ', '');
      if (raw.toUpperCase().startsWith('HOLM')) raw = raw.substring(4);
      if (raw.length != 10) return null;
    }
    final chars = StringBuffer();
    for (final ch in raw.toUpperCase().split('')) {
      final c = switch (ch) { 'O' => '0', 'I' || 'L' => '1', _ => ch };
      if (!_alphabet.contains(c)) return null;
      chars.write(c);
    }
    final s = chars.toString();
    return 'HOLM-${s.substring(0, 5)}-${s.substring(5)}';
  }

  static List<int> _derive(String code, String purpose, int length) =>
      hkdf(ascii.encode(code), length, _salt, ascii.encode(purpose));

  static String topic(String code) {
    const lower = 'abcdefghijklmnopqrstuvwxyz234567';
    return 'holm-inv-${_derive(code, 'topic', 20).map((b) => lower[b & 31]).join()}';
  }

  static String proof(String code) => toHex(_derive(code, 'proof', 16));

  static Friend? open(String code, String text) {
    if (!text.startsWith('hinv1 ') || text.length > 4096) return null;
    try {
      final sealed = fromBase64Url(text.substring(6));
      if (sealed.length < 30) return null;
      final plain = aesGcm(false, Uint8List.fromList(_derive(code, 'key', 32)), sealed.sublist(0, 12),
          concat([sealed.sublist(28), sealed.sublist(12, 28)]), ascii.encode(topic(code)));
      final card = jsonDecode(utf8.decode(plain)) as Map<String, dynamic>;
      if (((card['E'] as num?)?.toInt() ?? 0) < unixNow()) return null;
      return parseFriendCode(card['C'] as String?);
    } catch (_) {
      return null;
    }
  }
}

/// Leaving notes in inboxes and following our own. Every request is HTTPS, HTTP redirects are refused, and the socket
/// is pinned to a resolved public IP while TLS still verifies the original host name.
class Inbox {
  static const maxPollBytes = 4 * 1024 * 1024;
  static final HttpClient _http = HttpClient()
    ..userAgent = _userAgent
    ..connectionTimeout = const Duration(seconds: 15)
    ..findProxy = (_) {
      return 'DIRECT';
    }
    ..connectionFactory = (uri, proxyHost, proxyPort) {
      if (proxyHost != null || proxyPort != null) throw const HttpException('Inbox proxies are not supported.');
      return connectPublic(uri);
    };

  static Uri _endpoint(String server, String topic, [String suffix = '']) {
    final normalized = normalizeHttpsServer(server);
    if (normalized == null) throw const HttpException('The friend inbox URL is not allowed.');
    // Every request to an inbox comes through here: Holm's servers are never contacted.
    if (retiredServers.contains(normalized.toLowerCase())) {
      throw const HttpException("This inbox is on Holm's server, which HolyNotch no longer uses. Add them again with a new code.");
    }
    return Uri.parse('$normalized/${Uri.encodeComponent(topic)}$suffix');
  }

  static String _host(String server) => Uri.tryParse(normalizeHttpsServer(server) ?? '')?.host ?? 'the server';

  static Future<void> post(Friend to, String note, {bool keep = true}) async {
    await _postTo(to.serverUrl, to.topic, note, keep);
  }

  static Future<void> _postTo(String server, String topic, String note, bool keep) async {
    try {
      final req = await _http.postUrl(_endpoint(server, topic))..followRedirects = false;
      req.headers.contentType = ContentType('text', 'plain', charset: 'utf-8');
      if (!keep) req.headers.set('Cache', 'no');
      req.add(utf8.encode(note));
      final res = await req.close().timeout(const Duration(seconds: 20));
      await res.drain<void>();
      if (res.statusCode < 200 || res.statusCode > 299) {
        throw HttpException('${_host(server)} refused the note (${res.statusCode})');
      }
    } on TimeoutException {
      throw HttpException("Couldn't reach ${_host(server)}");
    } on SocketException {
      throw HttpException("Couldn't reach ${_host(server)}");
    }
  }

  /// The JSON lines kept in an inbox since [since] (a Unix time or "12h").
  static Future<List<InboxEvent>> poll(String server, String topic, String since) async {
    final url = _endpoint(server, topic, '/json?poll=1&since=${Uri.encodeComponent(since)}');
    final req = await _http.getUrl(url)..followRedirects = false;
    final res = await req.close().timeout(const Duration(seconds: 20));
    final body = await readBoundedUtf8(res, maxBytes: maxPollBytes);
    if (res.statusCode != 200) throw HttpException('${_host(server)} answered ${res.statusCode}');
    return body.split('\n').map(InboxEvent.tryParse).whereType<InboxEvent>().toList();
  }

  /// Follows our inbox on the relay with a WebSocket: kept notes since [since], then each new one.
  static Future<WebSocket> follow(String topic, int? since, {String server = relayServer}) {
    final normalized = normalizeHttpsServer(server);
    if (normalized == null) throw const HttpException('The friend inbox URL is not allowed.');
    return WebSocket.connect(
      '${normalized.replaceFirst('https://', 'wss://')}/${Uri.encodeComponent(topic)}/ws${since != null ? '?since=$since' : ''}',
      headers: {'User-Agent': _userAgent},
      customClient: _http,
    ).timeout(const Duration(seconds: 20));
  }

  /// Invite cards go to HolyNotch's server only.
  static Future<void> publishInvite(String topic, String card) => _postTo(relayServer, topic, card, true);

  /// The invite card for [code] on HolyNotch's server.
  static Future<Friend?> fetchInvite(String code) async {
    final topic = Invite.topic(code);
    final List<InboxEvent> events;
    try {
      events = await poll(relayServer, topic, '12h');
    } catch (_) {
      throw const HttpException("Couldn't reach the invite server");
    }
    final cards = events.where((e) => e.isMessage && e.text != null).toList().reversed;
    for (final e in cards) {
      final friend = Invite.open(code, e.text!);
      if (friend != null) return friend;
    }
    return null;
  }
}

class InboxEvent {
  final bool isMessage;
  final int time;
  final String? id;
  final String? text;
  InboxEvent(this.isMessage, this.time, this.id, this.text);

  static InboxEvent? tryParse(String line) {
    if (line.trim().isEmpty) return null;
    try {
      final j = jsonDecode(line);
      if (j is! Map) return null;
      return InboxEvent(j['event'] == 'message', (j['time'] as num?)?.toInt() ?? 0, j['id'] as String?, j['message'] as String?);
    } catch (_) {
      return null;
    }
  }
}
