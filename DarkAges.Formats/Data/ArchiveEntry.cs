namespace DarkAges.Formats.Data;

public sealed class ArchiveEntry
{
    private readonly Func<Stream> _open;

    public ArchiveEntry(string name, int size, Func<Stream> open, int? frameIndex = null, string? displayName = null)
    {
        Name = name;
        Size = size;
        _open = open;
        FrameIndex = frameIndex;
        DisplayName = displayName ?? name;
    }

    public string Name { get; }
    public string DisplayName { get; }
    public int Size { get; }
    public int? FrameIndex { get; }
    public string Extension => Path.GetExtension(Name);

    public ArchiveEntry WithFrame(int frameIndex, string displayName) =>
        new(Name, Size, _open, frameIndex, displayName);

    public Stream Open() => _open();

    public byte[] ReadAllBytes()
    {
        using Stream stream = Open();
        if (stream is MemoryStream ms && ms.TryGetBuffer(out ArraySegment<byte> segment) && segment.Array is not null)
        {
            var copy = new byte[segment.Count];
            Buffer.BlockCopy(segment.Array, segment.Offset, copy, 0, segment.Count);
            return copy;
        }

        using var copyStream = new MemoryStream();
        stream.CopyTo(copyStream);
        return copyStream.ToArray();
    }
}
