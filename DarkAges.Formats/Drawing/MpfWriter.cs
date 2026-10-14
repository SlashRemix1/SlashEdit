namespace DarkAges.Formats.Drawing;

public static class MpfWriter
{
    public static byte[] Write(
        int width,
        int height,
        int paletteNumber,
        IReadOnlyList<MpfFrame> frames,
        int walkFrameIndex = 0,
        int walkFrameCount = 0,
        int stopFrameIndex = 0,
        int stopFrameCount = 1,
        int stopMotionFrameCount = 0,
        int stopMotionProbability = 0,
        int attackFrameIndex = 0,
        int attackFrameCount = 0,
        int attack2FrameIndex = 0,
        int attack2FrameCount = 0,
        int attack3FrameIndex = 0,
        int attack3FrameCount = 0)
    {
        ArgumentNullException.ThrowIfNull(frames);
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        int tableCount = frames.Count + 1;
        using var pixels = new MemoryStream();
        var offsets = new int[frames.Count];
        for (int i = 0; i < frames.Count; i++)
        {
            offsets[i] = (int)pixels.Length;
            byte[] data = frames[i].Data ?? [];
            int need = Math.Max(0, frames[i].Width * frames[i].Height);
            int take = Math.Min(need, data.Length);
            if (take > 0)
            {
                pixels.Write(data, 0, take);
            }

            if (take < need)
            {
                pixels.Write(new byte[need - take]);
            }
        }

        byte[] blob = pixels.ToArray();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)tableCount);
        writer.Write((short)width);
        writer.Write((short)height);
        writer.Write(blob.Length);
        writer.Write((byte)walkFrameIndex);
        writer.Write((byte)walkFrameCount);
        writer.Write((short)-1);
        writer.Write((byte)stopFrameIndex);
        writer.Write((byte)stopFrameCount);
        writer.Write((byte)stopMotionFrameCount);
        writer.Write((byte)stopMotionProbability);
        writer.Write((byte)attackFrameIndex);
        writer.Write((byte)attackFrameCount);
        writer.Write((byte)attack2FrameIndex);
        writer.Write((byte)attack2FrameCount);
        writer.Write((byte)attack3FrameIndex);
        writer.Write((byte)attack3FrameCount);

        for (int i = 0; i < frames.Count; i++)
        {
            MpfFrame frame = frames[i];
            writer.Write((short)frame.Left);
            writer.Write((short)frame.Top);
            writer.Write((short)frame.Right);
            writer.Write((short)frame.Bottom);
            WriteInt16Be(writer, (short)frame.XOffset);
            WriteInt16Be(writer, (short)frame.YOffset);
            writer.Write(offsets[i]);
        }

        writer.Write((short)-1);
        writer.Write((short)-1);
        writer.Write((short)0);
        writer.Write((short)0);
        WriteInt16Be(writer, 0);
        WriteInt16Be(writer, 0);
        writer.Write(paletteNumber);
        writer.Write(blob);
        return stream.ToArray();
    }

    private static void WriteInt16Be(BinaryWriter writer, short value)
    {
        writer.Write((byte)(value >> 8));
        writer.Write((byte)(value & 0xFF));
    }
}
