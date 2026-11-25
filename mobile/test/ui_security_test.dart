import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:holm_mobile/app_state.dart';
import 'package:holm_mobile/net/transfer.dart';
import 'package:holm_mobile/ui/file_confirmation.dart';
import 'package:holm_mobile/ui/file_safety.dart';
import 'package:holm_mobile/ui/offer_confirmation.dart';
import 'package:material_ui/material_ui.dart';

Future<BuildContext> prompt(WidgetTester tester, Future<bool> Function(BuildContext) action, ValueChanged<bool?> result) async {
  late BuildContext context;
  await tester.pumpWidget(MaterialApp(
    home: Scaffold(
      body: Builder(builder: (value) {
        context = value;
        return TextButton(onPressed: () async => result(await action(value)), child: const Text('Prompt'));
      }),
    ),
  ));
  await tester.tap(find.text('Prompt'));
  await tester.pump();
  await tester.pump(const Duration(milliseconds: 300));
  return context;
}

void main() {
  testWidgets('nearby offers say unauthenticated and show every file name and size', (tester) async {
    bool? result;
    await prompt(tester, (context) => confirmOffer(context, ShareOffer('Laptop', [
      const ShareFile('report.pdf', 1234),
      const ShareFile('photo.jpg', 5678),
    ], false)), (value) => result = value);
    expect(find.textContaining('Unauthenticated nearby offer'), findsOneWidget);
    expect(find.text('report.pdf'), findsOneWidget);
    expect(find.text('photo.jpg'), findsOneWidget);
    expect(find.textContaining('1.2 KB'), findsOneWidget);
    await tester.tap(find.text('Decline'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 300));
    expect(result, isFalse);
  });

  testWidgets('account offers are labelled verified', (tester) async {
    bool? result;
    await prompt(tester,
        (context) => confirmOffer(context, ShareOffer('Laptop', const [ShareFile('Text.txt', 2)], true,
            authenticated: true, senderDeviceId: 'a' * 32)),
        (value) => result = value);
    expect(find.text('Verified HolyNotch account device'), findsOneWidget);
    expect(find.textContaining('Unauthenticated'), findsNothing);
    await tester.tap(find.text('Copy'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 300));
    expect(result, isTrue);
  });

  testWidgets('shared files show their actual names and sizes before sending', (tester) async {
    final Directory dir = (await tester.runAsync<Directory>(() => Directory.systemTemp.createTemp('holm-confirm')))!;
    final file = File('${dir.path}/shared.bin')..writeAsBytesSync([1, 2, 3, 4, 5]);
    bool? result;
    try {
      await prompt(tester, (context) => confirmOutgoingFiles(context, [file.path]), (value) => result = value);
      expect(find.text('Send these files?'), findsOneWidget);
      expect(find.text('shared.bin'), findsOneWidget);
      expect(find.text('5 B'), findsOneWidget);
      await tester.tap(find.text('Send 1 file(s)'));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 300));
      expect(result, isTrue);
    } finally {
      await tester.runAsync(() => dir.delete(recursive: true));
    }
  });

  test('Android package extensions are recognized case-insensitively', () {
    for (final name in ['app.apk', 'APP.APKS', 'bundle.XapK']) {
      expect(isAndroidPackage(name), isTrue, reason: name);
    }
    for (final name in ['app.apk.txt', 'photo.jpg', 'archive.zip']) {
      expect(isAndroidPackage(name), isFalse, reason: name);
    }
  });

  testWidgets('an APK requires the extra explicit warning before it can be opened', (tester) async {
    final file = ReceivedFile('/tmp/app.apk', 'Laptop', DateTime.now());
    bool? result;
    await prompt(tester, (context) => confirmOpenReceivedFile(context, file), (value) => result = value);
    expect(find.text('Open an Android app package?'), findsOneWidget);
    expect(find.textContaining('can install apps'), findsOneWidget);
    await tester.tap(find.text('Cancel'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 300));
    expect(result, isFalse);
  });
}
