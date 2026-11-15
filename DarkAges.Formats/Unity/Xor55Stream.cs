namespace DarkAges.Formats.Unity;

public sealed class Xor55Stream : Stream
{
    public const byte Key = 0x55;

    private readonly Stream _inner;
    private readonly bool _leaveOpen;

    public Xor55Stream(Stream inner, bool leaveOpen = false)
    {
        _inner = inner;
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = _inner.Read(buffer, offset, count);
        for (int i = 0; i < n; i++)
        {
            buffer[offset + i] ^= Key;
        }

        return n;
    }

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Flush() => _inner.Flush();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public static byte[] Apply(byte[] data)
    {
        var copy = new byte[data.Length];
        Apply(data, copy);
        return copy;
    }

    public static void Apply(ReadOnlySpan<byte> source, Span<byte> dest)
    {
        int n = Math.Min(source.Length, dest.Length);
        for (int i = 0; i < n; i++)
        {
            dest[i] = (byte)(source[i] ^ Key);
        }
    }

    public static bool LooksLikeUnityFs(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> head = stackalloc byte[8];
            int n = fs.Read(head);
            if (n < 8)
            {
                return false;
            }

            for (int i = 0; i < n; i++)
            {
                head[i] ^= Key;
            }

            return head[0] == (byte)'U'
                && head[1] == (byte)'n'
                && head[2] == (byte)'i'
                && head[3] == (byte)'t'
                && head[4] == (byte)'y'
                && head[5] == (byte)'F'
                && head[6] == (byte)'S'
                && head[7] == 0;
        }
        catch
        {
            return false;
        }
    }
}
