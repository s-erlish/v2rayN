using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace DpE2E;

internal sealed record PeFacts(Machine Machine, Subsystem Subsystem, bool Managed);

internal sealed record BundleEntry(string Path, long Offset, long Size, long CompressedSize, byte Type);

/// <summary>Сведения из managed-сборки: то, что приложение само о себе прочтёт.</summary>
internal sealed record AssemblyFacts(string? InformationalVersion, int? DebuggableModes);

/// <summary>
/// Разбор исполняемых файлов поставки без их запуска: заголовок PE, оглавление одиночного файла .NET
/// (single-file bundle) и метаданные сборок внутри него.
/// </summary>
internal static class Binary
{
    //  Подпись одиночного файла .NET — SHA-256 от «.net core bundle»; перед ней хост хранит 8 байт
    //  смещения заголовка (dotnet/runtime: bundle_marker.cpp, HostWriter.IsBundle).
    private static readonly byte[] BundleSignature =
    [
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38, 0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18, 0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae,
    ];

    public const int DebuggingModesDisableOptimizations = 256;

    public static PeFacts? ReadPe(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var pe = new PEReader(fs);
            var h = pe.PEHeaders;
            return h.PEHeader is null ? null : new PeFacts(h.CoffHeader.Machine, h.PEHeader.Subsystem, h.CorHeader is not null);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Оглавление одиночного файла или null (не одиночный файл, формат не тот).</summary>
    public static List<BundleEntry>? ReadBundle(byte[] image, out string note)
    {
        var at = image.AsSpan().IndexOf(BundleSignature);
        if (at < 8)
        {
            note = "подписи одиночного файла .NET нет";
            return null;
        }
        var headerOffset = BitConverter.ToInt64(image, at - 8);
        if (headerOffset <= 0 || headerOffset >= image.Length)
        {
            note = $"смещение заголовка {headerOffset} вне файла";
            return null;
        }
        try
        {
            using var br = new BinaryReader(new MemoryStream(image, (int)headerOffset, image.Length - (int)headerOffset, writable: false), Encoding.UTF8);
            var major = br.ReadUInt32();
            var minor = br.ReadUInt32();
            var count = br.ReadInt32();
            var bundleId = br.ReadString();
            if (major >= 2)
            {
                br.ReadInt64();
                br.ReadInt64();
                br.ReadInt64();
                br.ReadInt64();
                br.ReadUInt64();
            }
            var list = new List<BundleEntry>(count);
            for (var i = 0; i < count; i++)
            {
                var offset = br.ReadInt64();
                var size = br.ReadInt64();
                var compressed = major >= 6 ? br.ReadInt64() : 0;
                var type = br.ReadByte();
                var rel = br.ReadString();
                list.Add(new BundleEntry(rel, offset, size, compressed, type));
            }
            note = $"одиночный файл .NET, формат {major}.{minor}, {count} файлов, id {bundleId}";
            return list;
        }
        catch (Exception ex)
        {
            note = "оглавление одиночного файла не читается: " + ex.Message;
            return null;
        }
    }

    public static byte[] Extract(byte[] image, BundleEntry e)
    {
        if (e.CompressedSize == 0)
        {
            return image.AsSpan((int)e.Offset, (int)e.Size).ToArray();
        }
        using var src = new DeflateStream(new MemoryStream(image, (int)e.Offset, (int)e.CompressedSize, writable: false), CompressionMode.Decompress);
        var buf = new byte[e.Size];
        src.ReadExactly(buf);
        return buf;
    }

    /// <summary>
    /// AssemblyInformationalVersion (её читает Utils.GetVersionInfo — это версия, с которой приложение
    /// сравнивает выпуски) и режимы DebuggableAttribute (DisableOptimizations = отладочная сборка).
    /// </summary>
    public static AssemblyFacts? ReadAssembly(byte[] image)
    {
        try
        {
            using var pe = new PEReader(ImmutableArray.Create(image));
            if (!pe.HasMetadata)
            {
                return null;
            }
            var md = pe.GetMetadataReader();
            string? informational = null;
            int? modes = null;
            foreach (var h in md.GetAssemblyDefinition().GetCustomAttributes())
            {
                var ca = md.GetCustomAttribute(h);
                if (ca.Constructor.Kind != HandleKind.MemberReference)
                {
                    continue;
                }
                var parent = md.GetMemberReference((MemberReferenceHandle)ca.Constructor).Parent;
                if (parent.Kind != HandleKind.TypeReference)
                {
                    continue;
                }
                var name = md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Name);
                var blob = md.GetBlobReader(ca.Value);
                if (blob.Length < 2 || blob.ReadUInt16() != 1)
                {
                    continue;
                }
                if (name == "AssemblyInformationalVersionAttribute")
                {
                    informational = blob.ReadSerializedString();
                }
                else if (name == "DebuggableAttribute")
                {
                    //  DebuggableAttribute(DebuggingModes) — int32 и 2 байта числа именованных аргументов;
                    //  старая форма (bool isJITTrackingEnabled, bool isJITOptimizerDisabled) — два байта.
                    if (blob.RemainingBytes >= 4 + 2)
                    {
                        modes = blob.ReadInt32();
                    }
                    else
                    {
                        var tracking = blob.ReadBoolean();
                        var noOptimizer = blob.ReadBoolean();
                        modes = (tracking ? 1 : 0) | (noOptimizer ? DebuggingModesDisableOptimizations : 0);
                    }
                }
            }
            return new AssemblyFacts(informational, modes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Сколько раз строка встречается в байтах — на ЛЮБОМ смещении, не только на чётном.</summary>
    public static int Count(ReadOnlySpan<byte> hay, string needle, Encoding enc)
    {
        var n = enc.GetBytes(needle);
        var count = 0;
        while (true)
        {
            var i = hay.IndexOf(n);
            if (i < 0)
            {
                return count;
            }
            count++;
            hay = hay[(i + n.Length)..];
        }
    }

    public static string Sha256(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    public static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
