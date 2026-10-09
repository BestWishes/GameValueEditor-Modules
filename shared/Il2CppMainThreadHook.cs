using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Iced.Intel;

namespace GameValueEditor.Modules.Runtime;

internal static class Il2CppMainThreadHook
{
    public static IntPtr AllocateNear(IntPtr handle, ulong entry, int size)
    {
        var aligned = entry & ~0xFFFFUL;
        for (ulong distance = 0x10000; distance < 0x60000000; distance += 0x100000)
            foreach (var candidate in new[] { aligned + distance, aligned > distance ? aligned - distance : 0 })
            {
                if (candidate < 0x10000) continue;
                var result = VirtualAllocEx(handle, (IntPtr)(long)candidate, (nuint)size, 0x3000, 0x40);
                if (result != IntPtr.Zero) return result;
            }
        throw new InvalidOperationException("无法在主线程入口附近分配跳板；未执行修改。");
    }

    public static void WritePatch(Process process, IntPtr handle, ulong address, byte[] bytes)
    {
        // Pause only for the short code replacement, never while querying metadata or
        // waiting for the callback. Refuse patching if a thread is in the replaced block.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var paused = new List<IntPtr>();
            var retry = false;
            try
            {
                foreach (ProcessThread thread in process.Threads)
                {
                    var threadHandle = OpenThread(0x0002 | 0x0008, false, thread.Id);
                    if (threadHandle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法暂停游戏线程以替换入口。");
                    if (SuspendThread(threadHandle) == uint.MaxValue) { CloseHandle(threadHandle); throw new Win32Exception(Marshal.GetLastWin32Error()); }
                    paused.Add(threadHandle);
                    var buffer = Marshal.AllocHGlobal(1248);
                    try
                    {
                        var alignedContext = (IntPtr)((buffer.ToInt64() + 15) & ~15L);
                        Marshal.Copy(new byte[1232], 0, alignedContext, 1232);
                        Marshal.WriteInt32(alignedContext, 0x30, 0x100001);
                        if (!GetThreadContext(threadHandle, alignedContext)) throw new Win32Exception(Marshal.GetLastWin32Error());
                        var rip = unchecked((ulong)Marshal.ReadInt64(alignedContext, 0xF8));
                        if (rip >= address && rip < address + (ulong)bytes.Length) retry = true;
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                if (!retry)
                {
                    if (!WriteProcessMemory(handle, (IntPtr)(long)address, bytes, (nuint)bytes.Length, out var written) || written != (nuint)bytes.Length)
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (!FlushInstructionCache(handle, (IntPtr)(long)address, (nuint)bytes.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    return;
                }
            }
            finally { foreach (var thread in paused) { ResumeThread(thread); CloseHandle(thread); } }
            Thread.Sleep(5);
        }
        throw new InvalidOperationException("游戏线程正在经过操作入口，请稍后重试；未重复执行修改。");
    }
    // RIP-indirect jump: does not destroy RAX/flags before original instructions run.
    public static byte[] Jump(ulong target, int length)
    {
        if (length < 14) throw new ArgumentOutOfRangeException(nameof(length));
        var bytes = Enumerable.Repeat((byte)0x90, length).ToArray();
        new byte[] { 0xFF, 0x25, 0, 0, 0, 0 }.CopyTo(bytes, 0);
        BitConverter.GetBytes(target).CopyTo(bytes, 6);
        return bytes;
    }

    public static int InstructionLength(byte[] source, ulong ip)
    {
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(source));
        decoder.IP = ip;
        while (decoder.IP - ip < 14)
        {
            var instruction = decoder.Decode();
            if (instruction.IsInvalid || instruction.FlowControl is FlowControl.Return or FlowControl.UnconditionalBranch or FlowControl.IndirectBranch)
                throw new InvalidDataException("当前主线程入口太短或已被其他工具修改，未安装操作。");
            if (decoder.IP - ip > 64) throw new InvalidDataException("当前主线程入口指令过长。");
        }
        return checked((int)(decoder.IP - ip));
    }

    public static byte[] Relocate(byte[] original, ulong oldIp, ulong newIp)
    {
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(original));
        decoder.IP = oldIp;
        var instructions = new List<Instruction>();
        while (decoder.IP < oldIp + (ulong)original.Length) instructions.Add(decoder.Decode());
        using var output = new MemoryStream();
        var writer = new StreamCodeWriter(output);
        // Append the continuation before encoding, so widened branches cannot fall into
        // the encoder's literal-pointer pool at the end of the relocated block.
        instructions.Add(Instruction.CreateBranch(Code.Jmp_rel32_64, oldIp + (ulong)original.Length));
        if (!BlockEncoder.TryEncode(64, new InstructionBlock(writer, instructions, newIp), out var error, out _))
            throw new InvalidDataException($"无法搬迁当前主线程指令：{error}");
        return output.ToArray();
    }

    public static byte[] Wrap(byte[] body, ulong status, ulong continuation)
    {
        var bytes = new List<byte>();
        void Emit(params byte[] b) => bytes.AddRange(b);
        void MovRax(ulong value) { Emit(0x48, 0xB8); Emit(BitConverter.GetBytes(value)); }
        // Preserve flags, all volatile integer registers, and XMM0..5. RSP is 16-byte
        // aligned at every callback; 0x30 bytes remain available for shadow/stack args.
        Emit(0x9C, 0x50, 0x51, 0x52, 0x41, 0x50, 0x41, 0x51, 0x41, 0x52, 0x41, 0x53,
            0x48, 0x81, 0xEC, 0xA8, 0, 0, 0);
        for (var register = 0; register < 6; register++)
            Emit(0xF3, 0x0F, 0x7F, (byte)(0x84 | register << 3), 0x24, (byte)(0x40 + register * 16), 0, 0, 0);
        MovRax(status); Emit(0x48, 0x89, 0xC2, 0x33, 0xC0, 0xB9, 0x02, 0, 0, 0,
            0xF0, 0x0F, 0xB1, 0x0A, 0x0F, 0x85);
        var skipOffset = bytes.Count;
        Emit(0, 0, 0, 0);
        Emit(body);
        var restoreOffset = bytes.Count;
        var skipBytes = BitConverter.GetBytes(restoreOffset - skipOffset - 4);
        for (var i = 0; i < 4; i++) bytes[skipOffset + i] = skipBytes[i];
        for (var register = 0; register < 6; register++)
            Emit(0xF3, 0x0F, 0x6F, (byte)(0x84 | register << 3), 0x24, (byte)(0x40 + register * 16), 0, 0, 0);
        Emit(0x48, 0x81, 0xC4, 0xA8, 0, 0, 0,
            0x41, 0x5B, 0x41, 0x5A, 0x41, 0x59, 0x41, 0x58, 0x5A, 0x59, 0x58, 0x9D);
        Emit(Jump(continuation, 14));
        return bytes.ToArray();
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, nuint size, uint type, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenThread(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint SuspendThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetThreadContext(IntPtr thread, IntPtr context);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] bytes, nuint count, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, nuint count);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
