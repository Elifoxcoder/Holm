#if HOLM_WINDOWS_TESTS
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using Holm.Platform.Windows;

namespace WinNotch.Tests;

public sealed class WindowsSecurityTests
{
    const string Url = "https://github.com/cjpais/Handy/releases/download/v1.2.3/Handy_1.2.3_x64-setup.exe";
    const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Handy_release_requires_a_trusted_url_and_valid_digest_together()
    {
        string json = $$"""{"assets":[{"name":"Handy_1.2.3_x64-setup.exe","browser_download_url":"{{Url}}","digest":"{{Digest}}"}]}""";
        var candidate = HandyBridge.PickInstallerRelease(json);
        Assert.NotNull(candidate);
        var release = candidate.Value;
        Assert.Equal(Url, release.Url);
        Assert.Equal(Digest[7..], release.Sha256);

        string noDigest = $$"""{"assets":[{"name":"Handy_1.2.3_x64-setup.exe","browser_download_url":"{{Url}}"}]}""";
        Assert.Null(HandyBridge.PickInstallerRelease(noDigest));
        string badDigest = $$"""{"assets":[{"name":"Handy_1.2.3_x64-setup.exe","browser_download_url":"{{Url}}","digest":"sha256:abc"}]}""";
        Assert.Null(HandyBridge.PickInstallerRelease(badDigest));
    }

    [Theory]
    [InlineData("http://github.com/cjpais/Handy/releases/download/v1/Handy_x64-setup.exe")]
    [InlineData("https://github.com/another/Handy/releases/download/v1/Handy_x64-setup.exe")]
    [InlineData("https://github.com.evil.example/cjpais/Handy/releases/download/v1/Handy_x64-setup.exe")]
    [InlineData("https://github.com/cjpais/Handy/releases/download/v1/Handy_x64-setup.txt")]
    public void Handy_installer_url_must_be_an_https_download_from_handys_own_release(string url)
        => Assert.Null(HandyBridge.PickInstaller($$"""{"assets":[{"name":"Handy_x64-setup.exe","browser_download_url":"{{url}}"}]}"""));

    [Fact]
    public async Task Handy_download_refuses_a_missing_digest_before_making_a_request()
    {
        var handler = new CountingHandler();
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => HandyBridge.DownloadInstallerAsync(http, Url, null!, CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void Explorer_is_resolved_from_the_windows_directory()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Equal(Path.Combine(windows, "explorer.exe"), WindowsShellPlatform.ResolveExplorer(windows));
    }

    [Fact]
    public void Windows_backend_defaults_dll_search_to_system32()
    {
        var attribute = typeof(WindowsShellPlatform).Assembly.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>();
        Assert.NotNull(attribute);
        Assert.Equal(DllImportSearchPath.System32, attribute!.Paths);
    }

    [Fact]
    public void Authenticode_reports_an_unsigned_file()
    {
        string path = Path.Combine(Path.GetTempPath(), "holm-signature-test-" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            File.Copy(typeof(WindowsSecurityTests).Assembly.Location, path);
            Assert.Equal(HandyBridge.HandySignature.Unsigned, HandyBridge.VerifyAuthenticode(path));
        }
        finally { File.Delete(path); }
    }

    sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
#endif
