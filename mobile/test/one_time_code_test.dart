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

  test('any message with a 4, 6 or 3+3 digit number counts, as a notification brings it', () {
    expect(findCodeInNotification('Luis', 'WhatsApp · 715358'), '715358');
    expect(findCodeInNotification('Family', 'WhatsApp · Luis: 715 358'), '715358');
    expect(findCodeInNotification('Luis', 'WhatsApp · hier der code 482-913 für dich'), '482913');
    expect(findCodeInNotification('Luis', 'WhatsApp · nimm 4821'), '4821');
    expect(findCodeInNotification('Google', 'Messages · G-482913 is your Google verification code.'), '482913');
    expect(findCodeInNotification('Bank', 'Messages · Ihre TAN: 90817264'), '90817264'); // 8 digits with a code word
    expect(findCodeInNotification('Luis', 'WhatsApp · 2026'), isNull); // a year
    expect(findCodeInNotification('Luis', 'WhatsApp · see you at 12:30'), isNull); // a time
    expect(findCodeInNotification('Luis', 'WhatsApp · das kostet 1250.50 CHF'), isNull); // an amount
    expect(findCodeInNotification('Luis', 'WhatsApp · ruf an: +41 79 123 45 67'), isNull); // a phone number
    expect(findCodeInNotification('Luis', 'WhatsApp · 12345'), isNull); // 5 digits without a code word
    expect(findCodeInNotification('Luis', 'WhatsApp · 12'), isNull);
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
