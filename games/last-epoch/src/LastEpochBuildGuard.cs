using System.Diagnostics;

namespace GameValueEditor.Modules.LastEpoch;

internal static class LastEpochBuildGuard
{
    // Live-instance check, not a historical file-hash whitelist. Each editor
    // resolves its own fields and methods from this instance's IL2CPP metadata.
    internal static void EnsureCurrentProcess(int processId, DateTime? expectedStartTimeUtc = null)
    {
        using var process = Process.GetProcessById(processId);
        if (process.HasExited || !process.ProcessName.Equals("Last Epoch", StringComparison.OrdinalIgnoreCase) ||
            expectedStartTimeUtc is { } expected && process.StartTime.ToUniversalTime() != expected.ToUniversalTime())
            throw new InvalidOperationException("Last Epoch 进程已退出或重启，请重新连接游戏。");
        if (!process.Modules.Cast<ProcessModule>().Any(module =>
                module.ModuleName.Equals("GameAssembly.dll", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("游戏还在启动，请稍后连接。");
    }
}
