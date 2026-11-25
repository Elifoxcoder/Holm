using System.Security.Cryptography;
using System.Text;
using WinNotch.Sharing;

namespace WinNotch.Tests;

public class SecureChannelTests
{
    [Fact]
    public async Task Messages_arrive_in_both_directions()
    {
        var (sender, receiver) = await Net.ChannelPairAsync();
        await using (sender)
        await using (receiver)
        {
            var ct = Net.Timeout();
            // The receiver reads while the sender writes, as a real peer does: a full frame can be larger than the
            // socket's buffer (macOS keeps loopback buffers small), and writing it waits until it is read.
            var received = Task.Run(async () => (First: await receiver.ReceiveAsync(ct), Second: await receiver.ReceiveAsync(ct)));
            await sender.SendAsync(Encoding.UTF8.GetBytes("hello"), ct);
            await sender.SendAsync(new byte[SecureChannel.MaxPlaintext], ct);
            var (first, second) = await received;
            await receiver.SendAsync(Encoding.UTF8.GetBytes("hi back"), ct);

            Assert.Equal("hello", Encoding.UTF8.GetString(first));
            Assert.Equal(SecureChannel.MaxPlaintext, second.Length);
            Assert.Equal("hi back", Encoding.UTF8.GetString(await sender.ReceiveAsync(ct)));
        }
    }

    [Fact]
    public async Task Frames_are_encrypted_on_the_wire()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var wire = new MemoryStream();
        var secret = Encoding.UTF8.GetBytes("my secret holiday photo bytes");
        await new SecureChannel(wire, key, initiator: true).SendAsync(secret, CancellationToken.None);

        Assert.DoesNotContain("holiday", Encoding.UTF8.GetString(wire.ToArray()));
    }

    [Fact]
    public async Task A_changed_byte_is_detected()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var wire = new MemoryStream();
        await new SecureChannel(wire, key, initiator: true).SendAsync(new byte[100], CancellationToken.None);

        var bytes = wire.ToArray();
        bytes[20] ^= 0x01;
        var receiver = new SecureChannel(new MemoryStream(bytes), key, initiator: false);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => receiver.ReceiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_wrong_key_is_rejected()
    {
        var wire = new MemoryStream();
        await new SecureChannel(wire, RandomNumberGenerator.GetBytes(32), initiator: true).SendAsync(new byte[10], CancellationToken.None);
        wire.Position = 0;
        var receiver = new SecureChannel(wire, RandomNumberGenerator.GetBytes(32), initiator: false);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => receiver.ReceiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_replayed_frame_is_rejected()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var wire = new MemoryStream();
        await new SecureChannel(wire, key, initiator: true).SendAsync(new byte[10], CancellationToken.None);
        var frame = wire.ToArray();

        var replay = new MemoryStream(frame.Concat(frame).ToArray()); // the same frame twice
        var receiver = new SecureChannel(replay, key, initiator: false);
        await receiver.ReceiveAsync(CancellationToken.None);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => receiver.ReceiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_frame_cannot_be_reflected_back_to_its_sender()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var wire = new MemoryStream();
        await new SecureChannel(wire, key, initiator: true).SendAsync(new byte[10], CancellationToken.None);
        wire.Position = 0;
        var sameSide = new SecureChannel(wire, key, initiator: true); // reads with the other direction's nonces

        await Assert.ThrowsAnyAsync<CryptographicException>(() => sameSide.ReceiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_huge_length_prefix_is_refused_before_allocating()
    {
        var bytes = new byte[] { 0x7F, 0xFF, 0xFF, 0xFF };
        var receiver = new SecureChannel(new MemoryStream(bytes), RandomNumberGenerator.GetBytes(32), initiator: false);

        await Assert.ThrowsAsync<InvalidDataException>(() => receiver.ReceiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_sends_are_serialized_and_the_frames_still_decrypt_in_order()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var wire = new MemoryStream();
        var blocked = new BlockingWriteStream(wire);
        await using var sender = new SecureChannel(blocked, key, initiator: true);
        var first = sender.SendAsync(Encoding.UTF8.GetBytes("one"), CancellationToken.None);
        await blocked.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = sender.SendAsync(Encoding.UTF8.GetBytes("two"), CancellationToken.None);
        try
        {
            await Task.Delay(100);
            Assert.Equal(1, blocked.Writes); // the second frame cannot enter the stream while the first is writing
        }
        finally { blocked.Release(); }
        await Task.WhenAll(first, second);

        var receiver = new SecureChannel(new MemoryStream(wire.ToArray()), key, initiator: false);
        Assert.Equal("one", Encoding.UTF8.GetString(await receiver.ReceiveAsync(CancellationToken.None)));
        Assert.Equal("two", Encoding.UTF8.GetString(await receiver.ReceiveAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task A_precancelled_send_does_not_consume_a_nonce()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var wire = new MemoryStream();
        await using var sender = new SecureChannel(wire, key, initiator: true);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendAsync(Encoding.UTF8.GetBytes("never"), cancelled.Token));
        Assert.Empty(wire.ToArray());

        await sender.SendAsync(Encoding.UTF8.GetBytes("after"), CancellationToken.None);
        var receiver = new SecureChannel(new MemoryStream(wire.ToArray()), key, initiator: false);
        Assert.Equal("after", Encoding.UTF8.GetString(await receiver.ReceiveAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task Stream_teardown_drains_an_inflight_publish_before_end()
    {
        var (a, b) = await Net.LoopbackPairAsync();
        var paused = new PauseFirstWriteStream(a);
        var key = RandomNumberGenerator.GetBytes(32);
        var hostChannel = new SecureChannel(paused, key, initiator: true);
        await using var viewerChannel = new SecureChannel(b, key, initiator: false);
        var host = new StreamHost();
        host.Add("viewer", "Lena", hostChannel);
        try
        {
            host.Publish(new byte[] { 1, 2, 3 });
            await paused.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var disposing = host.DisposeAsync().AsTask();
            try
            {
                await Task.Delay(100);
                Assert.False(disposing.IsCompleted); // End must not pass the blocked frame
            }
            finally { paused.Release(); }
            await disposing.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(new byte[] { (byte)'F', 1, 2, 3 }, await viewerChannel.ReceiveAsync(Net.Timeout()));
            Assert.Equal(new byte[] { (byte)'E' }, await viewerChannel.ReceiveAsync(Net.Timeout()));
        }
        finally
        {
            paused.Release();
            await host.DisposeAsync();
        }
    }

    sealed class PauseFirstWriteStream(Stream inner) : Stream
    {
        readonly TaskCompletionSource _firstWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _writes;
        public TaskCompletionSource FirstWrite => _firstWrite;
        public void Release() => _release.TrySetResult();
        public override bool CanRead => false;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _writes) == 1)
            {
                _firstWrite.TrySetResult();
                await _release.Task; // deliberately ignore cancellation: teardown must still not interleave End
            }
            await inner.WriteAsync(buffer, CancellationToken.None);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    sealed class BlockingWriteStream(Stream inner) : Stream
    {
        readonly TaskCompletionSource _firstWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _writes;
        public TaskCompletionSource FirstWrite => _firstWrite;
        public int Writes => Volatile.Read(ref _writes);
        public void Release() => _release.TrySetResult();
        public override bool CanRead => false;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _writes) == 1)
            {
                _firstWrite.TrySetResult();
                await _release.Task.WaitAsync(ct);
            }
            await inner.WriteAsync(buffer, ct);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
