import 'dart:async';

import 'package:material_ui/material_ui.dart';

import '../net/transfer.dart';
import 'theme.dart';
import 'widgets.dart';

/// Shows who an offer claims to be and, for nearby LAN connections, says plainly that the identity is unauthenticated.
Future<bool> confirmOffer(BuildContext context, ShareOffer offer) async {
  BuildContext? dialog;
  final expire = Timer(const Duration(seconds: 58), () {
    final open = dialog;
    if (open != null && open.mounted) Navigator.of(open).pop(false);
  });
  final answer = await showDialog<bool>(
    context: context,
    barrierDismissible: false,
    builder: (context) {
      dialog = context;
      return AlertDialog(
        icon: Avatar(offer.sender, size: 56, icon: Icons.laptop_rounded),
        title: Text(offer.isText ? 'Text from ${offer.sender}' : '${offer.sender} wants to send you'),
        content: SizedBox(
          width: double.maxFinite,
          child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [
            Container(
              width: double.maxFinite,
              padding: const EdgeInsets.all(10),
              decoration: BoxDecoration(
                color: (offer.authenticated ? HolmColors.green : HolmColors.red).withValues(alpha: 0.12),
                borderRadius: BorderRadius.circular(10),
              ),
              child: Text(
                offer.authenticated
                    ? 'Verified HolyNotch account device'
                    : 'Unauthenticated nearby offer. The sender’s identity has not been verified; only accept from someone you trust on this network.',
                style: TextStyle(
                  color: offer.authenticated ? HolmColors.green : HolmColors.red,
                  fontSize: 13,
                  height: 1.3,
                ),
              ),
            ),
            const SizedBox(height: 12),
            if (offer.isText)
              const Text('Copy it to your clipboard?', style: TextStyle(color: HolmColors.dim))
            else ...[
              Text('${offer.files.length} file(s) · ${fileSize(offer.totalBytes)}',
                  style: const TextStyle(color: HolmColors.dim)),
              const SizedBox(height: 6),
              Flexible(
                child: ListView.builder(
                  shrinkWrap: true,
                  itemCount: offer.files.length,
                  itemBuilder: (context, i) => ListTile(
                    contentPadding: EdgeInsets.zero,
                    leading: const Icon(Icons.insert_drive_file_outlined),
                    title: Text(offer.files[i].name),
                    subtitle: Text(fileSize(offer.files[i].size)),
                  ),
                ),
              ),
            ],
          ]),
        ),
        actionsAlignment: MainAxisAlignment.center,
        actions: [
          TextButton(onPressed: () => Navigator.pop(context, false), child: const Text('Decline')),
          FilledButton(onPressed: () => Navigator.pop(context, true), child: Text(offer.isText ? 'Copy' : 'Accept')),
        ],
      );
    },
  );
  expire.cancel();
  return answer ?? false;
}
