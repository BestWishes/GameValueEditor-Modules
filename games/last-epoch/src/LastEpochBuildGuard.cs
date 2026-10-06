using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GameValueEditor.ModuleSdk;

namespace GameValueEditor.Modules.LastEpoch;

internal static class LastEpochBuildGuard
{
    internal const string UnverifiedBuildMessage =
        "当前 Last Epoch 构建尚未验证资源、异界及摄像头的固定布局，模块保持停用；请等待适配此构建的模块版本。";
    private static readonly object CacheLock = new();
    private static readonly Dictionary<int, VerifiedProcess> Cache = [];
    private static readonly GameBuildIdentity[] VerifiedBuilds = ReadVerifiedBuilds();

    internal static bool IsVerifiedBuild(GameBuildIdentity identity) => VerifiedBuilds.Any(build =>
        Match(build.ExecutableSha256, identity.ExecutableSha256) &&
        Match(build.GameAssemblySha256, identity.GameAssemblySha256) &&
        Match(build.MetadataSha256, identity.MetadataSha256));

    internal static void EnsureVerifiedProcess(int processId, DateTime? expectedStartTimeUtc = null)
    {
        using var process = Process.GetProcessById(processId);
        var startTime = process.StartTime.ToUniversalTime();
        if (!process.ProcessName.Equals("Last Epoch", StringComparison.OrdinalIgnoreCase) ||
            expectedStartTimeUtc is { } expected && startTime != expected.ToUniversalTime())
            throw new InvalidOperationException("Last Epoch 进程身份已变化，请重新连接游戏。");
        var executable = process.MainModule?.FileName
                         ?? throw new InvalidOperationException("无法验证 Last Epoch 主程序文件。");
        var assembly = process.Modules.Cast<ProcessModule>().FirstOrDefault(module =>
            module.ModuleName.Equals("GameAssembly.dll", StringComparison.OrdinalIgnoreCase))?.FileName
                       ?? throw new InvalidOperationException("Last Epoch 的 GameAssembly.dll 尚未加载。");
        var metadata = Path.Combine(Path.GetDirectoryName(executable)!,
            Path.GetFileNameWithoutExtension(executable) + "_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        var files = new[] { executable, assembly, metadata };
        lock (CacheLock)
        {
            var before = files.Select(ReadStamp).ToArray();
            if (Cache.TryGetValue(processId, out var previous) && previous.StartTimeUtc == startTime &&
                previous.Stamps.SequenceEqual(before)) return;
            Cache.Remove(processId);
            var hashes = files.Select(path =>
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return Convert.ToHexString(SHA256.HashData(stream));
            }).ToArray();
            if (!before.SequenceEqual(files.Select(ReadStamp)) || process.HasExited ||
                !IsVerifiedBuild(new GameBuildIdentity(hashes[0], string.Empty, hashes[1], hashes[2])))
                throw new InvalidOperationException(UnverifiedBuildMessage);
            if (Cache.Count >= 16) Cache.Clear();
            Cache[processId] = new(startTime, before);
        }
    }

    private static bool Match(string expected, string actual) => expected.Length == 64 &&
        !string.IsNullOrWhiteSpace(actual) && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

    private static FileStamp ReadStamp(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new InvalidOperationException("缺少 Last Epoch 构建校验文件，模块保持停用。");
        return new(file.FullName, file.Length, file.LastWriteTimeUtc);
    }

    private static GameBuildIdentity[] ReadVerifiedBuilds()
    {
        using var stream = typeof(LastEpochBuildGuard).Assembly.GetManifestResourceStream("LastEpoch.VerifiedBuilds.json")
                           ?? throw new InvalidOperationException("模块缺少已验证构建清单。");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("compatibleBuilds").EnumerateArray().Select(build =>
            new GameBuildIdentity(build.GetProperty("executableSha256").GetString()!, string.Empty,
                build.GetProperty("gameAssemblySha256").GetString()!, build.GetProperty("metadataSha256").GetString()!))
            .ToArray();
    }

    private sealed record FileStamp(string Path, long Length, DateTime ModifiedUtc);
    private sealed record VerifiedProcess(DateTime StartTimeUtc, FileStamp[] Stamps);
}
