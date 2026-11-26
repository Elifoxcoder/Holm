import 'dart:async';
import 'dart:io';

import 'package:file_picker/file_picker.dart';
import 'package:flutter/services.dart';
import 'package:material_ui/material_ui.dart';
import 'package:mobile_scanner/mobile_scanner.dart';

import '../app_state.dart';
import '../net/totp.dart';
import 'theme.dart';
import 'widgets.dart';

/// TOTP Authenticator page (replaces Notes on mobile): stacked compact cards with title + larger code,
/// tap to copy, + button to scan QR (camera) or paste code/URI or import from Google Authenticator / other apps.
class TotpPage extends StatefulWidget {
  final AppState state;
  const TotpPage(this.state, {super.key});

  @override
  State<TotpPage> createState() => _TotpPageState();
}

class _TotpPageState extends State<TotpPage> {
  Timer? _tick;
  String? _copiedId;
  int _copiedUntil = 0;

  AppState get s => widget.state;

  @override
  void initState() {
    super.initState();
    _tick = Timer.periodic(const Duration(seconds: 1), (_) {
      if (mounted) setState(() {});
    });
  }

  @override
  void dispose() {
    _tick?.cancel();
    super.dispose();
  }

  void _snack(String text) {
    if (!mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(text), duration: const Duration(seconds: 2)));
  }

  void _copy(TotpEntry entry, String code) {
    s.copy(code);
    setState(() {
      _copiedId = entry.id;
      _copiedUntil = DateTime.now().millisecondsSinceEpoch + 1400;
    });
    _snack('Copied ${entry.displayTitle}');
  }

  void _openAddSheet() {
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      builder: (ctx) => Padding(
        padding: const EdgeInsets.fromLTRB(20, 4, 20, 28),
        child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [
          const Text('Add authenticator code', style: TextStyle(fontSize: 22, fontWeight: FontWeight.w600)),
          const SizedBox(height: 6),
          const Text('Synced end-to-end with HolyNotch on your PC.', style: TextStyle(color: HolmColors.dim, fontSize: 13.5)),
          const SizedBox(height: 16),
          HolmCard(
            margin: const EdgeInsets.only(bottom: 10),
            onTap: () {
              Navigator.pop(ctx);
              _scanQr();
            },
            child: const Row(children: [
              Icon(Icons.qr_code_scanner_rounded, color: HolmColors.accent, size: 26),
              SizedBox(width: 14),
              Expanded(
                child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  Text('Scan QR code', style: TextStyle(fontWeight: FontWeight.w600, fontSize: 15.5)),
                  SizedBox(height: 2),
                  Text('Single 2FA QR or Google Authenticator export QR', style: TextStyle(color: HolmColors.dim, fontSize: 13)),
                ]),
              ),
              Icon(Icons.chevron_right_rounded, color: HolmColors.faint),
            ]),
          ),
          HolmCard(
            margin: const EdgeInsets.only(bottom: 10),
            onTap: () {
              Navigator.pop(ctx);
              _showManualSheet();
            },
            child: const Row(children: [
              Icon(Icons.keyboard_rounded, color: HolmColors.accent, size: 26),
              SizedBox(width: 14),
              Expanded(
                child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  Text('Paste code or URI', style: TextStyle(fontWeight: FontWeight.w600, fontSize: 15.5)),
                  SizedBox(height: 2),
                  Text('Enter a setup key, otpauth:// or Google Auth URI', style: TextStyle(color: HolmColors.dim, fontSize: 13)),
                ]),
              ),
              Icon(Icons.chevron_right_rounded, color: HolmColors.faint),
            ]),
          ),
          HolmCard(
            margin: EdgeInsets.zero,
            onTap: () {
              Navigator.pop(ctx);
              _importFile();
            },
            child: const Row(children: [
              Icon(Icons.file_upload_outlined, color: HolmColors.accent, size: 26),
              SizedBox(width: 14),
              Expanded(
                child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  Text('Import backup file', style: TextStyle(fontWeight: FontWeight.w600, fontSize: 15.5)),
                  SizedBox(height: 2),
                  Text('Google Authenticator, Aegis, 2FAS, or URI list', style: TextStyle(color: HolmColors.dim, fontSize: 13)),
                ]),
              ),
              Icon(Icons.chevron_right_rounded, color: HolmColors.faint),
            ]),
          ),
        ]),
      ),
    );
  }

  Future<void> _scanQr() async {
    final result = await Navigator.of(context).push<String>(
      MaterialPageRoute(builder: (_) => const _QrScannerScreen()),
    );
    if (result == null || result.trim().isEmpty || !mounted) return;
    _importRaw(result);
  }

  Future<void> _importFile() async {
    final picked = await FilePicker.pickFiles(type: FileType.any);
    final path = picked.firstOrNull?.path;
    if (path == null || !mounted) return;
    try {
      final text = await File(path).readAsString();
      _importRaw(text);
    } catch (_) {
      _snack("Couldn't read that file");
    }
  }

  Future<void> _showManualSheet() async {
    final titleCtrl = TextEditingController();
    final secretCtrl = TextEditingController();
    final clip = (await Clipboard.getData(Clipboard.kTextPlain))?.text?.trim();
    if (clip != null && (clip.toLowerCase().startsWith('otpauth') || TotpCode.normalizeSecret(clip).length >= 16)) {
      secretCtrl.text = clip;
    }
    if (!mounted) return;
    await showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      builder: (ctx) => Padding(
        padding: EdgeInsets.fromLTRB(20, 4, 20, MediaQuery.of(ctx).viewInsets.bottom + 28),
        child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [
          const Text('Enter setup key or URI', style: TextStyle(fontSize: 22, fontWeight: FontWeight.w600)),
          const SizedBox(height: 14),
          TextField(
            controller: titleCtrl,
            textCapitalization: TextCapitalization.words,
            decoration: const InputDecoration(hintText: 'Title (e.g. GitHub, Google)'),
          ),
          const SizedBox(height: 10),
          TextField(
            controller: secretCtrl,
            minLines: 1,
            maxLines: 3,
            decoration: const InputDecoration(hintText: 'Secret key, otpauth:// or otpauth-migration://'),
          ),
          const SizedBox(height: 16),
          Row(children: [
            PillButton('Add', icon: Icons.check_rounded, onPressed: () {
              final raw = secretCtrl.text.trim();
              final title = titleCtrl.text.trim();
              Navigator.pop(ctx);
              _importRaw(raw, fallbackTitle: title.isEmpty ? null : title);
            }),
          ]),
        ]),
      ),
    );
  }

  void _importRaw(String raw, {String? fallbackTitle}) {
    final parsed = TotpParser.parseAny(raw, fallbackTitle: fallbackTitle);
    if (parsed.isEmpty) {
      _snack('No valid TOTP code found');
      return;
    }
    if (parsed.length == 1) {
      if (fallbackTitle != null && fallbackTitle.isNotEmpty) parsed.first.title = fallbackTitle;
      final saved = s.addTotp(parsed.first);
      if (saved != null) _snack('Added ${saved.displayTitle}');
      return;
    }
    final count = s.addTotpMany(parsed);
    _snack(count > 0 ? 'Imported $count codes' : 'All codes are already saved');
  }

  void _confirmDelete(TotpEntry entry) {
    showModalBottomSheet<void>(
      context: context,
      builder: (ctx) => Padding(
        padding: const EdgeInsets.fromLTRB(20, 4, 20, 28),
        child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text('Delete ${entry.displayTitle}?', style: const TextStyle(fontSize: 20, fontWeight: FontWeight.w600)),
          const SizedBox(height: 8),
          const Text('This removes the authenticator code on this phone and synced devices.', style: TextStyle(color: HolmColors.dim)),
          const SizedBox(height: 16),
          Row(children: [
            PillButton('Cancel', primary: false, onPressed: () => Navigator.pop(ctx)),
            const SizedBox(width: 10),
            PillButton('Delete', onPressed: () {
              s.deleteTotp(entry.id);
              Navigator.pop(ctx);
              _snack('Removed ${entry.displayTitle}');
            }),
          ]),
        ]),
      ),
    );
  }

  @override
  Widget build(BuildContext context) => ListenableBuilder(
        listenable: s,
        builder: (context, _) {
          final entries = s.activeTotp;
          final nowMs = DateTime.now().millisecondsSinceEpoch;
          return ListView(
            padding: const EdgeInsets.only(bottom: 24),
            children: [
              PageHeader(
                'Authenticator',
                'Tap any code to copy · synced with your PC',
                trailing: RoundIconButton(Icons.add_rounded, tooltip: 'Add TOTP code', filled: true, onPressed: _openAddSheet),
              ),
              if (entries.isEmpty)
                Column(children: [
                  const EmptyState(
                    Icons.key_rounded,
                    'No codes yet',
                    'Scan a QR code, paste a setup key, or import from Google Authenticator.',
                  ),
                  PillButton('Add code', icon: Icons.add_rounded, onPressed: _openAddSheet),
                ]),
              for (final entry in entries)
                Builder(builder: (context) {
                  final gen = TotpCode.generate(entry);
                  final copied = _copiedId == entry.id && nowMs < _copiedUntil;
                  final low = gen.remaining <= 5;
                  return HolmCard(
                    onTap: () => _copy(entry, gen.code),
                    onLongPress: () => _confirmDelete(entry),
                    padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 14),
                    child: Row(children: [
                      Expanded(
                        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                          Text(entry.displayTitle,
                              maxLines: 1,
                              overflow: TextOverflow.ellipsis,
                              style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w600)),
                          if (entry.account != null && entry.account!.isNotEmpty) ...[
                            const SizedBox(height: 2),
                            Text(entry.account!,
                                maxLines: 1,
                                overflow: TextOverflow.ellipsis,
                                style: const TextStyle(fontSize: 12.5, color: HolmColors.dim)),
                          ],
                        ]),
                      ),
                      const SizedBox(width: 12),
                      Text(
                        copied ? 'Copied' : TotpCode.format(gen.code),
                        style: TextStyle(
                          fontSize: copied ? 18 : 26,
                          fontWeight: FontWeight.w700,
                          fontFeatures: const [FontFeature.tabularFigures()],
                          letterSpacing: copied ? 0 : 1.5,
                          color: copied ? HolmColors.green : (low ? HolmColors.red : HolmColors.ink),
                        ),
                      ),
                      const SizedBox(width: 10),
                      SizedBox(
                        width: 28,
                        child: Text(
                          '${gen.remaining}s',
                          textAlign: TextAlign.right,
                          style: TextStyle(fontSize: 12.5, color: low ? HolmColors.red : HolmColors.faint),
                        ),
                      ),
                    ]),
                  );
                }),
            ],
          );
        },
      );
}

class _QrScannerScreen extends StatefulWidget {
  const _QrScannerScreen();

  @override
  State<_QrScannerScreen> createState() => _QrScannerScreenState();
}

class _QrScannerScreenState extends State<_QrScannerScreen> {
  final _controller = MobileScannerController(formats: const [BarcodeFormat.qrCode]);
  bool _popped = false;

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Scaffold(
        backgroundColor: Colors.black,
        appBar: AppBar(
          backgroundColor: Colors.black,
          title: const Text('Scan QR code'),
          actions: [
            IconButton(
              icon: const Icon(Icons.flash_on_rounded),
              onPressed: () => _controller.toggleTorch(),
            ),
          ],
        ),
        body: Stack(children: [
          MobileScanner(
            controller: _controller,
            onDetect: (capture) {
              if (_popped) return;
              for (final barcode in capture.barcodes) {
                final raw = barcode.rawValue;
                if (raw != null && raw.trim().isNotEmpty) {
                  _popped = true;
                  Navigator.of(context).pop(raw.trim());
                  return;
                }
              }
            },
          ),
          Center(
            child: Container(
              width: 240,
              height: 240,
              decoration: BoxDecoration(
                border: Border.all(color: HolmColors.accent, width: 2.5),
                borderRadius: BorderRadius.circular(20),
              ),
            ),
          ),
          const Positioned(
            left: 24,
            right: 24,
            bottom: 42,
            child: Text(
              'Point at a 2FA QR code or Google Authenticator export QR',
              textAlign: TextAlign.center,
              style: TextStyle(color: Colors.white, fontSize: 14, shadows: [Shadow(blurRadius: 8, color: Colors.black)]),
            ),
          ),
        ]),
      );
}
