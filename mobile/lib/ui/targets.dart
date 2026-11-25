import 'package:material_ui/material_ui.dart';

import '../app_state.dart';
import 'theme.dart';
import 'widgets.dart';

/// Asks where something goes: a device of the account (anywhere) or a Holm on this Wi-Fi. With only one, takes it.
Future<SendTarget?> pickTarget(BuildContext context, AppState state,
    {String title = 'Send to', bool accountOnly = false}) async {
  List<SendTarget> available() => accountOnly ? state.targets.where((t) => t.authenticated).toList() : state.targets;
  var targets = available();
  if (targets.isEmpty) {
    await state.nearby.refresh();
    state.announceDevice(force: true);
    for (var i = 0; i < 10 && available().isEmpty; i++) {
      await Future<void>.delayed(const Duration(milliseconds: 250));
    }
    targets = available();
  }
  if (!context.mounted) return null;
  if (targets.isEmpty) {
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(content: Text(accountOnly
          ? 'Automatic clipboard sync needs a device signed in to your HolyNotch account. Open it on your PC and sign in here.'
          : state.account == null
              ? 'No device found. Open HolyNotch on your laptop on this Wi-Fi, or sign in to reach it anywhere'
              : 'No device found. Open HolyNotch on your laptop')),
    );
    return null;
  }
  if (targets.length == 1) return targets.first;
  return showModalBottomSheet<SendTarget>(
    context: context,
    builder: (context) => SafeArea(
      child: Column(mainAxisSize: MainAxisSize.min, children: [
        Padding(
          padding: const EdgeInsets.only(bottom: 8),
          child: Text(title, style: const TextStyle(fontSize: 20, fontWeight: FontWeight.w600)),
        ),
        for (final t in targets)
          ListTile(
            leading: Avatar(t.name, size: 36, icon: Icons.laptop_rounded),
            title: Text(t.name),
            subtitle: Text(
              t.authenticated ? 'Verified account device · Anywhere' : 'Unauthenticated nearby · ${t.peer!.address.address}',
              style: const TextStyle(color: HolmColors.dim),
            ),
            onTap: () => Navigator.pop(context, t),
          ),
        const SizedBox(height: 12),
      ]),
    ),
  );
}
