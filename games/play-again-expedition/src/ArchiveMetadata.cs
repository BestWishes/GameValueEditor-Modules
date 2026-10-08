using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace GameValueEditor.Modules.PlayAgainExpedition;

// Read only the ASAR table and package.json, never unpack/import the game.
internal static class ArchiveMetadata
{
    internal static string ReadVersion(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> prefix = stackalloc byte[8]; stream.ReadExactly(prefix);
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(prefix[4..]);
        if (headerSize < 8 || headerSize > 4 * 1024 * 1024 || headerSize > stream.Length - 8)
            throw new InvalidDataException("游戏包目录格式无效。");
        var header = new byte[(int)headerSize]; stream.ReadExactly(header);
        var jsonLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        if (jsonLength == 0 || jsonLength > headerSize - 8) throw new InvalidDataException("游戏包目录长度无效。");
        using var directory = JsonDocument.Parse(header.AsMemory(8, (int)jsonLength));
        var entry = directory.RootElement.GetProperty("files").GetProperty("package.json");
        if (entry.TryGetProperty("unpacked", out var unpacked) && unpacked.GetBoolean())
            throw new InvalidDataException("游戏声明文件不在原游戏包内。");
        var size = entry.GetProperty("size").GetInt64();
        var offset = long.Parse(entry.GetProperty("offset").GetString()!, CultureInfo.InvariantCulture);
        var position = checked(8L + headerSize + offset);
        if (size <= 0 || size > 65536 || offset < 0 || position > stream.Length - size)
            throw new InvalidDataException("游戏声明文件长度无效。");
        stream.Position = position;
        var bytes = new byte[(int)size]; stream.ReadExactly(bytes);
        using var package = JsonDocument.Parse(bytes);
        var version = package.RootElement.GetProperty("version").GetString();
        if (string.IsNullOrWhiteSpace(version) || version.Length > 128)
            throw new InvalidDataException("游戏声明版本无效。");
        return version;
    }
}
