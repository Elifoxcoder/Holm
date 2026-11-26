import 'dart:convert';
import 'dart:typed_data';

import 'package:pointycastle/export.dart';

import 'crypto.dart';

/// One TOTP (RFC 6238) authenticator entry, synced end-to-end with Desktop via own-device notes.
class TotpEntry {
  final String id;
  String title;
  String? account;
  String secret;
  int digits;
  int period;
  String algorithm;
  int updatedAt;
  bool deleted;

  TotpEntry({
    String? id,
    required this.title,
    this.account,
    required this.secret,
    this.digits = 6,
    this.period = 30,
    this.algorithm = 'SHA1',
    int? updatedAt,
    this.deleted = false,
  })  : id = (id != null && id.isNotEmpty) ? id : newGuidN(),
        updatedAt = updatedAt ?? (DateTime.now().millisecondsSinceEpoch ~/ 1000);

  String get displayTitle {
    if (title.trim().isNotEmpty) return title.trim();
    if (account != null && account!.trim().isNotEmpty) return account!.trim();
    return 'Authenticator';
  }

  /// Wire JSON matches Desktop C# TotpEntry PascalCase property names for seamless cross-platform sync.
  Map<String, dynamic> toJson() => {
        'Id': id,
        'Title': title,
        'Account': account,
        'Secret': secret,
        'Digits': digits,
        'Period': period,
        'Algorithm': algorithm,
        'UpdatedAt': updatedAt,
        'Deleted': deleted,
      };

  static TotpEntry? fromJson(Object? raw) {
    if (raw is! Map<String, dynamic>) return null;
    final id = (raw['Id'] ?? raw['id']) as String?;
    final secret = (raw['Secret'] ?? raw['secret']) as String? ?? '';
    final deleted = (raw['Deleted'] ?? raw['deleted']) as bool? ?? false;
    if (id == null || id.isEmpty) return null;
    final norm = TotpCode.normalizeSecret(secret);
    if (!deleted && norm.length < 8) return null;
    return TotpEntry(
      id: id,
      title: ((raw['Title'] ?? raw['title']) as String? ?? '').trim(),
      account: (raw['Account'] ?? raw['account']) as String?,
      secret: norm,
      digits: ((raw['Digits'] ?? raw['digits']) as num?)?.toInt() ?? 6,
      period: ((raw['Period'] ?? raw['period']) as num?)?.toInt() ?? 30,
      algorithm: ((raw['Algorithm'] ?? raw['algorithm']) as String? ?? 'SHA1').toUpperCase(),
      updatedAt: ((raw['UpdatedAt'] ?? raw['updatedAt']) as num?)?.toInt() ?? 0,
      deleted: deleted,
    );
  }
}

class TotpCode {
  static const _alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';

  static String normalizeSecret(String? raw) {
    if (raw == null || raw.trim().isEmpty) return '';
    final sb = StringBuffer();
    for (var i = 0; i < raw.length; i++) {
      final ch = raw[i];
      if (ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r' || ch == '-' || ch == '=') continue;
      final u = ch.toUpperCase();
      if (!_alphabet.contains(u)) return '';
      sb.write(u);
    }
    return sb.toString();
  }

  static Uint8List? decodeBase32(String? raw) {
    final clean = normalizeSecret(raw);
    if (clean.length < 8) return null;
    final outLen = (clean.length * 5) ~/ 8;
    if (outLen <= 0) return null;
    final out = Uint8List(outLen);
    var buffer = 0, bits = 0, idx = 0;
    for (var i = 0; i < clean.length; i++) {
      final v = _alphabet.indexOf(clean[i]);
      if (v < 0) return null;
      buffer = (buffer << 5) | v;
      bits += 5;
      if (bits >= 8) {
        bits -= 8;
        if (idx < out.length) out[idx++] = (buffer >> bits) & 0xff;
      }
    }
    return out;
  }

  static String encodeBase32(Uint8List data) {
    if (data.isEmpty) return '';
    final sb = StringBuffer();
    var buffer = 0, bits = 0;
    for (final b in data) {
      buffer = (buffer << 8) | b;
      bits += 8;
      while (bits >= 5) {
        bits -= 5;
        sb.write(_alphabet[(buffer >> bits) & 31]);
      }
    }
    if (bits > 0) sb.write(_alphabet[(buffer << (5 - bits)) & 31]);
    return sb.toString();
  }

  static ({String code, String nextCode, int remaining, int period}) generate(TotpEntry entry, {int? unixSeconds}) {
    final period = (entry.period >= 5 && entry.period <= 300) ? entry.period : 30;
    final digits = (entry.digits == 6 || entry.digits == 7 || entry.digits == 8) ? entry.digits : 6;
    final now = unixSeconds ?? (DateTime.now().millisecondsSinceEpoch ~/ 1000);
    final counter = now ~/ period;
    final remaining = period - (now % period);

    final key = decodeBase32(entry.secret);
    if (key == null || key.isEmpty) {
      final dash = '-' * digits;
      return (code: dash, nextCode: dash, remaining: remaining, period: period);
    }

    final algo = entry.algorithm.toUpperCase();
    final code = _computeAt(key, counter, digits, algo);
    final next = _computeAt(key, counter + 1, digits, algo);
    return (code: code, nextCode: next, remaining: remaining, period: period);
  }

  static String _computeAt(Uint8List key, int counter, int digits, String algo) {
    var c = counter;
    final msg = Uint8List(8);
    for (var i = 7; i >= 0; i--) {
      msg[i] = c & 0xff;
      c >>= 8;
    }

    final HMac hmac;
    switch (algo) {
      case 'SHA256':
        hmac = HMac(SHA256Digest(), 64);
      case 'SHA512':
        hmac = HMac(SHA512Digest(), 128);
      default:
        hmac = HMac(SHA1Digest(), 64);
    }
    hmac.init(KeyParameter(key));
    final hash = hmac.process(msg);

    final offset = hash[hash.length - 1] & 0x0f;
    final binary = ((hash[offset] & 0x7f) << 24) |
        ((hash[offset + 1] & 0xff) << 16) |
        ((hash[offset + 2] & 0xff) << 8) |
        (hash[offset + 3] & 0xff);

    final mod = digits == 7 ? 10000000 : digits == 8 ? 100000000 : 1000000;
    return (binary % mod).toString().padLeft(digits, '0');
  }

  static String format(String code) {
    if (code.length == 6) return '${code.substring(0, 3)} ${code.substring(3)}';
    if (code.length == 8) return '${code.substring(0, 4)} ${code.substring(4)}';
    return code;
  }
}

/// Parses otpauth:// URIs, Google Authenticator otpauth-migration:// protobuf exports,
/// Aegis / 2FAS JSON backups, newline-separated URIs, and plain Base32 secrets.
class TotpParser {
  static List<TotpEntry> parseAny(String? input, {String? fallbackTitle}) {
    if (input == null || input.trim().isEmpty) return [];
    final trimmed = input.trim();
    final results = <TotpEntry>[];

    if (trimmed.startsWith('{') || trimmed.startsWith('[')) {
      results.addAll(_parseJsonExport(trimmed));
      if (results.isNotEmpty) return results;
    }

    for (final rawLine in trimmed.split(RegExp(r'[\r\n]+'))) {
      final line = rawLine.trim();
      if (line.isEmpty) continue;
      final lower = line.toLowerCase();
      if (lower.startsWith('otpauth-migration://')) {
        results.addAll(parseGoogleMigration(line));
      } else if (lower.startsWith('otpauth://')) {
        final e = parseOtpAuthUri(line);
        if (e != null) results.add(e);
      }
    }
    if (results.isNotEmpty) return results;

    final clean = TotpCode.normalizeSecret(trimmed);
    if (clean.length >= 16 && clean.length <= 128 && TotpCode.decodeBase32(clean) != null) {
      results.add(TotpEntry(
        title: (fallbackTitle != null && fallbackTitle.trim().isNotEmpty) ? fallbackTitle.trim() : 'Authenticator',
        secret: clean,
      ));
    }
    return results;
  }

  static TotpEntry? parseOtpAuthUri(String raw) {
    final uri = Uri.tryParse(raw.trim());
    if (uri == null || uri.scheme.toLowerCase() != 'otpauth' || uri.host.toLowerCase() != 'totp') return null;
    final params = {for (final e in uri.queryParameters.entries) e.key.toLowerCase(): e.value};
    final secret = TotpCode.normalizeSecret(params['secret']);
    if (secret.length < 8 || TotpCode.decodeBase32(secret) == null) return null;

    var label = Uri.decodeComponent(uri.path.startsWith('/') ? uri.path.substring(1) : uri.path);
    var issuer = params['issuer']?.trim();
    String? account;
    final colon = label.indexOf(':');
    if (colon >= 0) {
      if (issuer == null || issuer.isEmpty) issuer = label.substring(0, colon).trim();
      account = label.substring(colon + 1).trim();
    } else if (label.trim().isNotEmpty) {
      if (issuer == null || issuer.isEmpty) {
        issuer = label.trim();
      } else {
        account = label.trim();
      }
    }

    final d = int.tryParse(params['digits'] ?? '');
    final digits = (d == 6 || d == 7 || d == 8) ? d! : 6;
    final p = int.tryParse(params['period'] ?? '');
    final period = (p != null && p >= 5 && p <= 300) ? p : 30;
    final a = (params['algorithm'] ?? 'SHA1').toUpperCase();
    final algo = (a == 'SHA256' || a == 'SHA512') ? a : 'SHA1';

    final title = (issuer != null && issuer.isNotEmpty)
        ? issuer
        : ((account != null && account.isNotEmpty) ? account : 'Authenticator');
    return TotpEntry(
      title: title,
      account: (account != null && account.isNotEmpty && account.toLowerCase() != title.toLowerCase()) ? account : null,
      secret: secret,
      digits: digits,
      period: period,
      algorithm: algo,
    );
  }

  static List<TotpEntry> parseGoogleMigration(String raw) {
    final uri = Uri.tryParse(raw.trim());
    if (uri == null) return [];
    final data = uri.queryParameters['data'];
    if (data == null || data.isEmpty) return [];

    Uint8List bytes;
    try {
      var norm = data.replaceAll(' ', '+').replaceAll('-', '+').replaceAll('_', '/');
      final pad = norm.length % 4;
      if (pad > 0) norm += '=' * (4 - pad);
      bytes = base64Decode(norm);
    } catch (_) {
      return [];
    }

    final results = <TotpEntry>[];
    final reader = _ProtoReader(bytes);
    while (!reader.eof) {
      final tag = reader.readVarint();
      if (tag == null) break;
      final field = tag >> 3;
      final wire = tag & 7;
      if (field == 1 && wire == 2) {
        final slice = reader.readBytes();
        if (slice == null) break;
        final entry = _decodeGoogleParam(slice);
        if (entry != null) results.add(entry);
      } else if (!reader.skip(wire)) {
        break;
      }
    }
    return results;
  }

  static TotpEntry? _decodeGoogleParam(Uint8List buf) {
    Uint8List? secretBytes;
    String? name, issuer;
    var algo = 1, digits = 1, type = 2;

    final r = _ProtoReader(buf);
    while (!r.eof) {
      final tag = r.readVarint();
      if (tag == null) break;
      final field = tag >> 3;
      final wire = tag & 7;
      switch (field) {
        case 1 when wire == 2:
          secretBytes = r.readBytes();
        case 2 when wire == 2:
          final b = r.readBytes();
          if (b != null) name = utf8.decode(b, allowMalformed: true);
        case 3 when wire == 2:
          final b = r.readBytes();
          if (b != null) issuer = utf8.decode(b, allowMalformed: true);
        case 4 when wire == 0:
          algo = r.readVarint() ?? 1;
        case 5 when wire == 0:
          digits = r.readVarint() ?? 1;
        case 6 when wire == 0:
          type = r.readVarint() ?? 2;
        default:
          if (!r.skip(wire)) return null;
      }
    }
    if (type == 1 || secretBytes == null || secretBytes.isEmpty) return null;
    final secret = TotpCode.encodeBase32(secretBytes);
    if (secret.length < 8) return null;

    var title = issuer?.trim();
    var account = name?.trim();
    if (account != null && account.contains(':')) {
      final c = account.indexOf(':');
      if (title == null || title.isEmpty) title = account.substring(0, c).trim();
      account = account.substring(c + 1).trim();
    }
    final finalTitle = (title != null && title.isNotEmpty)
        ? title
        : ((account != null && account.isNotEmpty) ? account : 'Authenticator');
    return TotpEntry(
      title: finalTitle,
      account: (account != null && account.isNotEmpty && account.toLowerCase() != finalTitle.toLowerCase()) ? account : null,
      secret: secret,
      digits: digits == 2 ? 8 : 6,
      period: 30,
      algorithm: algo == 2 ? 'SHA256' : algo == 3 ? 'SHA512' : 'SHA1',
    );
  }

  static List<TotpEntry> _parseJsonExport(String jsonText) {
    final results = <TotpEntry>[];
    try {
      final decoded = jsonDecode(jsonText);
      if (decoded is! Map<String, dynamic>) return results;

      // Aegis JSON
      final db = decoded['db'];
      if (db is Map<String, dynamic> && db['entries'] is List) {
        for (final raw in db['entries'] as List) {
          if (raw is! Map<String, dynamic>) continue;
          if ((raw['type'] as String?)?.toLowerCase() == 'hotp') continue;
          final info = raw['info'];
          if (info is! Map<String, dynamic>) continue;
          final secret = TotpCode.normalizeSecret(info['secret'] as String?);
          if (secret.length < 8) continue;
          final iss = (raw['issuer'] as String?)?.trim();
          final name = (raw['name'] as String?)?.trim();
          final title = (iss != null && iss.isNotEmpty) ? iss : ((name != null && name.isNotEmpty) ? name : 'Authenticator');
          results.add(TotpEntry(
            title: title,
            account: (name != null && name.isNotEmpty && name.toLowerCase() != title.toLowerCase()) ? name : null,
            secret: secret,
            digits: (info['digits'] as num?)?.toInt() ?? 6,
            period: (info['period'] as num?)?.toInt() ?? 30,
            algorithm: ((info['algo'] as String?) ?? 'SHA1').toUpperCase(),
          ));
        }
        return results;
      }

      // 2FAS JSON
      if (decoded['services'] is List) {
        for (final raw in decoded['services'] as List) {
          if (raw is! Map<String, dynamic>) continue;
          final secret = TotpCode.normalizeSecret(raw['secret'] as String?);
          if (secret.length < 8) continue;
          final otp = raw['otp'] as Map<String, dynamic>?;
          if ((otp?['tokenType'] as String?)?.toUpperCase() == 'HOTP') continue;
          final account = (otp?['account'] as String?)?.trim();
          final title = ((raw['name'] as String?)?.trim().isNotEmpty == true)
              ? (raw['name'] as String).trim()
              : (account ?? 'Authenticator');
          results.add(TotpEntry(
            title: title,
            account: (account != null && account.isNotEmpty && account.toLowerCase() != title.toLowerCase()) ? account : null,
            secret: secret,
            digits: (otp?['digits'] as num?)?.toInt() ?? 6,
            period: (otp?['period'] as num?)?.toInt() ?? 30,
            algorithm: ((otp?['algorithm'] as String?) ?? 'SHA1').toUpperCase(),
          ));
        }
      }
    } catch (_) {}
    return results;
  }
}

class _ProtoReader {
  final Uint8List _b;
  int _pos = 0;
  _ProtoReader(this._b);

  bool get eof => _pos >= _b.length;

  int? readVarint() {
    var result = 0, shift = 0;
    while (_pos < _b.length && shift < 63) {
      final byte = _b[_pos++];
      result |= (byte & 0x7f) << shift;
      if ((byte & 0x80) == 0) return result;
      shift += 7;
    }
    return null;
  }

  Uint8List? readBytes() {
    final len = readVarint();
    if (len == null || len < 0 || _pos + len > _b.length) return null;
    final out = _b.sublist(_pos, _pos + len);
    _pos += len;
    return out;
  }

  bool skip(int wire) {
    switch (wire) {
      case 0:
        return readVarint() != null;
      case 1:
        if (_pos + 8 > _b.length) return false;
        _pos += 8;
        return true;
      case 2:
        return readBytes() != null;
      case 5:
        if (_pos + 4 > _b.length) return false;
        _pos += 4;
        return true;
      default:
        return false;
    }
  }
}
