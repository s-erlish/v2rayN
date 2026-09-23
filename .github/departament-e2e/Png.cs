using System.Buffers.Binary;
using System.IO.Compression;

namespace DpE2E;

/// <summary>
/// Снимок экрана в PNG без System.Drawing: на .NET 10 он только для Windows и тянет GDI+, а стенду
/// нужен один формат вывода. Кадр приходит как BGRA сверху вниз (так его отдаёт GetDIBits/DIB-секция).
/// </summary>
internal static class Png
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static void Save(string path, byte[] bgra, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path);
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;  // бит на канал
        ihdr[9] = 2;  // RGB
        WriteChunk(file, "IHDR", ihdr);

        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[1 + width * 3];
            for (var y = 0; y < height; y++)
            {
                row[0] = 0; // фильтр None
                var src = y * width * 4;
                for (var x = 0; x < width; x++)
                {
                    row[1 + x * 3] = bgra[src + x * 4 + 2];
                    row[2 + x * 3] = bgra[src + x * 4 + 1];
                    row[3 + x * 3] = bgra[src + x * 4];
                }
                z.Write(row);
            }
        }
        WriteChunk(file, "IDAT", raw.ToArray());
        WriteChunk(file, "IEND", []);
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = 0xFFFFFFFFu;
        crc = Update(crc, typeBytes);
        crc = Update(crc, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc ^ 0xFFFFFFFFu);
        s.Write(crcBytes);
    }

    private static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }
}
