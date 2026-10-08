using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameValueEditor.Modules.PlayAgainExpedition;

// Equivalent to Node's Windows _debugProcess. The target runtime publishes this
// callback itself. Never derive an RVA, allocate executable memory or write VM.
internal static class NativeRuntimeActivation
{
    internal static bool Available(GameSession session)
    {
        try { WithHandler(session, false); return true; } catch { return false; }
    }
    internal static void Activate(GameSession session) => WithHandler(session, true);
    private static void WithHandler(GameSession session, bool activate)
    {
        OriginalGameSession.Assert(session, false);
        if (!Environment.Is64BitProcess) throw new InvalidOperationException("直接接入需要 x64 主程序。");
        using var target = Process.GetProcessById(session.ProcessId);
        var module = target.MainModule ?? throw new InvalidOperationException("无法验证游戏运行时。");
        var begin = module.BaseAddress.ToInt64(); var end = checked(begin + module.ModuleMemorySize);
        IntPtr mapping = IntPtr.Zero, view = IntPtr.Zero, process = IntPtr.Zero, thread = IntPtr.Zero;
        try
        {
            mapping = OpenFileMappingW(4, false, "node-debug-handler-" + session.ProcessId);
            if (mapping == IntPtr.Zero) throw new InvalidOperationException("当前游戏运行时没有提供直接接入入口。");
            view = MapViewOfFile(mapping, 4, 0, 0, (UIntPtr)8);
            if (view == IntPtr.Zero) throw new InvalidOperationException("无法读取运行时接入入口。");
            var handler = Marshal.ReadIntPtr(view);
            if (handler.ToInt64() < begin || handler.ToInt64() >= end) throw new InvalidOperationException("运行时接入入口不属于已验证的游戏映像。");
            process = OpenProcess(activate ? 0x043au : 0x0400u, false, (uint)session.ProcessId);
            if (process == IntPtr.Zero) throw new InvalidOperationException("无法取得游戏接入权限；请确认主程序和游戏权限一致。");
            if (VirtualQueryEx(process, handler, out var memory, (UIntPtr)Marshal.SizeOf<MemoryInfo>()).ToUInt64() != (ulong)Marshal.SizeOf<MemoryInfo>() ||
                memory.AllocationBase.ToInt64() != begin || memory.State != 0x1000 || memory.Type != 0x1000000 ||
                (memory.Protect & 0xff) != 0x20 || (memory.Protect & 0x100) != 0)
                throw new InvalidOperationException("运行时接入入口不是已验证的可执行映像页。");
            if (!activate) return;
            OriginalGameSession.Assert(session, false);
            thread = CreateRemoteThread(process, IntPtr.Zero, UIntPtr.Zero, handler, IntPtr.Zero, 0, out _);
            if (thread == IntPtr.Zero) throw new InvalidOperationException("游戏运行时拒绝直接接入，本次没有发送业务请求。");
            if (WaitForSingleObject(thread, 5000) != 0 || !GetExitCodeThread(thread, out var code) || code != 0)
                throw new InvalidOperationException("运行时接入结果不确定，已停止操作，不会自动重试。");
        }
        finally
        {
            if (thread != IntPtr.Zero) CloseHandle(thread);
            if (process != IntPtr.Zero) CloseHandle(process);
            if (view != IntPtr.Zero) UnmapViewOfFile(view);
            if (mapping != IntPtr.Zero) CloseHandle(mapping);
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInfo
    {
        public IntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort Partition;
        public UIntPtr RegionSize;
        public uint State, Protect, Type;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint high, uint low, UIntPtr size);
    [DllImport("kernel32.dll")] private static extern bool UnmapViewOfFile(IntPtr view);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint process);
    [DllImport("kernel32.dll")] private static extern UIntPtr VirtualQueryEx(IntPtr process, IntPtr address, out MemoryInfo information, UIntPtr length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, UIntPtr stack, IntPtr start, IntPtr argument, uint flags, out uint thread);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeThread(IntPtr thread, out uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
