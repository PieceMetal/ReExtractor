using System.Buffers.Binary;
using ReeLib;

namespace ReExtractor.Core;

/// <summary>Read-only MDF compatibility for games sharing the .51 suffix.</summary>
public static class MdfService
{
    public static MdfFile Read(Stream stream, string path)
    {
        using var copy = new MemoryStream();
        if (stream.CanSeek) stream.Position = 0;
        stream.CopyTo(copy);
        var bytes = copy.ToArray();
        var handler = new FileHandler(new MemoryStream(bytes), path);
        if (handler.FileVersion == 51 &&
            !ValidHeaders(bytes, 108, 20, 60) && ValidHeaders(bytes, 104, 24, 56))
        {
            // Onimusha .51 inserts a uint after paramCount; the other .51 layout
            // adds a ulong after ukn1. Remove only that uint in a private copy and
            // pack the headers as .50. All absolute payload offsets stay unchanged.
            var count = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(6));
            var normalized = (byte[])bytes.Clone();
            for (var i = 0; i < count; i++)
            {
                bytes.AsSpan(16 + i * 104, 20).CopyTo(normalized.AsSpan(16 + i * 100));
                bytes.AsSpan(16 + i * 104 + 24, 80).CopyTo(normalized.AsSpan(16 + i * 100 + 20));
            }
            handler = new FileHandler(new MemoryStream(normalized), path) { FileVersion = 50 };
        }
        using (handler.Stream)
        {
            var mdf = new MdfFile(handler);
            if (!mdf.Read()) throw new InvalidDataException($"Failed to parse MDF: {path}");
            return mdf;
        }
    }

    private static bool ValidHeaders(byte[] bytes, int stride, int textureCountOffset, int offsetsStart)
    {
        if (bytes.Length < 16 || BinaryPrimitives.ReadInt32LittleEndian(bytes) != MdfFile.Magic) return false;
        var count = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(6));
        var tableEnd = 16L + count * stride;
        if (count <= 0 || tableEnd > bytes.Length) return false;
        for (var i = 0; i < count; i++)
        {
            var start = 16 + i * stride;
            int Int(int relative) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(start + relative));
            long Long(int relative) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(start + relative));
            bool Range(long offset, long size) => size >= 0 && offset >= tableEnd && offset <= bytes.Length - size;
            bool String(long offset) => Range(offset, 2) && offset % 2 == 0;
            var parameters = Int(16);
            var textures = Int(textureCountOffset);
            var buffers = Int(textureCountOffset + 4);
            if (parameters < 0 || textures < 0 || buffers < 0 || buffers != Int(textureCountOffset + 8) ||
                !String(Long(0)) || !String(Long(offsetsStart + 32)) ||
                (parameters > 0 && !Range(Long(offsetsStart), parameters * 24L)) ||
                (textures > 0 && !Range(Long(offsetsStart + 8), textures * 32L)) ||
                (buffers > 0 && !Range(Long(offsetsStart + 16), buffers * 32L)) ||
                !Range(Long(offsetsStart + 24), Int(12))) return false;
        }
        return true;
    }
}
