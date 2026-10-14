namespace DarkAges.Formats.Drawing;

public static class EpfWriter
{
    public static byte[] Write(int width, int height, IReadOnlyList<EpfFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)frames.Count);
        writer.Write((ushort)width);
        writer.Write((ushort)height);
        writer.Write((ushort)0);
        writer.Write(0);

        var starts = new int[frames.Count];
        var ends = new int[frames.Count];
        int cursor = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            EpfFrame frame = frames[i];
            int need = Math.Max(0, frame.Width * frame.Height);
            byte[] data = frame.Data ?? [];
            starts[i] = cursor;
            int take = Math.Min(need, data.Length);
            if (take > 0)
            {
                writer.Write(data, 0, take);
            }

            if (take < need)
            {
                writer.Write(new byte[need - take]);
            }

            cursor += need;
            ends[i] = cursor;
        }

        int toc = cursor;
        stream.Position = 8;
        writer.Write(toc);
        stream.Seek(0, SeekOrigin.End);

        for (int i = 0; i < frames.Count; i++)
        {
            EpfFrame frame = frames[i];
            writer.Write((short)frame.Top);
            writer.Write((short)frame.Left);
            writer.Write((short)frame.Bottom);
            writer.Write((short)frame.Right);
            writer.Write(starts[i]);
            writer.Write(ends[i]);
        }

        return stream.ToArray();
    }
}
