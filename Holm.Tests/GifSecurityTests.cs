using WinNotch.Sharing;

namespace WinNotch.Tests;

public class GifSecurityTests
{
    [Theory]
    [InlineData("https://static.klipy.com/ii/abc/file.gif", true)]
    [InlineData("https://klipy.com/gifs/abc.gif", true)]
    [InlineData("https://static.klipy.com.example.com/pixel.gif", false)]
    [InlineData("https://media.giphy.com/media/abc/giphy.gif", true)]
    [InlineData("https://media.tenor.com/abc.gif", true)]
    [InlineData("https://example.com/pixel.gif", false)]
    [InlineData("https://media.giphy.com.example.com/pixel.gif", false)]
    [InlineData("http://media.giphy.com/media/abc/giphy.gif", false)]
    [InlineData("https://user@media.giphy.com/media/abc/giphy.gif", false)]
    public void Only_trusted_CDNs_auto_load(string url, bool expected)
        => Assert.Equal(expected, ChatGif.MayAutoLoad(url));
}
