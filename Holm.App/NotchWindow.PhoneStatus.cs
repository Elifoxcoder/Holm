using System;
using Avalonia.Input.Platform;
using WinNotch;
using WinNotch.Sharing;
using WinNotch.Notify;

namespace Holm.App;

// The phone in the notch: the phone app (signed in to the same Holm account) tells the PC what happens on it, and the
// notch pops it out like a message of its own: notifications and calls, the song that starts, a timer that runs or
// went off, and when the phone is fully charged. The notes travel end-to-end encrypted through the account's own inbox (FriendNoteKind.Phone).
public sealed partial class NotchWindow
{
    /// <summary>
    /// A login or verification code from a notification on the phone: "Code 482913 · from Google" with Copy. Copied, it
    /// stays out of the clipboard history and the other devices, and leaves the clipboard after a minute.
    /// </summary>
    void OfferPhoneCode(OwnDevice device, string from, string code)
    {
        if (_busy) { ShowPeek("", $"Code {code}", $"From {from} on {device.Name}", null, seconds: 8); ApplyState(animate: true); return; }
        Diag.Log($"Phone: a login code from {from}");
        string spaced = code.Length is 6 or 8 ? code[..(code.Length / 2)] + " " + code[(code.Length / 2)..] : code;
        ShowTask("", ShareBlue, $"Code {spaced}", $"From {from} · {device.Name}", null,
                 ("Copy", () => _ = CopyPhoneCodeAsync(code)), ("Dismiss", HideTask));
        _taskHide.Stop();
        _taskHide.Interval = TimeSpan.FromSeconds(30);
        _taskHide.Start();
        ApplyState(animate: true);
    }

    async System.Threading.Tasks.Task CopyPhoneCodeAsync(string code)
    {
        // Our own doing: the clipboard capture skips it (no history entry, not synced to the account's devices).
        _restoringKey = ClipboardHistory.TextKey(code);
        if (!await TrySetClipboardAsync(code))
        {
            _restoringKey = null;
            ShowTask("", RedBrush, "Couldn't copy the code", "Another app holds the clipboard", null, null, ("OK", HideTask));
            return;
        }
        _copiedCode = code;
        ShowTask("", GreenBrush, "Code copied", "Paste it now · it leaves the clipboard in a minute", null, null, ("OK", HideTask));
        _taskHide.Stop();
        _taskHide.Interval = TimeSpan.FromSeconds(4);
        _taskHide.Start();
        Avalonia.Threading.DispatcherTimer.RunOnce(async () =>
        {
            if (_copiedCode != code || Clipboard is not { } clipboard) return;
            _copiedCode = null;
            try { if (await clipboard.TryGetTextAsync() == code) await clipboard.ClearAsync(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Diag.Log("Clearing the copied code: " + ex.Message); }
        }, TimeSpan.FromSeconds(60));
    }

    /// <summary>The last song the phone told about: a pause and play of the same song doesn't pop out again.</summary>
    string? _phoneSong;

    /// <summary>The login code on offer, cleared from the clipboard a minute after it was copied (if still there).</summary>
    string? _copiedCode;

    void OnPhoneStatus(OwnDevice device, PhoneStatus status)
    {
        if (status.Kind == PhoneStatus.Code)
        {
            // Never in the phone tool's list or any history: shown once, copied on request.
            if (_settings.PhoneCodes && status.On && status.Detail is { } code) OfferPhoneCode(device, status.Title, code);
            return;
        }
        NotePhone(device, status); // the phone tool's page
        string phone = device.Name;
        switch (status.Kind)
        {
            case PhoneStatus.Notification or PhoneStatus.Call when status.On:
                var message = new ChatMessage
                {
                    // The app's name as the phone said it ("WhatsApp"): its logo when Holm knows the app.
                    App = status.Detail is { } app && status.Kind == PhoneStatus.Notification ? FirstPart(app) : phone,
                    Title = status.Kind == PhoneStatus.Call ? $"{status.Title} is calling" : status.Title,
                    Text = status.Kind == PhoneStatus.Call ? $"On {phone}" : RestOf(status.Detail) ?? $"On {phone}",
                    Time = DateTimeOffset.Now,
                };
                if (Silenced(message)) { Diag.Log("Quiet hours: no pop-out from the phone"); return; }
                ShowMessage(message);
                ApplyState(animate: true);
                break;
            case PhoneStatus.Music:
                if (!status.On) { _phoneSong = null; return; }
                if (status.Title == _phoneSong || _info?.IsPlaying == true) return; // music here comes first
                _phoneSong = status.Title;
                if (_settings.MutePopOuts) return;
                ShowPeek(null, status.Title, status.Detail ?? phone, Dim, seconds: 4);
                ApplyState(animate: true);
                break;
            case PhoneStatus.Battery when status.On:
                // Fully charged: time to unplug it.
                if (_settings.MutePopOuts) return;
                ShowPeek(null, $"{phone} is charged", status.Detail ?? "100%", GreenBrush, seconds: 4, battery: 100, charging: true);
                ApplyState(animate: true);
                break;
            case PhoneStatus.Timer when status.On && status.Since == 0:
                if (_settings.MutePopOuts) return;
                ShowMessage(new ChatMessage { App = phone, Title = "Time's up", Text = $"{status.Title} on {phone}", Time = DateTimeOffset.Now });
                ApplyState(animate: true);
                break;
            case PhoneStatus.Timer when status.On:
                if (_settings.MutePopOuts) return;
                var left = DateTimeOffset.FromUnixTimeSeconds(status.Since) - DateTimeOffset.UtcNow;
                if (left <= TimeSpan.Zero) return;
                ShowPeek(null, $"{status.Title} on {phone}", left.TotalHours >= 1 ? left.ToString(@"h\:mm\:ss") : left.ToString(@"m\:ss"), Dim);
                ApplyState(animate: true);
                break;
        }
    }

    /// <summary>The phone sends "App · text" in the detail of a notification.</summary>
    static string FirstPart(string detail) => detail.Split(" · ", 2)[0];

    static string? RestOf(string? detail)
    {
        if (detail == null) return null;
        var parts = detail.Split(" · ", 2);
        return parts.Length == 2 && parts[1].Length > 0 ? parts[1] : null;
    }
}
