import 'package:material_ui/material_ui.dart';

import '../app_state.dart';
import 'theme.dart';

bool isAndroidPackage(String name) {
  final lower = name.toLowerCase();
  return lower.endsWith('.apk') || lower.endsWith('.apks') || lower.endsWith('.xapk');
}

/// APKs and app bundles get a second, explicit warning immediately before they are handed to Android's opener.
Future<bool> confirmOpenReceivedFile(BuildContext context, ReceivedFile file) async {
  if (!isAndroidPackage(file.name)) return true;
  return await showDialog<bool>(
        context: context,
        builder: (context) => AlertDialog(
          icon: const Icon(Icons.warning_amber_rounded, color: Colors.orange, size: 36),
          title: const Text('Open an Android app package?'),
          content: Text(
            '${file.name}\n\nThis file came from ${file.from}. APK, APKS and XAPK files can install apps and may be dangerous. '
            'Only continue if you trust the sender. Android will ask you to confirm the installation again.',
            style: const TextStyle(color: HolmColors.dim),
          ),
          actionsAlignment: MainAxisAlignment.center,
          actions: [
            TextButton(onPressed: () => Navigator.pop(context, false), child: const Text('Cancel')),
            FilledButton(onPressed: () => Navigator.pop(context, true), child: const Text('Open anyway')),
          ],
        ),
      ) ??
      false;
}
