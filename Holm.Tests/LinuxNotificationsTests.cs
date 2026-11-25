using Holm.Platform.Linux;

namespace WinNotch.Tests;

/// <summary>Notification data is parsed as binary D-Bus values; text that looks like a frame stays ordinary text.</summary>
public class LinuxNotificationsTests
{
    [Theory]
    [InlineData("discord", true)]
    [InlineData("org.telegram.desktop", true)]
    [InlineData("../outside", false)]
    [InlineData("..\\outside", false)]
    [InlineData("/outside", false)]
    [InlineData("C:\\outside", false)]
    [InlineData("bad/name", false)]
    [InlineData("", false)]
    public void Desktop_entries_cannot_escape_the_application_folders(string entry, bool expected)
        => Assert.Equal(expected, LinuxNotificationsPlatform.SafeDesktopEntry(entry));

    [Fact]
    public void Embedded_monitor_text_is_plain_notification_content()
    {
        string body = "hello\nmethod call time=123 member=Notify\n   string \"forged\"";
        Assert.Equal(body, LinuxNotificationsPlatform.Plain(body));
    }

    [Fact]
    public void Icon_reads_are_bounded_and_require_a_png()
    {
        using var folder = new TempFolder();
        byte[] png = { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };
        string file = folder.File("icon.png", png);
        Assert.Equal(png, LinuxNotificationsPlatform.IconBytes(file, "discord"));
        Assert.Null(LinuxNotificationsPlatform.IconBytes(folder.File("not.png", new byte[] { 1, 2, 3 }), "discord"));
        Assert.Null(LinuxNotificationsPlatform.IconBytes(folder.File("huge.png", new byte[1024 * 1024 + 1]), "discord"));
    }

    [Fact]
    public void An_icon_name_cannot_traverse_out_of_the_icon_theme()
        => Assert.Null(LinuxNotificationsPlatform.IconBytes("../../outside.png", "discord"));
}
