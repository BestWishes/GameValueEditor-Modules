using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using GameValueEditor.ModuleSdk;

namespace GameValueEditor.Modules.PlayAgainExpedition;

internal static class OriginalGameSession
{
    internal const string InterfaceHelp = "从 Steam 或原入口正常启动游戏，进入远征存档后连接并刷新即可。无需配置启动参数或重开游戏。模块不修改游戏文件；材料在游戏空闲且奖励结算完成后可修改，概率规则仅本次游戏会话有效。";

    internal static bool IsGameProcessName(string name) =>
        name.Equals("PlayAgainExpedition", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("ZsebExpedition", StringComparison.OrdinalIgnoreCase);

    internal static GameSession Resolve(GameProcessContext context, bool requireInterface)
    {
        using var selected = Process.GetProcessById(context.ProcessId);
        if (selected.StartTime.ToUniversalTime().Ticks != context.StartTimeUtc.ToUniversalTime().Ticks ||
            !string.Equals(selected.MainModule?.FileName, context.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("游戏启动实例已改变，请重新连接。");
        if (!IsGameProcessName(selected.ProcessName)) throw new InvalidOperationException("选中的进程不是再刷一把：远征。");
        var parents = Parents();
        var eligible = new List<GameSession>();
        var candidates = Process.GetProcessesByName("ZsebExpedition");
        try
        {
            foreach (var candidate in candidates)
            {
                var ancestor = selected.Id;
                var related = selected.Id == candidate.Id || parents.GetValueOrDefault(candidate.Id) == selected.Id;
                for (var depth = 0; depth < 6 && !related; depth++)
                {
                    ancestor = parents.GetValueOrDefault(ancestor);
                    if (ancestor == 0) break;
                    related = ancestor == candidate.Id;
                }
                if (!related) continue;
                var command = CommandLine(candidate);
                if (Regex.IsMatch(command, @"(?:^|\s)--type=")) continue;
                var executable = candidate.MainModule?.FileName ?? throw new InvalidOperationException("无法验证原游戏路径。");
                var archive = Path.Combine(Path.GetDirectoryName(executable)!, "resources", "app.asar");
                if (!File.Exists(archive)) continue;
                // The launcher is an optional launch source, never a version identity.
                string launcherPath = ""; int launcherId = 0; long launcherStart = 0;
                try
                {
                    using var launcher = Process.GetProcessById(parents.GetValueOrDefault(candidate.Id));
                    if (launcher.ProcessName.Equals("PlayAgainExpedition", StringComparison.OrdinalIgnoreCase))
                    {
                        launcherPath = launcher.MainModule?.FileName ?? "";
                        launcherId = launcher.Id; launcherStart = launcher.StartTime.ToUniversalTime().Ticks;
                    }
                }
                catch (ArgumentException) { } // The optional launcher may already have exited.
                eligible.Add(new(candidate.Id, candidate.StartTime.ToUniversalTime().Ticks, executable, archive,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZsebExpedition"),
                    launcherPath, launcherId, launcherStart));
            }
        }
        finally { foreach (var candidate in candidates) candidate.Dispose(); }
        if (eligible.Count != 1) throw new InvalidOperationException("无法唯一定位选中游戏的原主进程，请连接正在运行的远征游戏。");
        var session = eligible[0];
        if (requireInterface) Assert(session, true);
        return session;
    }

    internal static void Assert(GameSession session, bool requireInterface)
    {
        using var process = Process.GetProcessById(session.ProcessId);
        if (process.StartTime.ToUniversalTime().Ticks != session.StartTicks ||
            !string.Equals(process.MainModule?.FileName, session.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
            !process.ProcessName.Equals("ZsebExpedition", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("游戏实例已改变，请重新连接。");
        if (requireInterface && !new[] { RuntimeBridge.DefaultPort, RuntimeBridge.LegacyPort }.Any(port => RuntimeBridge.Listener(port).IsOwnedBy(session.ProcessId)))
            throw new InvalidOperationException("本地接口尚未接入，请刷新模块。");
    }

    internal static string CommandLine(Process process)
    {
        _ = NtQueryInformationProcess(process.Handle, 60, IntPtr.Zero, 0, out var size);
        if (size < 16 || size > 65536) throw new InvalidOperationException("无法安全读取游戏启动角色。");
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (NtQueryInformationProcess(process.Handle, 60, buffer, size, out _) != 0) throw new InvalidOperationException("无法验证游戏启动角色。");
            var length = (ushort)Marshal.ReadInt16(buffer);
            var text = Marshal.ReadIntPtr(buffer, 8);
            if (length % 2 != 0 || text.ToInt64() < buffer.ToInt64() || text.ToInt64() + length > buffer.ToInt64() + size)
                throw new InvalidOperationException("游戏启动角色数据无效。");
            return Marshal.PtrToStringUni(text, length / 2) ?? string.Empty;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static Dictionary<int, int> Parents()
    {
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) throw new InvalidOperationException("无法验证游戏进程组。");
        try
        {
            var result = new Dictionary<int, int>();
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), ExeFile = string.Empty };
            if (!Process32First(snapshot, ref entry)) throw new InvalidOperationException("无法读取游戏进程组。");
            do { result[(int)entry.ProcessId] = (int)entry.ParentProcessId; } while (Process32Next(snapshot, ref entry));
            return result;
        }
        finally { CloseHandle(snapshot); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr data, int size, out int returnLength);
}
