using System.Text;

namespace DarkAges.Formats.IO;

public static class BinaryReaderExtensions
{
    public static short ReadInt16(this BinaryReader reader, bool bigEndian)
    {
        if (!bigEndian)
        {
            return reader.ReadInt16();
        }

        byte[] buffer = reader.ReadBytes(2);
        if (buffer.Length < 2)
        {
            throw new EndOfStreamException();
        }

        return (short)(buffer[1] | buffer[0] << 8);
    }

    public static BinaryReader Create(Stream stream) =>
        new(stream, Encoding.ASCII, leaveOpen: true);
}
