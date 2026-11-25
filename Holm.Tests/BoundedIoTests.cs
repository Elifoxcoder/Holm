using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinNotch;

namespace WinNotch.Tests;

public class BoundedIoTests
{
    [Fact]
    public async Task A_stream_with_no_length_is_capped_while_reading()
    {
        using var source = new UnknownLengthStream(1024);
        await Assert.ThrowsAsync<IOException>(() => BoundedIo.ReadAllBytesAsync(source, 100));
    }

    [Fact]
    public async Task A_declared_oversize_is_rejected_before_reading()
    {
        using var source = new MemoryStream(new byte[101]);
        await Assert.ThrowsAsync<IOException>(() => BoundedIo.ReadAllBytesAsync(source, 100));
    }

    [Fact]
    public async Task A_bounded_stream_is_read_completely()
    {
        using var source = new UnknownLengthStream(100);
        Assert.Equal(100, (await BoundedIo.ReadAllBytesAsync(source, 100)).Length);
    }

    sealed class UnknownLengthStream(int length) : Stream
    {
        readonly byte[] _bytes = new byte[length];
        int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position == _bytes.Length) return 0;
            int n = Math.Min(count, _bytes.Length - _position);
            Array.Clear(buffer, offset, n);
            _position += n;
            return n;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
