// Finds a one-time code (a login, verification or TAN code) in a notification, so the phone can offer it on the PC.
// Only with a word that says it is a code nearby; years, amounts, times and phone numbers are left alone.

final _keyword = RegExp(
  r'(code|codice|código|otp|passcode|pass code|password|passwort|kennwort|pin\b|tan\b|verif|bestätig|bestaetig|sicherheits|anmelde|login|log-in|sign in|sign-in|2fa|two-factor|zwei-faktor|einmal|one-time|authenti)',
  caseSensitive: false,
);

// 4 to 8 digits, or two groups of three ("123 456", "123-456"), optionally after a letter prefix ("G-123456").
// Not part of a longer number, an amount (12.50, 1,000), a time (12:30), a date (01/02) or a phone number (+41…).
final _candidate = RegExp(r'(?<![\d.,:/+\-])(?:[A-Z]{1,3}-)?(\d{3}[ \-]\d{3}|\d{4,8})(?![\d:/%€$]|[.,]\d|\s?(?:€|\$|chf|eur|usd|fr\.))', caseSensitive: false);

/// The code in [text] (digits only, as it is typed), or null when there is none or it isn't clearly a code.
String? findOneTimeCode(String? text) {
  if (text == null || text.length > 2000 || !_keyword.hasMatch(text)) return null;
  final found = <String>[];
  for (final m in _candidate.allMatches(text)) {
    final digits = m.group(1)!.replaceAll(RegExp(r'[ \-]'), '');
    if (digits.length == 4) {
      final n = int.parse(digits);
      if (n >= 1900 && n <= 2099) continue; // a year
    }
    found.add(digits);
  }
  if (found.isEmpty) return null;
  // The usual lengths first: 6, then 8, 5, 4, 7.
  const preference = [6, 8, 5, 4, 7];
  found.sort((a, b) => preference.indexOf(a.length).compareTo(preference.indexOf(b.length)));
  return found.first;
}
