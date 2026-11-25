import 'dart:io';

import 'package:material_ui/material_ui.dart';
import 'package:path/path.dart' as p;

import 'theme.dart';
import 'widgets.dart';

/// The share sheet and file picker must show the actual names and sizes before anything leaves the phone, even when
/// the target picker had only one device and did not need to ask where to send.
Future<bool> confirmOutgoingFiles(BuildContext context, List<String> paths, {String title = 'Send these files?'}) async {
  if (paths.isEmpty) return false;
  final files = [for (final path in paths) File(path)];
  final sizes = [for (final file in files) file.lengthSync()];
  final total = sizes.fold<int>(0, (sum, size) => sum + size);
  return await showDialog<bool>(
        context: context,
        builder: (context) => AlertDialog(
          title: Text(title),
          content: SizedBox(
            width: double.maxFinite,
            child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [
              Text('${files.length} file(s) · ${fileSize(total)}', style: const TextStyle(color: HolmColors.dim)),
              const SizedBox(height: 10),
              Flexible(
                child: ListView.builder(
                  shrinkWrap: true,
                  itemCount: files.length,
                  itemBuilder: (context, i) => ListTile(
                    contentPadding: EdgeInsets.zero,
                    leading: const Icon(Icons.insert_drive_file_outlined),
                    title: Text(p.basename(files[i].path)),
                    subtitle: Text(fileSize(sizes[i])),
                  ),
                ),
              ),
            ]),
          ),
          actionsAlignment: MainAxisAlignment.center,
          actions: [
            TextButton(onPressed: () => Navigator.pop(context, false), child: const Text('Cancel')),
            FilledButton(onPressed: () => Navigator.pop(context, true), child: Text('Send ${files.length} file(s)')),
          ],
        ),
      ) ??
      false;
}
