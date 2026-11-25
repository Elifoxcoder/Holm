using System.Net;
using System.Net.Http;
using System.Text;
using WinNotch.Updates;

namespace WinNotch.Tests;

/// <summary>The desktop updater asks HolyNotch's own update server first and GitHub only when that fails.</summary>
public class UpdateServiceTests
{
    const string Sha = "a3f1c2d4e5b60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";

    sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Asked { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(answer(request));
        }
    }

    static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    static string GitHubRelease(string tag) => $$"""
        {"tag_name":"{{tag}}","draft":false,"prerelease":false,"assets":[{"name":"HolyNotch-{{tag.TrimStart('v')}}-win-x64.zip",
         "state":"uploaded","size":1234,"digest":"sha256:{{Sha}}",
         "browser_download_url":"https://github.com/Elifoxcoder/Holm/releases/download/{{tag}}/HolyNotch-{{tag.TrimStart('v')}}-win-x64.zip"}]}
        """;

    [Fact]
    public async Task A_newer_version_on_the_own_server_downloads_from_there_and_GitHub_is_not_asked()
    {
        var handler = new FakeHandler(_ => Json($$"""{"version":"2.4.1","file":"HolyNotch-2.4.1-win-x64.zip","size":1234,"sha256":"{{Sha}}"}"""));
        using var updates = new UpdateService(new HttpClient(handler));

        var release = await updates.CheckAsync(new Version(2, 4, 0, 0));

        Assert.NotNull(release);
        Assert.Equal(new Version(2, 4, 1), release!.Version);
        Assert.Equal(UpdateService.ServerBase + "HolyNotch-2.4.1-win-x64.zip", release.DownloadUrl.AbsoluteUri);
        Assert.Equal(Sha, release.Sha256);
        Assert.Equal(new[] { UpdateService.ServerBase + "latest.json" }, handler.Asked);
    }

    [Fact]
    public async Task The_same_version_on_the_own_server_means_no_update_without_asking_GitHub()
    {
        var handler = new FakeHandler(_ => Json($$"""{"version":"2.4.1","file":"HolyNotch-2.4.1-win-x64.zip","size":1234,"sha256":"{{Sha}}"}"""));
        using var updates = new UpdateService(new HttpClient(handler));

        Assert.Null(await updates.CheckAsync(new Version(2, 4, 1, 0)));
        Assert.Single(handler.Asked);
    }

    [Theory]
    [InlineData("""{"version":"2.4.1","file":"../evil.zip","size":1234,"sha256":"SHA"}""")]
    [InlineData("""{"version":"2.4.1","file":"HolyNotch-2.4.1-win-x64.zip","size":1234,"sha256":"nope"}""")]
    [InlineData("""{"version":"2.4.1","file":"HolyNotch-2.4.1-win-x64.zip","size":999999999999,"sha256":"SHA"}""")]
    [InlineData("""not json""")]
    public async Task A_broken_answer_from_the_own_server_falls_back_to_GitHub(string answer)
    {
        var handler = new FakeHandler(r => r.RequestUri!.Host == "api.github.com"
            ? Json(GitHubRelease("v2.4.1"))
            : Json(answer.Replace("SHA", Sha)));
        using var updates = new UpdateService(new HttpClient(handler));

        var release = await updates.CheckAsync(new Version(2, 4, 0, 0));

        Assert.NotNull(release);
        Assert.StartsWith("https://github.com/Elifoxcoder/Holm/releases/download/v2.4.1/", release!.DownloadUrl.AbsoluteUri);
        Assert.Equal(2, handler.Asked.Count);
    }

    /// <summary>The real update server: what it offers downloads, matches its checksum and is a HolyNotch exe.</summary>
    [Fact, Trait("Category", "Online")]
    public async Task The_live_update_server_offers_a_release_the_updater_accepts()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var updates = new UpdateService();
        var release = await updates.CheckServerAsync(new Version(0, 0, 1, 0), CancellationToken.None);
        Assert.NotNull(release);
        string staging = Path.Combine(Path.GetTempPath(), "holynotch-update-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            string exe = await updates.DownloadAsync(release!, staging);
            Assert.True(File.Exists(exe));
        }
        finally { try { Directory.Delete(staging, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task An_unreachable_own_server_falls_back_to_GitHub()
    {
        var handler = new FakeHandler(r => r.RequestUri!.Host == "api.github.com"
            ? Json(GitHubRelease("v2.4.2"))
            : new HttpResponseMessage(HttpStatusCode.BadGateway));
        using var updates = new UpdateService(new HttpClient(handler));

        var release = await updates.CheckAsync(new Version(2, 4, 1, 0));

        Assert.Equal(new Version(2, 4, 2), release!.Version);
    }
}
