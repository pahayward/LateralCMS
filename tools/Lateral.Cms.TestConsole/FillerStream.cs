namespace Lateral.Cms.TestConsole;

/// <summary>
/// A read-only stream of <c>length</c> spaces (valid JSON whitespace) that exists only as a counter and a loop, so a
/// 10 GB "upload" costs no disk and no memory. <see cref="BytesRead"/> says how much the sender really pulled before
/// the server refused it.
/// </summary>
public sealed class FillerStream(long length) : Stream
{
    private long _remaining = length;
    private long _bytesRead;

    public long BytesRead => Interlocked.Read(ref _bytesRead);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Fill(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer) => Fill(buffer);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        cancellationToken.IsCancellationRequested
            ? ValueTask.FromCanceled<int>(cancellationToken)
            : ValueTask.FromResult(Fill(buffer.Span));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int Fill(Span<byte> buffer)
    {
        var count = (int)Math.Min(buffer.Length, _remaining);
        buffer[..count].Fill((byte)' ');
        _remaining -= count;
        Interlocked.Add(ref _bytesRead, count);
        return count;
    }
}
