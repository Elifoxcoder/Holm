# Security audit follow-up — 2026-11-25

This document tracks remediation of `HOLM-SEC-001` through `HOLM-SEC-034` in HolyNotch. It covers the desktop app, Android app, the legacy Cloudflare relay in `relay/`, and the active Node server in `server/holynotch-server.mjs`.

## Compatibility boundary

HolyNotch still interoperates with Holm and already-installed HolyNotch versions. The remediation therefore does not change friend/invite/Beam/transit/nearby wire formats, HKDF labels, account REST shapes, the settings folder, DPAPI entropy, or the Android package id and signing lineage.

Important limitations cannot be removed without a coordinated protocol or migration change:

- **LAN discovery and key exchange remain unauthenticated** (`HOLM-SEC-002`). The existing UDP announcement and `WinNotch LAN v1` ephemeral ECDH have no identity binding or SAS. The clients now prevent nearby peers from shadowing authenticated account devices, prevent endpoint replacement from another IP, label nearby targets as unauthenticated, and restrict clipboard auto-send/auto-accept to authenticated account devices. An active LAN attacker can still impersonate a manually selected nearby peer.
- **Inbox reads remain unauthenticated** (`HOLM-SEC-004`). Existing `WNF1.` codes confer read access and installed clients cannot present a new read credential. The relay and server now reject excess followers instead of evicting legitimate ones, throttle every route, cap bodies while streaming, and apply per-IP/topic write quotas. A party that knows a topic can still read ciphertext and, within quotas, cause availability pressure.

The 50-bit `HOLM-XXXXX-XXXXX` invite code and its `Holm friend invite v1` HKDF derivation also remain unchanged (`HOLM-SEC-028`) because changing either would make existing invites and installed clients incompatible. Third-party `ntfy.sh` mirroring is no longer the default, and compatible revocation/expiry controls are used instead.

PhoneShare's browser link also remains cleartext HTTP with a bearer token (`HOLM-SEC-009`). Adding TLS would change the installed phone-browser flow and certificate trust model. The token is random and short-lived, and approvals are now pinned to the originating IP with strict count/byte/lifetime quotas, but a passive observer can still see the token.

Android's existing plaintext `holm.json` format remains in place (`HOLM-SEC-012`) to avoid a silent key/history migration. Cloud and device-transfer backup are disabled instead; a future version can migrate secrets to Keystore/Keychain with an explicit compatibility path.

Invite expiry still has to trust the inbox server's arrival timestamp for the existing 300-second offline grace (`HOLM-SEC-006`). A malicious server can falsify that timestamp; preventing this completely requires an authenticated server receipt or a protocol change that would reject legitimately queued invites. Replay IDs, authenticated send-time/skew checks, and authenticated freshness for real-time notes are enforced locally.

## Findings

| ID | Status | Change and location |
| --- | --- | --- |
| HOLM-SEC-001 | fixed | All relay/server routes are throttled; request bodies are streamed under a hard cap rather than trusted by `Content-Length`; new-topic storage and cleanup are bounded (`relay/src/index.js`, `server/holynotch-server.mjs`). |
| HOLM-SEC-002 | mitigated | Nearby peers cannot shadow account devices or replace a live endpoint from another IP; mobile clipboard automation requires an authenticated account device; anonymous LAN ECDH remains for compatibility (`NearbyService.cs`, `NotchWindow.Beam.cs`, `mobile/lib/net/nearby.dart`, `mobile/lib/app_state.dart`). |
| HOLM-SEC-003 | fixed | Handy is no longer auto-executed from `~/Downloads`; a missing or invalid installer digest prevents execution (`LinuxDictationPlatform.cs`, `HandyBridge.cs`). |
| HOLM-SEC-004 | mitigated | Excess followers are rejected, never evicted; per-IP/topic quotas protect queued notes; unauthenticated reads remain for installed-client compatibility (`relay/src/index.js`, `server/holynotch-server.mjs`). |
| HOLM-SEC-005 | fixed | Nearby transfers have handshake, initial-offer and idle-frame deadlines and release the incoming slot on failure (`NearbyService.cs`, `FileTransfer.cs`, `mobile/lib/net/nearby.dart`, `mobile/lib/net/transfer.dart`). |
| HOLM-SEC-006 | mitigated | Notes use time-bounded persistent replay tracking and authenticated send-time/freshness checks; invite expiry uses server arrival rather than a backdated sender timestamp (`Friends.cs`, `FriendsService.cs`, `mobile/lib/net/friends.dart`, `mobile/lib/app_state.dart`). |
| HOLM-SEC-007 | fixed | Image dimensions and pixel counts are validated before allocation, with scaled decoding and strict avatar/stream limits (`ImageSecurity.cs`, `MediaService.cs`, `StreamWindows.cs`, `NotchWindow.Beam.cs`). |
| HOLM-SEC-008 | fixed | GIF fetches require HTTPS, validate redirects/resolved addresses, block non-public networks and bound downloads; arbitrary hosts no longer load zero-click (`Gifs.cs`, `NetSecurity.cs`, `ChatPage.cs`, `NotchWindow.Hub.cs`). |
| HOLM-SEC-009 | mitigated | PhoneShare approvals enforce originating-IP, file-count and cumulative-byte quotas and retire exhausted sessions; the cleartext bearer link remains for browser compatibility (`PhoneShare.cs`, `NotchWindow.Phone.cs`). |
| HOLM-SEC-010 | fixed | Runtime clipboard/text/screenshot/queue files use private per-user directories, random names and exclusive `0600` creation (`PrivateStorage.cs`, `FolderPack.cs`, `NotchWindow.Clipboard.cs`, `NotchWindow.BeamText.cs`, `NotchWindow.Screenshots.cs`, `SendLater.cs`). |
| HOLM-SEC-011 | fixed | Friend inbox URLs require HTTPS without userinfo/query/fragment and reject non-public destinations at connection time (`Friends.cs`, `FriendsService.cs`, `mobile/lib/net/friends.dart`). |
| HOLM-SEC-012 | mitigated | Android cloud/device-transfer backup is disabled and sensitive files are excluded; existing plaintext `holm.json` remains for format/migration compatibility (`AndroidManifest.xml`, backup rules). |
| HOLM-SEC-013 | fixed | Linux notification monitoring uses binary D-Bus framing instead of parsing spoofable `dbus-monitor` text (`LinuxNotificationsPlatform.cs`, `Bus.cs`). |
| HOLM-SEC-014 | fixed | Android share intents reject `file://` and private-provider URIs at both entry points and require explicit target/file confirmation (`MainActivity.kt`, `NotchBridge.kt`, `mobile/lib/main.dart`). |
| HOLM-SEC-015 | fixed | Workflow permissions are read-only by default, publish rights are job-scoped, actions are SHA-pinned, Python dependencies are hash-pinned and inputs are passed through validated environment variables (`.github/workflows/`). |
| HOLM-SEC-016 | fixed | Android media artwork accepts only HTTPS/content URIs, caps bytes and downsamples during decode (`NotchListener.kt`). |
| HOLM-SEC-017 | fixed | Admin keys are compared in constant time, attempts/routes are throttled, `/admin` sends security headers and errors are generic (`relay/src/security.js`, `relay/src/stats.js`, `relay/src/index.js`, `server/holynotch-server.mjs`). |
| HOLM-SEC-018 | fixed | Linux WAV playback uses random exclusive `0600` files in a private per-user directory and cleans them up (`LinuxSoundPlatform.cs`). |
| HOLM-SEC-019 | fixed | Desktop and mobile offer totals use overflow-safe subtraction checks before addition (`FileTransfer.cs`, `mobile/lib/net/transfer.dart`). |
| HOLM-SEC-020 | fixed | Linux notification icon lookup rejects traversal, bounds/clears its cache and reads only bounded regular non-symlink PNGs (`LinuxNotificationsPlatform.cs`). |
| HOLM-SEC-021 | fixed | Linux capture rejects empty cursors, clamps geometry and traps Xlib errors around `XGetImage` (`LinuxScreenGrabber.cs`). |
| HOLM-SEC-022 | fixed | Unix identity keys are initialized under a lock with atomic replacement, ownership/mode checks and a non-`/tmp` fallback (`IIdentityPlatform.cs`, `IPlatformServices.cs`). |
| HOLM-SEC-023 | fixed | Android release builds no longer fall back to the debug keystore; release credentials are explicit and publishing rejects unsigned/debug-signed APKs (`mobile/android/app/build.gradle.kts`, `mobile/tool/`). |
| HOLM-SEC-024 | fixed | Windows P/Invokes search `System32`, Core imports are restricted likewise and Explorer is launched from the Windows directory (`Holm.Platform.Windows/AssemblyInfo.cs`, `NotificationBadges.cs`, `UsbDrives.cs`, `WindowsShellPlatform.cs`). |
| HOLM-SEC-025 | fixed | Received executable/script/shortcut types cannot be opened from completion actions; exact filenames are shown and text fallback saves as `.txt` (`FileTransfer.cs`, `NotchWindow.Beam.cs`, `NotchWindow.Phone.cs`, `NotchWindow.BeamText.cs`, `NotchWindow.Downloads.cs`). |
| HOLM-SEC-026 | fixed | Linux artwork rejects cleartext/non-public destinations and special files, bounds `data:`/file/network reads and cannot wedge refresh on a FIFO (`LinuxMediaPlatform.cs`). |
| HOLM-SEC-027 | fixed | Locked Android devices redact sensitive notifications/calendar data and require unlock for sensitive notch actions (`NotchService.kt`, `NotchListener.kt`, `mobile/lib/notch/`). |
| HOLM-SEC-028 | mitigated | Invite mirroring no longer defaults to `ntfy.sh` and compatible revocation/expiry controls were added; the 50-bit code and v1 HKDF remain unchanged (`FriendsService.cs`, `Friends.cs`, `mobile/lib/net/friends.dart`). |
| HOLM-SEC-029 | fixed | PhoneShare caps pre-token connections per IP, shortens header deadlines and applies per-write timeouts (`PhoneShare.cs`). |
| HOLM-SEC-030 | fixed | Album art and WinRT cover/logo streams have byte and pre-decode dimension/pixel limits with scaled decoding (`ImageSecurity.cs`, `BoundedIo.cs`, `MediaService.cs`, `WindowsMediaPlatform.cs`, `WindowsNotificationsPlatform.cs`). |
| HOLM-SEC-031 | fixed | Secure-channel writes are serialized and stream teardown prevents concurrent end/publish frames (`SecureChannel.cs`, `ScreenStream.cs`). |
| HOLM-SEC-032 | fixed | Friend keys require the P-256 curve OID and malformed key material is rejected without faulting inbox/UI loops (`Friends.cs`, `FriendsService.cs`, `AddFriendPage.cs`). |
| HOLM-SEC-033 | fixed | Android labels unauthenticated offers and requires an extra warning/confirmation before opening APK-family files (`mobile/lib/net/transfer.dart`, `mobile/lib/main.dart`, `mobile/lib/notch/notch_view.dart`, `mobile/lib/ui/devices_page.dart`). |
| HOLM-SEC-034 | fixed | The existing `~/.config/Holm` folder and sensitive files are created/enforced as `0700`/`0600` on Unix (`PrivateStorage.cs`, `Settings.cs`, `Chat.cs`, `AccountService.cs`, `Program.cs`). |

## Verification

Verified on 2026-11-25:

- `dotnet build Holm.slnx -c Release -m:1` — succeeds, 0 warnings, 0 errors.
- `dotnet build Holm.App/Holm.App.csproj -c Release -r linux-x64 -m:1` — succeeds (the only warnings are the two pre-existing Avalonia API deprecations).
- `dotnet test Holm.Tests -c Release -m:1 --filter "Category!=Local&Category!=Online&Category!=Manual"` — 942 of 942 pass.
- `cd server; node --test test/server.test.mjs` — 17 of 17 pass.
- Mobile (copy under `%LOCALAPPDATA%\HolmDev\hb`): `flutter analyze lib test` — no issues; `flutter test` — 65 of 65 pass.

Three test corrections were needed during integration; each stated a behaviour the hardened code deliberately does not have, so the test was aligned with the secure implementation rather than the other way around:

- `SecurityStorageTests.Core_windows_imports_are_restricted_to_system32` looked for `WinSqlite` as a nested type of `NotificationBadges`; the hardened interop type is top-level, so the test now reflects `WinSqlite` directly.
- `LinuxScreenGrabberTests.Capture_bounds_are_clamped_to_the_current_root` expected a capture starting at `-10` to keep width `200`; clipping to the root window correctly shrinks it to `190`.
- `FriendInviteTests.New_notes_have_authenticated_ids_and_old_notes_use_a_stable_sealed_hash` built its padded variant without the sealed payload; it now pads the key field and keeps the payload, which is what `FriendNotes.Open` and `ReplayId` canonicalize.

One implementation bug surfaced by the suite: `PrivateStorage.IsReparsePoint` treated a path that does not exist yet as a link (the `IOException` path was also taken for `FileNotFoundException`), so first-time private writes were refused. It now returns `false` for a missing path while still rejecting dangling symlinks and reparse points.
