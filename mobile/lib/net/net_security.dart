import 'dart:async';
import 'dart:io';
import 'dart:typed_data';

/// Destination checks for friend inboxes. A friendly host name is not enough: the address used for the socket is
/// checked too, so a DNS answer cannot change between validation and connect (DNS rebinding).
bool isPublicAddress(InternetAddress address) {
  final raw = address.rawAddress;
  if (address.type == InternetAddressType.IPv6 && raw.length == 16) {
    // ::ffff:0:0/96: judge the embedded IPv4 address, not the apparently global IPv6 wrapper.
    var mapped = true;
    for (var i = 0; i < 10; i++) {
      if (raw[i] != 0) mapped = false;
    }
    if (mapped && raw[10] == 0xff && raw[11] == 0xff) {
      return isPublicAddress(InternetAddress.fromRawAddress(Uint8List.sublistView(raw, 12), type: InternetAddressType.IPv4));
    }
    // Deprecated IPv4-compatible form (::/96), including :: and ::1.
    var compatible = true;
    for (var i = 0; i < 12; i++) {
      if (raw[i] != 0) compatible = false;
    }
    if (compatible) {
      return isPublicAddress(InternetAddress.fromRawAddress(Uint8List.sublistView(raw, 12), type: InternetAddressType.IPv4));
    }
  }
  if (address.type == InternetAddressType.IPv4 && raw.length == 4) {
    final a = raw[0], b = raw[1], c = raw[2];
    return a != 0 && a != 127 && a < 224 &&
        !(a == 10) &&
        !(a == 172 && b >= 16 && b <= 31) &&
        !(a == 192 && b == 168) &&
        !(a == 169 && b == 254) &&
        !(a == 100 && b >= 64 && b <= 127) &&
        !(a == 198 && (b == 18 || b == 19)) &&
        !(a == 192 && b == 0 && (c == 0 || c == 2)) &&
        !(a == 198 && b == 51 && c == 100) &&
        !(a == 203 && b == 0 && c == 113);
  }
  if (address.type != InternetAddressType.IPv6 || raw.length != 16) return false;
  return !address.isLoopback &&
      !address.isLinkLocal &&
      !address.isMulticast &&
      (raw[0] & 0xfe) != 0xfc &&
      !((raw[0] & 0xfe) == 0xfe && (raw[1] & 0xc0) == 0xc0) && // fec0::/10 site-local
      !(raw[0] == 0x20 && raw[1] == 0x01 && raw[2] == 0x0d && raw[3] == 0xb8) && // documentation
      !(raw[0] == 0x00 && raw[1] == 0x64 && raw[2] == 0xff && raw[3] == 0x9b) && // NAT64
      !(raw[0] == 0x20 && raw[1] == 0x02) && // 6to4
      !(raw[0] == 0x20 && raw[1] == 0x01 && raw[2] == 0x00 && raw[3] == 0x00); // Teredo
}

/// Cheap policy before DNS. Resolved addresses must still pass [isPublicAddress].
bool isPublicHost(String? host) {
  if (host == null || host.trim().isEmpty) return false;
  var name = host.trim();
  if (name.startsWith('[') && name.endsWith(']')) name = name.substring(1, name.length - 1);
  final literal = InternetAddress.tryParse(name);
  if (literal != null) return isPublicAddress(literal);
  name = name.toLowerCase();
  while (name.endsWith('.')) {
    name = name.substring(0, name.length - 1);
  }
  if (name.length > 253 || !name.contains('.')) return false;
  if (name == 'localhost' || name.endsWith('.localhost')) return false;
  for (final suffix in ['.local', '.lan', '.internal', '.home', '.test', '.invalid']) {
    if (name.endsWith(suffix)) return false;
  }
  final label = RegExp(r'^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$');
  return name.split('.').every(label.hasMatch);
}

/// The URL policy shared by friend cards, saved cards and every inbox request. Null means no value was supplied.
String? normalizeHttpsServer(String? server) {
  if (server == null) return null;
  var candidate = server.trim();
  if (candidate.length < 8 || candidate.length > 2048) return null;
  if (candidate.runes.any((r) => r <= 0x20 || r == 0x7f)) return null;
  final uri = Uri.tryParse(candidate);
  if (uri == null || uri.scheme != 'https' || uri.host.isEmpty || uri.userInfo.isNotEmpty) return null;
  if (uri.authority.contains('@') || candidate.contains('?') || candidate.contains('#')) return null;
  if (!isPublicHost(uri.host)) return null;
  while (candidate.endsWith('/')) {
    candidate = candidate.substring(0, candidate.length - 1);
  }
  return candidate;
}

/// Resolves once and connects to one of the checked public addresses. TLS still uses the original URI host name.
Future<ConnectionTask<Socket>> connectPublic(Uri uri,
    {Future<List<InternetAddress>> Function(String host)? resolve}) async {
  if (uri.scheme != 'https' || uri.userInfo.isNotEmpty || !isPublicHost(uri.host)) {
    throw const HttpException('The request destination is not a public HTTPS server.');
  }
  var host = uri.host;
  if (host.startsWith('[') && host.endsWith(']')) host = host.substring(1, host.length - 1);
  final literal = InternetAddress.tryParse(host);
  final addresses = literal != null
      ? [literal]
      : await (resolve ?? InternetAddress.lookup)(host).timeout(const Duration(seconds: 15));
  final public = addresses.where(isPublicAddress).toList()
    ..sort((a, b) => (a.type == InternetAddressType.IPv4 ? 0 : 1) - (b.type == InternetAddressType.IPv4 ? 0 : 1));
  if (public.isEmpty) throw const HttpException('The request destination has no public address.');
  Object? last;
  for (final address in public) {
    final attempt = await Socket.startConnect(address, uri.port);
    try {
      final socket = await attempt.socket.timeout(const Duration(seconds: 15));
      return ConnectionTask.fromSocket(Future.value(socket), () => socket.destroy());
    } catch (e) {
      attempt.cancel();
      last = e;
    }
  }
  throw HttpException('The request destination could not be reached. ${last ?? ''}'.trim());
}
