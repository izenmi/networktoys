namespace PingWatcher.Core.Terminal;

/// <summary>
/// Telnet の制御を取り除いて本文だけを見せるストリーム。
///
/// 下位のストリーム（TCP）から読んだバイトを <see cref="TelnetFilter"/> に通し、
/// 生じた返事はその場で下位へ書き戻す。上に乗る
/// <see cref="CiscoSession"/> は Telnet を意識しなくてよくなり、
/// SSH のシェルとまったく同じ扱いで駆動できる。
/// </summary>
public sealed class TelnetStream(Stream inner) : Stream
{
    private readonly TelnetFilter _filter = new();
    private readonly byte[] _raw = new byte[8192];

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        // 制御だけのチャンクだと本文が 0 バイトになる。呼び出し側に
        // 「切れた」と誤解させないよう、本文が出るまで読み続ける
        while (true)
        {
            int size = Math.Min(_raw.Length, buffer.Length);
            int read = await inner.ReadAsync(_raw.AsMemory(0, size), token).ConfigureAwait(false);

            if (read <= 0) return 0;

            int written = _filter.Process(_raw.AsSpan(0, read), buffer.Span, out byte[] reply);

            if (reply.Length > 0)
            {
                await inner.WriteAsync(reply, token).ConfigureAwait(false);
                await inner.FlushAsync(token).ConfigureAwait(false);
            }

            if (written > 0) return written;
        }
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        => inner.WriteAsync(buffer, token);

    public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();

        base.Dispose(disposing);
    }
}
