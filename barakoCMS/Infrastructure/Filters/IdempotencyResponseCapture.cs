namespace barakoCMS.Infrastructure.Filters;

/// <summary>
/// Passes every write through to the real response body and keeps a copy of up to <c>limit</c>
/// bytes, so the response can be replayed to a retry of the same key.
/// </summary>
/// <remarks>
/// A tee rather than a buffer: the client is answered exactly as it would be without a key, and a
/// response over the limit costs no more memory than the limit.
/// </remarks>
internal sealed class IdempotencyResponseCapture(Stream inner, int limit) : Stream
{
    private readonly MemoryStream _copy = new();

    public Stream Inner => inner;

    public bool Overflowed { get; private set; }

    public byte[] Captured => _copy.ToArray();

    private void Keep(ReadOnlySpan<byte> data)
    {
        if (Overflowed) return;
        if (_copy.Length + data.Length > limit)
        {
            Overflowed = true;
            _copy.SetLength(0);
            return;
        }
        _copy.Write(data);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        Keep(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        Keep(buffer);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
        Keep(buffer.AsSpan(offset, count));
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken);
        Keep(buffer.Span);
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
