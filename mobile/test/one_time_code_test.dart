import 'package:flutter_test/flutter_test.dart';
import 'package:holm_mobile/net/one_time_code.dart';

void main() {
  test('finds the usual codes', () {
    expect(findOneTimeCode('G-482913 is your Google verification code.'), '482913');
    expect(findOneTimeCode('Your WhatsApp code: 123-456. Don\'t share it.'), '123456');
    expect(findOneTimeCode('Dein Bestätigungscode lautet 845 221'), '845221');
    expect(findOneTimeCode('Use 7731 as your PIN'), '7731');
    expect(findOneTimeCode('Ihre TAN für den Auftrag über 120.50 EUR: 90817264'), '90817264');
    expect(findOneTimeCode('Microsoft account security code: 5521'), '5521');
  });

  test('leaves ordinary messages alone', () {
    expect(findOneTimeCode('See you at 12:30 tomorrow'), isNull);
    expect(findOneTimeCode('Lunch was 24.50, send me 1250 please'), isNull); // no code word
    expect(findOneTimeCode('Call me at +41 79 123 45 67'), isNull);
    expect(findOneTimeCode('Your code expires in 2026'), isNull); // only a year
    expect(findOneTimeCode('Your total is 1234 CHF, code word: pizza'), isNull); // an amount
    expect(findOneTimeCode(null), isNull);
  });
}
