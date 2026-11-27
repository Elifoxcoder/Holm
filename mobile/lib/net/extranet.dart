import 'dart:convert';
import 'dart:io';
import 'dart:math';

import 'crypto.dart';

/// One work interval recorded on extranet.bbcag.ch for today.
class ExtranetInterval {
  final int fromMinutes;
  final int? toMinutes;
  const ExtranetInterval(this.fromMinutes, this.toMinutes);
}

/// Live calculation of today's worked duration, remaining time, and target finish time.
class ExtranetSnapshot {
  final DateTime fetchedAt;
  final List<ExtranetInterval> intervals;
  final int targetMinutes;
  final bool signedIn;
  final String? error;

  const ExtranetSnapshot({
    required this.fetchedAt,
    required this.intervals,
    required this.targetMinutes,
    required this.signedIn,
    this.error,
  });

  bool get isClockedIn => intervals.isNotEmpty && intervals.last.toMinutes == null;

  Duration get target => Duration(minutes: targetMinutes.clamp(60, 16 * 60));

  Duration workedAt(DateTime now) {
    final nowMins = now.hour * 60 + now.minute + (now.second / 60.0);
    double totalMins = 0;
    for (final iv in intervals) {
      final end = iv.toMinutes?.toDouble() ?? nowMins;
      final diff = end - iv.fromMinutes;
      if (diff > 0) totalMins += diff;
    }
    return Duration(seconds: (totalMins * 60).round());
  }

  Duration remainingAt(DateTime now) {
    final rem = target - workedAt(now);
    return rem.isNegative ? Duration.zero : rem;
  }

  Duration overtimeAt(DateTime now) {
    final over = workedAt(now) - target;
    return over.isNegative ? Duration.zero : over;
  }

  bool canBadgeOutAt(DateTime now) => intervals.isNotEmpty && workedAt(now) >= target;

  DateTime? finishAt(DateTime now) {
    final rem = remainingAt(now);
    return rem <= Duration.zero ? null : now.add(rem);
  }

  static String formatDuration(Duration d) {
    final totalMin = (d.inSeconds / 60).ceil().clamp(0, 24 * 60);
    final h = totalMin ~/ 60;
    final m = totalMin % 60;
    return h > 0 ? '${h}h ${m.toString().padLeft(2, '0')}m' : '${m}m';
  }

  static String formatHm(DateTime t) =>
      '${t.hour.toString().padLeft(2, '0')}:${t.minute.toString().padLeft(2, '0')}';
}

class ExtranetAuthResult {
  final String accessToken;
  final String refreshToken;
  final int expiresInSeconds;
  final String? userName;
  const ExtranetAuthResult(this.accessToken, this.refreshToken, this.expiresInSeconds, this.userName);
}

/// Per-device read-only client for extranet.bbcag.ch (OAuth 2.0 + PKCE via api.extranet.bbcag.ch).
/// Never stores passwords and never modifies any timesheet or badge data.
class ExtranetClient {
  static const webUrl = 'https://extranet.bbcag.ch/timesheet/record';
  static const _apiBase = 'https://api.extranet.bbcag.ch';
  static const _clientId = '7R6bulmurfMbXsYQUdOKa1Hg9Z3I5QNq';
  static const _redirectUri = 'https://extranet.bbcag.ch';
  static const _scope = 'account:manage';

  static String _randomBase64Url(int length) {
    final rng = Random.secure();
    final bytes = List<int>.generate(length, (_) => rng.nextInt(256));
    return base64UrlEncode(bytes).replaceAll('=', '');
  }

  static Future<ExtranetAuthResult> signIn(String email, String password) async {
    final cleanEmail = email.trim();
    if (cleanEmail.isEmpty || password.isEmpty) {
      throw Exception('Bitte E-Mail und Passwort eingeben.');
    }
    final client = HttpClient()..connectionTimeout = const Duration(seconds: 20);
    final cookies = <Cookie>[];
    try {
      final verifier = _randomBase64Url(32);
      final challenge = base64UrlEncode(sha256(ascii.encode(verifier))).replaceAll('=', '');
      final state = _randomBase64Url(16);

      final authUri = Uri.parse('$_apiBase/oauth/auth').replace(queryParameters: {
        'response_type': 'code',
        'client_id': _clientId,
        'redirect_uri': _redirectUri,
        'scope': _scope,
        'state': state,
        'code_challenge': challenge,
        'code_challenge_method': 'S256',
      });
      final req1 = await client.getUrl(authUri);
      req1.followRedirects = false;
      final res1 = await req1.close();
      await res1.drain<void>();
      cookies.addAll(res1.cookies);
      final loc = res1.headers.value(HttpHeaders.locationHeader) ?? '';
      if (!loc.contains('/flow/') || loc.contains('/flow/error')) {
        throw Exception('Extranet-Login konnte nicht gestartet werden.');
      }
      final flowId = loc.split('/flow/')[1].split('?').first;

      final req2 = await client.postUrl(Uri.parse('$_apiBase/api/account/v1/authentication/flow/${Uri.encodeComponent(flowId)}/usernamepassword'));
      req2.headers.contentType = ContentType.json;
      req2.cookies.addAll(cookies);
      req2.write(jsonEncode({'username': cleanEmail, 'password': password}));
      final res2 = await req2.close();
      final body2 = await res2.transform(utf8.decoder).join();
      cookies.addAll(res2.cookies);
      if (res2.statusCode < 200 || res2.statusCode >= 300) {
        throw Exception('Ungültige Extranet E-Mail oder falsches Passwort.');
      }
      final j2 = jsonDecode(body2) as Map<String, dynamic>;
      final userName = j2['userName'] as String?;
      final step = j2['step'] as String?;
      if (step != null && step.toLowerCase() != 'finish') {
        throw Exception('Zusätzlicher Anmeldeschritt erforderlich ($step).');
      }

      final req3 = await client.postUrl(Uri.parse('$_apiBase/api/account/v1/authentication/flow/${Uri.encodeComponent(flowId)}/finish'));
      req3.headers.contentType = ContentType.json;
      req3.cookies.addAll(cookies);
      req3.write('{}');
      final res3 = await req3.close();
      final body3 = await res3.transform(utf8.decoder).join();
      if (res3.statusCode < 200 || res3.statusCode >= 300) {
        throw Exception('Extranet-Anmeldung konnte nicht abgeschlossen werden.');
      }
      final j3 = jsonDecode(body3) as Map<String, dynamic>;
      final redir = Uri.tryParse(j3['redirectUri'] as String? ?? '');
      final code = redir?.queryParameters['code'];
      if (code == null || code.isEmpty) {
        throw Exception('Kein OAuth-Code vom Extranet erhalten.');
      }

      final req4 = await client.postUrl(Uri.parse('$_apiBase/oauth/token'));
      req4.headers.contentType = ContentType('application', 'x-www-form-urlencoded');
      req4.write(Uri(queryParameters: {
        'grant_type': 'authorization_code',
        'code': code,
        'redirect_uri': _redirectUri,
        'client_id': _clientId,
        'code_verifier': verifier,
      }).query);
      final res4 = await req4.close();
      final body4 = await res4.transform(utf8.decoder).join();
      if (res4.statusCode < 200 || res4.statusCode >= 300) {
        throw Exception('OAuth-Token konnte nicht abgerufen werden.');
      }
      return _parseToken(body4, userName);
    } finally {
      client.close(force: true);
    }
  }

  static Future<ExtranetAuthResult> refresh(String refreshToken) async {
    final client = HttpClient()..connectionTimeout = const Duration(seconds: 15);
    try {
      final req = await client.postUrl(Uri.parse('$_apiBase/oauth/token'));
      req.headers.contentType = ContentType('application', 'x-www-form-urlencoded');
      req.write(Uri(queryParameters: {
        'grant_type': 'refresh_token',
        'refresh_token': refreshToken,
        'client_id': _clientId,
      }).query);
      final res = await req.close();
      final body = await res.transform(utf8.decoder).join();
      if (res.statusCode < 200 || res.statusCode >= 300) {
        throw Exception('Extranet-Sitzung abgelaufen (${res.statusCode}). Bitte neu anmelden.');
      }
      return _parseToken(body, null);
    } finally {
      client.close(force: true);
    }
  }

  static Future<List<ExtranetInterval>> fetchToday(String accessToken) async {
    final client = HttpClient()..connectionTimeout = const Duration(seconds: 15);
    try {
      final req = await client.getUrl(Uri.parse('$_apiBase/api/timesheet/v1/records/today'));
      req.headers.set(HttpHeaders.authorizationHeader, 'Bearer $accessToken');
      final res = await req.close();
      final body = await res.transform(utf8.decoder).join();
      if (res.statusCode < 200 || res.statusCode >= 300) {
        throw Exception('Extranet HTTP ${res.statusCode}');
      }
      return parseTodayRecords(body);
    } finally {
      client.close(force: true);
    }
  }

  static List<ExtranetInterval> parseTodayRecords(String jsonText) {
    final decoded = jsonDecode(jsonText);
    if (decoded is! List) return const [];
    final out = <ExtranetInterval>[];
    for (final raw in decoded) {
      if (raw is! Map) continue;
      final cat = raw['category'] as String?;
      if (cat != null && cat.toLowerCase() != 'work') continue;
      final iv = raw['interval'];
      if (iv is! Map) continue;
      final from = _parseHm(iv['from'] as String?);
      if (from == null) continue;
      final to = _parseHm(iv['to'] as String?);
      out.add(ExtranetInterval(from, to));
    }
    out.sort((a, b) => a.fromMinutes.compareTo(b.fromMinutes));
    return out;
  }

  static int? _parseHm(String? s) {
    if (s == null || s.trim().isEmpty) return null;
    final parts = s.trim().split(':');
    if (parts.length < 2) return null;
    final h = int.tryParse(parts[0]);
    final m = int.tryParse(parts[1]);
    if (h == null || m == null || h < 0 || h > 23 || m < 0 || m > 59) return null;
    return h * 60 + m;
  }

  static ExtranetAuthResult _parseToken(String jsonText, String? userName) {
    final j = jsonDecode(jsonText) as Map<String, dynamic>;
    final access = j['access_token'] as String? ?? '';
    final refresh = j['refresh_token'] as String? ?? '';
    final expires = (j['expires_in'] as num?)?.toInt() ?? 300;
    if (access.isEmpty) throw Exception('Leeres Access-Token vom Extranet.');
    return ExtranetAuthResult(access, refresh, expires, userName);
  }
}
