namespace WinNotch;

/// <summary>
/// HolyNotch's own server (server/ in this repo, on the VPS): the account and its synced sections, the friend inboxes
/// and the transfer relay. The only place that names it; the phone app has the same in mobile/lib/net/server.dart.
/// </summary>
public static class HolyNotchServer
{
    public const string Host = "holynotch.62-83-85-236.sslip.io";
    public const string Url = "https://" + Host;
    public const int TransitPort = 4001;
}
