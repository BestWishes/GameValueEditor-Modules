using System.IO;
using System.Text;

namespace GameValueEditor.Modules.Deskrawl;

internal static class DeskrawlApplicationMetadata
{
    internal static string ReadProductName(string dataDirectory)
    {
        var lines = ReadStartupLines(Path.Combine(dataDirectory, "app.info"));
        return lines.Length >= 2 ? lines[1] : string.Empty;
    }

    internal static string ReadBuildGuid(string dataDirectory)
    {
        var values = ReadStartupLines(Path.Combine(dataDirectory, "boot.config"))
            .Where(line => line.StartsWith("build-guid=", StringComparison.Ordinal))
            .Select(line => line["build-guid=".Length..].Trim())
            .ToArray();
        return values.Length == 1 && Guid.TryParseExact(values[0], "N", out var guid) && guid != Guid.Empty
            ? values[0]
            : string.Empty;
    }

    private static string[] ReadStartupLines(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > 16 * 1024) return [];
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
            var buffer = new char[16 * 1024 + 1];
            var length = reader.ReadBlock(buffer, 0, buffer.Length);
            if (length > 16 * 1024) return [];
            using var text = new StringReader(new string(buffer, 0, length));
            var lines = new List<string>();
            while (text.ReadLine() is { } line) lines.Add(line);
            return lines.ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            // These independent optional fields must not discard a valid runtime version.
            return [];
        }
    }
}
