using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using WinNotch.Sharing;
using Xunit;

namespace Holm.Tests;

/// <summary>The phone sends to this computer: the page, the script and the uploads, over a real local socket.</summary>
public class PhoneReceiveTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "holm-phone-" + Guid.NewGuid().ToString("N"));
    readonly PhoneShare _share;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    const string Phone = "a1b2c3d4e5f60718", Stranger = "ffffeeeeddddcccc";
    readonly System.Collections.Generic.List<PhoneRequest> _asked = new();
    bool _yes = true;

    public PhoneReceiveTests()
    {
        _share = PhoneShare.StartReceiving(IPAddress.Loopback, _folder);
        _share.Ask = request => { lock (_asked) _asked.Add(request); return Task.FromResult(_yes); };
    }

    public void Dispose()
    {
        _share.Dispose();
        _http.Dispose();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    async Task<HttpStatusCode> Ask(string session = Phone, int count = 1, long size = 1, string name = "photo.jpg")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _share.Url + "ask");
        request.Headers.Add("X-Session", session);
        request.Headers.Add("X-Name", Uri.EscapeDataString(name));
        request.Headers.Add("X-Count", count.ToString());
        request.Headers.Add("X-Size", size.ToString());
        return (await _http.SendAsync(request)).StatusCode;
    }

    Task<HttpResponseMessage> Upload(string name, byte[] body, string? url = null, string session = Phone)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url ?? _share.Url + "u") { Content = new ByteArrayContent(body) };
        request.Headers.Add("X-Name", Uri.EscapeDataString(name));
        request.Headers.Add("X-Session", session);
        return _http.SendAsync(request);
    }

    bool NothingSaved => !Directory.Exists(_folder) || !Directory.EnumerateFiles(_folder).Any();

    [Fact]
    public async Task Nothing_is_saved_before_the_computer_says_yes()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Upload("sneaky.jpg", "x"u8.ToArray())).StatusCode);
        Assert.True(NothingSaved);
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task The_computer_is_asked_once_with_what_comes_and_a_no_keeps_everything_out()
    {
        _yes = false;
        Assert.Equal(HttpStatusCode.Forbidden, await Ask(count: 5, size: 12_000_000, name: "IMG_0001.HEIC"));
        Assert.Equal(HttpStatusCode.Forbidden, await Ask(count: 5, size: 12_000_000, name: "IMG_0001.HEIC")); // no second question
        Assert.Equal(HttpStatusCode.Forbidden, (await Upload("IMG_0001.HEIC", "x"u8.ToArray())).StatusCode);
        var asked = Assert.Single(_asked);
        Assert.Equal(("IMG_0001.HEIC", 5, 12_000_000L), (asked.FirstName, asked.Count, asked.Bytes));
        Assert.True(NothingSaved);
    }

    [Fact]
    public async Task A_yes_is_for_that_phone_only_and_someone_else_with_the_code_is_asked_again()
    {
        Assert.Equal(HttpStatusCode.OK, await Ask(Phone, name: "mine.jpg"));
        Assert.Equal(HttpStatusCode.Forbidden, (await Upload("x.jpg", "x"u8.ToArray(), session: Stranger)).StatusCode);
        _yes = false;
        Assert.Equal(HttpStatusCode.Forbidden, await Ask(Stranger));
        Assert.Equal(2, _asked.Count);
        Assert.Equal(HttpStatusCode.OK, (await Upload("mine.jpg", "x"u8.ToArray())).StatusCode);
        Assert.True(File.Exists(Path.Combine(_folder, "mine.jpg")));
        Assert.Single(Directory.EnumerateFiles(_folder));
    }

    [Theory]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Mobile/15E148 Safari/604.1", "iPhone")]
    [InlineData("Mozilla/5.0 (iPad; CPU OS 17_0 like Mac OS X) AppleWebKit/605.1.15", "iPad")]
    [InlineData("Mozilla/5.0 (Linux; Android 14; SM-S911B) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/25.0 Chrome/121.0 Mobile Safari/537.36", "Android phone (SM-S911B)")]
    [InlineData("Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36", "Android phone")]
    [InlineData("Mozilla/5.0 (Android 14; Mobile; rv:132.0) Gecko/132.0 Firefox/132.0", "Android phone")]
    [InlineData("Mozilla/5.0 (Linux; Android 13; SM-X710) AppleWebKit/537.36 Chrome/120.0 Safari/537.36", "Android tablet (SM-X710)")]
    [InlineData(null, "A phone")]
    public void The_question_says_what_kind_of_device_asks(string? userAgent, string device)
        => Assert.Equal(device, PhoneShare.DeviceName(userAgent));

    [Fact]
    public async Task The_question_names_the_device()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _share.Url + "ask");
        request.Headers.Add("X-Session", Phone);
        request.Headers.Add("X-Name", Uri.EscapeDataString("photo.jpg"));
        request.Headers.Add("X-Count", "1");
        request.Headers.Add("X-Size", "1");
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) Mobile/15E148 Safari/604.1");
        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(request)).StatusCode);
        Assert.Equal("iPhone", Assert.Single(_asked).Device);
    }

    [Fact]
    public async Task Without_an_answer_to_give_nothing_is_accepted()
    {
        _share.Ask = null;
        Assert.Equal(HttpStatusCode.Forbidden, await Ask());
        Assert.Equal(HttpStatusCode.Forbidden, await Ask("short"));
    }

    [Fact]
    public async Task The_page_offers_a_file_picker_and_its_script()
    {
        Assert.True(_share.IsReceiving);
        string page = await _http.GetStringAsync(_share.Url);
        Assert.Contains("type=\"file\" multiple", page);
        Assert.Contains("/app.js", page);
        Assert.Contains("XMLHttpRequest", await _http.GetStringAsync(_share.Url + "app.js"));
    }

    [Fact]
    public async Task Silent_pre_token_clients_from_one_address_are_capped()
    {
        var url = new Uri(_share.Url);
        var clients = new System.Collections.Generic.List<TcpClient>();
        var reads = new System.Collections.Generic.List<Task<int>>();
        try
        {
            for (int i = 0; i < PhoneShare.MaxClientsPerIp * 2; i++)
            {
                var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, url.Port);
                clients.Add(client);
                reads.Add(client.GetStream().ReadAsync(new byte[1]).AsTask()); // no request line: still pre-token
            }
            var closed = new System.Collections.Generic.List<Task<int>>();
            for (int i = 0; i < PhoneShare.MaxClientsPerIp; i++)
            {
                var done = await Task.WhenAny(reads).WaitAsync(TimeSpan.FromSeconds(2));
                reads.Remove(done);
                closed.Add(done);
            }
            Assert.All(closed, read => Assert.Equal(0, read.Result));
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
            try { await Task.WhenAll(reads); } catch { }
        }

        await Task.Delay(100); // let ServeAsync return the permits of the closed clients
        Assert.Contains("Send to your computer", await _http.GetStringAsync(_share.Url));
    }

    [Fact]
    public async Task A_photo_arrives_whole_under_its_own_name_and_never_overwrites()
    {
        var bytes = new byte[700_000];
        new Random(7).NextBytes(bytes);
        string? received = null;
        _share.Received += path => received = path;
        Assert.Equal(HttpStatusCode.OK, await Ask(count: 2, size: bytes.Length + "second"u8.Length, name: "Ferien äöü.jpg"));

        Assert.Equal(HttpStatusCode.OK, (await Upload("Ferien äöü.jpg", bytes)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Upload("Ferien äöü.jpg", "second"u8.ToArray())).StatusCode);

        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_folder, "Ferien äöü.jpg")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(_folder, "Ferien äöü (1).jpg")));
        Assert.Equal(2, _share.ReceivedFiles.Count);
        Assert.Equal(Path.Combine(_folder, "Ferien äöü (1).jpg"), received);
    }

    [Fact]
    public async Task A_name_cannot_leave_the_folder()
    {
        Assert.Equal(HttpStatusCode.OK, await Ask(name: "../../evil.txt"));
        Assert.Equal(HttpStatusCode.OK, (await Upload("../../evil.txt", "x"u8.ToArray())).StatusCode);
        Assert.True(File.Exists(Path.Combine(_folder, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(_folder, "..", "evil.txt")));
    }

    [Fact]
    public async Task Without_the_secret_part_of_the_address_nothing_is_saved()
    {
        var url = new Uri(_share.Url);
        var response = await Upload("x.txt", "x"u8.ToArray(), $"http://{url.Authority}/wrongtoken/u");
        Assert.False(response.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(_share.Url + "u")).StatusCode);
        Assert.False(Directory.Exists(_folder) && Directory.EnumerateFiles(_folder).Any());
    }

    [Fact]
    public async Task A_file_cut_off_halfway_is_not_kept()
    {
        Assert.Equal(HttpStatusCode.OK, await Ask(size: 100_000, name: "half.bin"));
        var url = new Uri(_share.Url);
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, url.Port);
            var stream = client.GetStream();
            string head = $"POST {url.AbsolutePath}u HTTP/1.1\r\nHost: {url.Authority}\r\nX-Name: half.bin\r\nX-Session: {Phone}\r\nContent-Length: 100000\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            await stream.WriteAsync(new byte[5000]);
            await stream.FlushAsync();
            // The file is being written while the bytes come.
            for (int i = 0; i < 50 && NothingSaved; i++) await Task.Delay(50);
            Assert.False(NothingSaved);
        } // the phone went away
        for (int i = 0; i < 50 && Directory.Exists(_folder) && Directory.EnumerateFiles(_folder).Any(); i++) await Task.Delay(100);
        Assert.False(Directory.Exists(_folder) && Directory.EnumerateFiles(_folder).Any());
        Assert.Empty(_share.ReceivedFiles);

        // The failed request returned its reservation, so the same approved session can retry the file.
        Assert.Equal(HttpStatusCode.OK, (await Upload("half.bin", "whole"u8.ToArray())).StatusCode);
        Assert.Equal("whole", File.ReadAllText(Path.Combine(_folder, "half.bin")));
        Assert.Single(_share.ReceivedFiles);
    }

    [Fact]
    public async Task Approved_file_count_and_cumulative_bytes_are_enforced()
    {
        Assert.Equal(HttpStatusCode.OK, await Ask(count: 2, size: 5, name: "first.bin"));
        Assert.Equal(HttpStatusCode.OK, (await Upload("first.bin", new byte[3])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Upload("too-big.bin", new byte[3])).StatusCode); // only 2 bytes left
        Assert.Equal(HttpStatusCode.OK, (await Upload("second.bin", new byte[2])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Upload("third.bin", new byte[1])).StatusCode); // session retired
        Assert.Equal(new[] { "first.bin", "second.bin" }, Directory.EnumerateFiles(_folder).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task The_first_upload_matches_the_name_that_was_approved()
    {
        Assert.Equal(HttpStatusCode.OK, await Ask(count: 2, size: 2, name: "Photo.JPG"));
        Assert.Equal(HttpStatusCode.Forbidden, (await Upload("other.jpg", "x"u8.ToArray())).StatusCode);
        var caseStatus = (await Upload("photo.jpg", "x"u8.ToArray())).StatusCode;
        Assert.Equal(OperatingSystem.IsWindows() ? HttpStatusCode.OK : HttpStatusCode.Forbidden, caseStatus);
        if (caseStatus != HttpStatusCode.OK)
            Assert.Equal(HttpStatusCode.OK, (await Upload("Photo.JPG", "x"u8.ToArray())).StatusCode);

        // Once the first reservation is taken, later files may have different names and are listed on completion.
        Assert.Equal(HttpStatusCode.OK, (await Upload("different.jpg", "y"u8.ToArray())).StatusCode);
        Assert.Equal(2, _share.ReceivedFiles.Count);
    }

    [Fact]
    public async Task Parallel_uploads_cannot_oversubscribe_the_approved_quota()
    {
        Assert.Equal(HttpStatusCode.OK, await Ask(count: 2, size: 4, name: "a.bin"));
        var first = Upload("a.bin", new byte[4]);
        var second = Upload("b.bin", new byte[4]);
        var statuses = (await Task.WhenAll(first, second)).Select(r => r.StatusCode).OrderBy(s => s).ToArray();
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Forbidden }, statuses);
        Assert.Single(Directory.EnumerateFiles(_folder));
    }

    [Fact]
    public async Task A_session_cannot_be_used_from_another_address()
    {
        Assert.Equal(HttpStatusCode.OK, await Ask(name: "stolen.jpg"));
        var url = new Uri(_share.Url);
        using var client = new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = async (endpoint, ct) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
                await socket.ConnectAsync(endpoint.DnsEndPoint, ct);
                return new NetworkStream(socket, ownsSocket: true);
            },
        });
        var upload = new HttpRequestMessage(HttpMethod.Post, _share.Url + "u") { Content = new ByteArrayContent("x"u8.ToArray()) };
        upload.Headers.Add("X-Session", Phone);
        upload.Headers.Add("X-Name", Uri.EscapeDataString("stolen.jpg"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(upload)).StatusCode);

        var ask = new HttpRequestMessage(HttpMethod.Post, _share.Url + "ask");
        ask.Headers.Add("X-Session", Phone);
        ask.Headers.Add("X-Name", Uri.EscapeDataString("stolen.jpg"));
        ask.Headers.Add("X-Count", "1");
        ask.Headers.Add("X-Size", "1");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(ask)).StatusCode);
        Assert.Single(_asked);
        Assert.True(NothingSaved);
    }

    [Fact]
    public void A_link_that_offers_files_takes_none()
    {
        Assert.Equal(PhoneShare.RouteKind.NotFound, PhoneShare.Route("/tok/u", "tok", 0).Kind);
        Assert.Equal(PhoneShare.RouteKind.Upload, PhoneShare.Route("/tok/u", "tok", 0, receiving: true).Kind);
        Assert.Equal(PhoneShare.RouteKind.Script, PhoneShare.Route("/tok/app.js", "tok", 0, receiving: true).Kind);
        Assert.Equal(PhoneShare.RouteKind.Ask, PhoneShare.Route("/tok/ask", "tok", 0, receiving: true).Kind);
    }
}
