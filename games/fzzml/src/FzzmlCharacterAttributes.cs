using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using GameValueEditor.ModuleSdk;

namespace GameValueEditor.Modules.Fzzml;

public sealed partial class FzzmlGameAdapter
{
    private const string CharacterEditorId = "game.fzzml.character-attributes";
    private const string CharacterBuildAssembly = "BF156D35DDB79839797517BC95BC07080DAACDCF78D08579B7D0D4C09386D752";
    private const string CharacterBuildMetadata = "AEB09A9D3C8359F54C2DF29F3045C5AE1D9270DC269EC0A8E18C0D359CE4CD29";

    private static readonly AttributeLayout[] CharacterAttributes =
    [
        new("constitution", "根骨", 0x4C, 0x44, 0x68, 0x10),
        new("strength", "力道", 0x50, 0x48, 0x60, 0x14),
        new("spirit", "神识", 0x54, 0x4C, 0x70, 0x18),
        new("agility", "身法", 0x58, 0x50, 0x64, 0x1C),
        new("vitality", "体魄", 0x5C, 0x54, 0x6C, 0x20)
    ];

    public bool SupportsCharacterAttributes(GameProcessContext process)
    {
        try
        {
            var layout = ResolveLayout(process);
            return string.Equals(layout.GameAssemblySha256, CharacterBuildAssembly, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(layout.MetadataSha256, CharacterBuildMetadata, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<AdapterCharacterItem> ReadCharacters(GameProcessContext process)
    {
        if (!SupportsCharacterAttributes(process))
            throw new InvalidOperationException("当前 fzzml 构建尚未支持人物属性编辑；背包物品功能仍可单独使用。");

        lock (WriteGate)
        {
            using var session = new CharacterSession(process.ProcessId, ResolveLayout(process));
            return session.ReadCharacters();
        }
    }

    public AdapterCharacterItem WriteCharacterAttribute(
        GameProcessContext process,
        string characterId,
        string attributeKey,
        int targetValue)
    {
        if (targetValue < 0) throw new InvalidOperationException("人物属性不能小于 0。");
        if (!SupportsCharacterAttributes(process))
            throw new InvalidOperationException("当前 fzzml 构建尚未支持人物属性编辑。");

        var attribute = CharacterAttributes.SingleOrDefault(item =>
            string.Equals(item.Key, attributeKey, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"不支持的人物属性键：{attributeKey}。");

        lock (WriteGate)
        {
            using var session = new CharacterSession(process.ProcessId, ResolveLayout(process));
            return session.WriteCharacterAttribute(characterId, attribute, targetValue);
        }
    }

    public static string BuildCharacterFieldKey(string characterId, string attributeKey) =>
        ModuleFieldKey.Create(CharacterEditorId, characterId, attributeKey);

    private static bool TryParseCharacterFieldKey(string fieldKey, out string characterId, out string attributeKey)
    {
        characterId = string.Empty;
        attributeKey = string.Empty;
        if (!ModuleFieldKey.TryParse(fieldKey, out var editorId, out characterId, out attributeKey)) return false;
        return string.Equals(editorId, CharacterEditorId, StringComparison.Ordinal);
    }

    private sealed class CharacterSession : IDisposable
    {
        private const uint ProcessAccess = 0x0002 | 0x0008 | 0x0010 | 0x0020 | 0x0400;
        private const uint MemCommitReserve = 0x1000 | 0x2000;
        private const uint MemRelease = 0x8000;
        private const uint PageExecuteReadWrite = 0x40;
        private const uint Infinite = 0xFFFFFFFF;
        private const ulong ConfigManagerGetInstance = 0x6B9A00;
        private const ulong PlayerAttributeAggregatorCompute = 0x328300;
        private const ulong PlayerAttributeEventHubRaise = 0x32BD90;
        private const ulong TryGetUnitConfigById = 0x51C230;

        private readonly Process _process;
        private readonly IntPtr _handle;
        private readonly ulong _moduleBase;
        private readonly ulong _saveManager;
        private readonly ulong _stringNew;
        private readonly BuildLayout _layout;

        public CharacterSession(int processId, BuildLayout layout)
        {
            _layout = layout;
            _process = Process.GetProcessById(processId);
            var module = _process.Modules.Cast<ProcessModule>().SingleOrDefault(candidate =>
                string.Equals(candidate.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("目标进程没有加载 GameAssembly.dll。");
            _moduleBase = unchecked((ulong)module.BaseAddress.ToInt64());
            _handle = OpenProcess(ProcessAccess, false, processId);
            if (_handle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法连接 fzzml 进程。可以尝试以管理员身份运行本应用。");

            var exports = PortableExportResolver.Read(module.FileName);
            _stringNew = _moduleBase + exports.GetRequired("il2cpp_string_new");
            _saveManager = CallPointerFunction(
                _moduleBase + exports.GetRequired("il2cpp_domain_get"),
                _moduleBase + exports.GetRequired("il2cpp_thread_attach"),
                _moduleBase + exports.GetRequired("il2cpp_thread_detach"),
                _moduleBase + _layout.SaveManagerGetInstance);
            if (_saveManager == 0) throw new InvalidOperationException("SaveManager.Instance 尚未就绪，请进入存档后重试。");
        }

        public IReadOnlyList<AdapterCharacterItem> ReadCharacters()
        {
            var slots = ReadCharacterSlots();
            var result = new List<AdapterCharacterItem>(slots.Count);
            foreach (var slot in slots)
            {
                var context = ExecuteCharacterOperation(slot.CharacterId, null);
                result.Add(ToAdapterCharacter(slot, context.Aggregated));
            }
            return result.OrderBy(item => item.DisplayName, StringComparer.CurrentCulture).ToList();
        }

        public AdapterCharacterItem WriteCharacterAttribute(string characterId, AttributeLayout attribute, int targetValue)
        {
            var slot = ReadCharacterSlots().SingleOrDefault(item =>
                string.Equals(item.CharacterId, characterId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"当前存档没有找到人物“{characterId}”。");
            var currentRaw = slot.RawValues[attribute.Key];
            var before = ExecuteCharacterOperation(characterId, null);
            var oldBase = ReadInt32(before.ConfigAddress + attribute.ConfigBaseOffset);
            var newBaseLong = (long)oldBase + targetValue - currentRaw;
            if (newBaseLong is < int.MinValue or > int.MaxValue)
                throw new InvalidOperationException("目标属性超出游戏可表达的 Int32 范围。");

            var after = ExecuteCharacterOperation(characterId,
                new CharacterWrite(attribute.ConfigBaseOffset, oldBase, (int)newBaseLong));
            var updatedSlot = slot with
            {
                RawValues = slot.RawValues.ToDictionary(
                    pair => pair.Key,
                    pair => string.Equals(pair.Key, attribute.Key, StringComparison.Ordinal) ? targetValue : pair.Value,
                    StringComparer.Ordinal)
            };
            var updated = ToAdapterCharacter(updatedSlot, after.Aggregated);
            var actual = updated.Attributes.Single(item => string.Equals(item.Key, attribute.Key, StringComparison.Ordinal));
            if (actual.RawValue != targetValue)
                throw new InvalidOperationException($"人物属性回读失败：预期 {targetValue}，实际 {actual.RawValue}。");
            return updated;
        }

        private IReadOnlyList<CharacterSlot> ReadCharacterSlots()
        {
            var allData = ReadUInt64(_saveManager + _layout.SaveManagerSnapshotOffset);
            if (allData == 0) throw new InvalidOperationException("当前存档快照尚未加载。");
            var playerDefault = ReadUInt64(allData + 0x28);
            if (playerDefault == 0) throw new InvalidOperationException("当前人物存档尚未加载。");
            var units = ReadUInt64(playerDefault + 0x30);
            if (units == 0) throw new InvalidOperationException("当前人物列表尚未加载。");
            var array = ReadUInt64(units + 0x10);
            var count = ReadInt32(units + 0x18);
            if (array == 0 || count is < 0 or > 10_000)
                throw new InvalidDataException($"人物列表结构无效（数量 {count}）。");

            var result = new List<CharacterSlot>(count);
            for (var index = 0; index < count; index++)
            {
                var address = ReadUInt64(array + 0x20UL + (ulong)index * 8UL);
                if (address == 0) continue;
                var characterId = ReadManagedString(ReadUInt64(address + 0x10));
                if (string.IsNullOrWhiteSpace(characterId)) continue;
                var displayName = ReadManagedString(ReadUInt64(address + 0x18));
                var rawValues = CharacterAttributes.ToDictionary(
                    item => item.Key,
                    item => ReadInt32(address + item.SlotValueOffset),
                    StringComparer.Ordinal);
                var growthValues = CharacterAttributes.ToDictionary(
                    item => item.Key,
                    item => ReadSingle(address + item.SlotGrowthOffset),
                    StringComparer.Ordinal);
                result.Add(new CharacterSlot(
                    characterId,
                    string.IsNullOrWhiteSpace(displayName) ? characterId : displayName,
                    ReadInt32(address + 0x38),
                    rawValues,
                    growthValues));
            }
            return result;
        }

        private static AdapterCharacterItem ToAdapterCharacter(CharacterSlot slot, AggregatedFiveDimensions aggregate) =>
            new(
                slot.CharacterId,
                slot.DisplayName,
                slot.Level,
                CharacterAttributes.Select(attribute => new AdapterCharacterAttribute(
                    attribute.Key,
                    attribute.DisplayName,
                    slot.RawValues[attribute.Key],
                    aggregate.Values[attribute.Key],
                    slot.GrowthValues[attribute.Key])).ToList());

        private CharacterOperationResult ExecuteCharacterOperation(string characterId, CharacterWrite? write)
        {
            var hookAddress = _moduleBase + _layout.ExecuteTasks;
            var original = Read(hookAddress, _layout.ExpectedHookPrologue.Length);
            if (!original.AsSpan().SequenceEqual(_layout.ExpectedHookPrologue))
                throw new InvalidDataException("游戏主线程入口与受支持版本不一致，已拒绝人物属性操作。");

            var remote = Allocate(4096);
            var remoteBase = unchecked((ulong)remote.ToInt64());
            var unitText = remoteBase + 0x800;
            var unitString = remoteBase + 0x900;
            var config = remoteBase + 0x908;
            var configManager = remoteBase + 0x910;
            var aggregate = remoteBase + 0x918;
            var status = remoteBase + 0x920;
            var patched = false;
            var oldProtection = 0u;
            var canFreeRemote = true;
            try
            {
                Write(unitText, Encoding.UTF8.GetBytes(characterId + "\0"));
                var code = BuildCharacterTrampoline(
                    hookAddress,
                    _moduleBase + _layout.InitializationFlag,
                    unitText,
                    unitString,
                    config,
                    configManager,
                    aggregate,
                    status,
                    _stringNew,
                    _moduleBase + TryGetUnitConfigById,
                    _moduleBase + ConfigManagerGetInstance,
                    _moduleBase + PlayerAttributeAggregatorCompute,
                    _moduleBase + PlayerAttributeEventHubRaise,
                    _saveManager,
                    write);
                Write(remoteBase, code);
                if (!VirtualProtectEx(_handle, (IntPtr)(long)hookAddress, (nuint)original.Length,
                        PageExecuteReadWrite, out oldProtection))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法临时写入游戏主线程入口。");
                Write(hookAddress, BuildAbsoluteJump(remoteBase, original.Length));
                patched = true;
                FlushInstructionCache(_handle, (IntPtr)(long)hookAddress, (nuint)original.Length);

                var stopwatch = Stopwatch.StartNew();
                var state = 0;
                while (state is 0 or 2 && stopwatch.Elapsed < TimeSpan.FromSeconds(5))
                {
                    Thread.Sleep(10);
                    state = ReadInt32(status);
                }
                if (state is 0 or 2)
                {
                    canFreeRemote = state == 0;
                    throw new TimeoutException(state == 2
                        ? "游戏主线程已进入人物属性操作，但未在五秒内完成。"
                        : "游戏主线程五秒内没有执行人物属性操作。");
                }
                if (state == 3) throw new InvalidOperationException($"找不到人物“{characterId}”的运行时配置。");
                if (state == 4) throw new InvalidOperationException("人物属性在操作前已变化，请刷新后重试。");
                if (state == 5) throw new InvalidOperationException("游戏属性聚合器没有返回结果。");
                if (state != 1) throw new InvalidOperationException($"人物属性操作返回未知状态 {state}。");

                var configAddress = ReadUInt64(config);
                var aggregateAddress = ReadUInt64(aggregate);
                if (configAddress == 0 || aggregateAddress == 0)
                    throw new InvalidOperationException("人物属性操作没有返回有效的运行时对象。");
                return new CharacterOperationResult(configAddress, ReadAggregatedFiveDimensions(aggregateAddress));
            }
            finally
            {
                if (patched)
                {
                    Write(hookAddress, original);
                    FlushInstructionCache(_handle, (IntPtr)(long)hookAddress, (nuint)original.Length);
                }
                if (oldProtection != 0)
                    VirtualProtectEx(_handle, (IntPtr)(long)hookAddress, (nuint)original.Length, oldProtection, out _);
                if (canFreeRemote) VirtualFreeEx(_handle, remote, 0, MemRelease);
            }
        }

        private AggregatedFiveDimensions ReadAggregatedFiveDimensions(ulong address) =>
            new(CharacterAttributes.ToDictionary(
                item => item.Key,
                item => ReadInt32(address + item.AggregatedOffset),
                StringComparer.Ordinal));

        private ulong CallPointerFunction(ulong domainGet, ulong threadAttach, ulong threadDetach, ulong target)
        {
            var result = Allocate(8);
            var codeAddress = Allocate(256);
            try
            {
                var code = new CharacterEmitter();
                code.Emit(0x53, 0x48, 0x83, 0xEC, 0x20);
                code.MovRax(domainGet); code.CallRax();
                code.Emit(0x48, 0x89, 0xC1);
                code.MovRax(threadAttach); code.CallRax();
                code.Emit(0x48, 0x89, 0xC3, 0x33, 0xC9, 0x33, 0xD2);
                code.MovRax(target); code.CallRax();
                code.MovRdx(unchecked((ulong)result.ToInt64())); code.Emit(0x48, 0x89, 0x02);
                code.Emit(0x48, 0x89, 0xD9);
                code.MovRax(threadDetach); code.CallRax();
                code.Emit(0x33, 0xC0, 0x48, 0x83, 0xC4, 0x20, 0x5B, 0xC3);
                Write(unchecked((ulong)codeAddress.ToInt64()), code.ToArray());
                var thread = CreateRemoteThread(_handle, IntPtr.Zero, 0, codeAddress, IntPtr.Zero, 0, out _);
                if (thread == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建 IL2CPP 查询线程。");
                try
                {
                    if (WaitForSingleObject(thread, Infinite) != 0) throw new InvalidOperationException("等待 IL2CPP 查询线程失败。");
                }
                finally
                {
                    CloseHandle(thread);
                }
                return ReadUInt64(unchecked((ulong)result.ToInt64()));
            }
            finally
            {
                VirtualFreeEx(_handle, result, 0, MemRelease);
                VirtualFreeEx(_handle, codeAddress, 0, MemRelease);
            }
        }

        private string ReadManagedString(ulong address)
        {
            if (address == 0) return string.Empty;
            var length = ReadInt32(address + 0x10);
            if (length is < 0 or > 1_000_000) throw new InvalidDataException($"IL2CPP 字符串长度无效：{length}。");
            return Encoding.Unicode.GetString(Read(address + 0x14, checked(length * 2)));
        }

        private IntPtr Allocate(int size)
        {
            var address = VirtualAllocEx(_handle, IntPtr.Zero, (nuint)size, MemCommitReserve, PageExecuteReadWrite);
            if (address == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法在目标进程分配临时内存。");
            return address;
        }

        private int ReadInt32(ulong address) => BitConverter.ToInt32(Read(address, 4));
        private ulong ReadUInt64(ulong address) => BitConverter.ToUInt64(Read(address, 8));
        private float ReadSingle(ulong address) => BitConverter.ToSingle(Read(address, 4));

        private byte[] Read(ulong address, int count)
        {
            var bytes = new byte[count];
            if (!ReadProcessMemory(_handle, (IntPtr)(long)address, bytes, (nuint)count, out var read) || read != (nuint)count)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"读取目标内存失败：0x{address:X}。");
            return bytes;
        }

        private void Write(ulong address, byte[] bytes)
        {
            if (!WriteProcessMemory(_handle, (IntPtr)(long)address, bytes, (nuint)bytes.Length, out var written) || written != (nuint)bytes.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"写入目标内存失败：0x{address:X}。");
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero) CloseHandle(_handle);
            _process.Dispose();
        }
    }

    private static byte[] BuildCharacterTrampoline(
        ulong hookAddress,
        ulong initializationFlagAddress,
        ulong unitText,
        ulong unitString,
        ulong configResult,
        ulong configManagerResult,
        ulong aggregateResult,
        ulong statusAddress,
        ulong stringNew,
        ulong tryGetUnitConfig,
        ulong configManagerGetInstance,
        ulong compute,
        ulong raise,
        ulong saveManager,
        CharacterWrite? write)
    {
        var code = new CharacterEmitter();
        code.MovRax(statusAddress);
        code.Emit(0x83, 0x38, 0x00);
        var alreadyExecuted = code.EmitNearConditionalJump(0x85);
        code.Emit(0x9C, 0x50, 0x51, 0x52, 0x41, 0x50, 0x41, 0x51, 0x41, 0x52, 0x41, 0x53);
        code.Emit(0x48, 0x83, 0xEC, 0x48);
        code.MovRdx(statusAddress); code.Emit(0xC7, 0x02, 0x02, 0x00, 0x00, 0x00);

        code.MovRcx(unitText); code.MovRax(stringNew); code.CallRax();
        code.MovRdx(unitString); code.Emit(0x48, 0x89, 0x02);
        code.MovRax(unitString); code.Emit(0x48, 0x8B, 0x08);
        code.MovRdx(configResult); code.Emit(0x45, 0x33, 0xC0);
        code.MovRax(tryGetUnitConfig); code.CallRax();
        code.Emit(0x84, 0xC0);
        var noConfig = code.EmitNearConditionalJump(0x84);

        var mismatch = -1;
        if (write is not null)
        {
            code.MovRax(configResult); code.Emit(0x48, 0x8B, 0x00);
            code.Emit(0x81, 0x78, checked((byte)write.ConfigOffset));
            code.Emit(BitConverter.GetBytes(write.ExpectedBase));
            mismatch = code.EmitNearConditionalJump(0x85);
            code.Emit(0xC7, 0x40, checked((byte)write.ConfigOffset));
            code.Emit(BitConverter.GetBytes(write.NewBase));
        }

        code.Emit(0x33, 0xC9, 0x33, 0xD2);
        code.MovRax(configManagerGetInstance); code.CallRax();
        code.MovRdx(configManagerResult); code.Emit(0x48, 0x89, 0x02);
        code.MovRax(unitString); code.Emit(0x48, 0x8B, 0x08);
        code.MovRdx(saveManager);
        code.MovRax(configManagerResult); code.Emit(0x4C, 0x8B, 0x00);
        code.Emit(0x45, 0x33, 0xC9);
        code.MovRax(compute); code.CallRax();
        code.MovRdx(aggregateResult); code.Emit(0x48, 0x89, 0x02);
        code.Emit(0x48, 0x85, 0xC0);
        var noAggregate = code.EmitNearConditionalJump(0x84);

        if (write is not null)
        {
            code.MovRax(unitString); code.Emit(0x48, 0x8B, 0x08);
            code.MovRax(aggregateResult); code.Emit(0x48, 0x8B, 0x10);
            code.Emit(0x45, 0x33, 0xC0);
            code.MovRax(raise); code.CallRax();
        }
        code.MovRdx(statusAddress); code.Emit(0xC7, 0x02, 0x01, 0x00, 0x00, 0x00);
        var success = code.EmitNearJump();

        var noConfigPath = code.Position;
        code.PatchNearJump(noConfig, noConfigPath);
        code.MovRdx(statusAddress); code.Emit(0xC7, 0x02, 0x03, 0x00, 0x00, 0x00);
        var noConfigCleanup = code.EmitNearJump();
        var mismatchCleanup = -1;
        if (mismatch >= 0)
        {
            var mismatchPath = code.Position;
            code.PatchNearJump(mismatch, mismatchPath);
            code.MovRdx(statusAddress); code.Emit(0xC7, 0x02, 0x04, 0x00, 0x00, 0x00);
            mismatchCleanup = code.EmitNearJump();
        }
        var noAggregatePath = code.Position;
        code.PatchNearJump(noAggregate, noAggregatePath);
        code.MovRdx(statusAddress); code.Emit(0xC7, 0x02, 0x05, 0x00, 0x00, 0x00);

        var cleanup = code.Position;
        code.PatchNearJump(success, cleanup);
        code.PatchNearJump(noConfigCleanup, cleanup);
        if (mismatchCleanup >= 0) code.PatchNearJump(mismatchCleanup, cleanup);
        code.Emit(0x48, 0x83, 0xC4, 0x48);
        code.Emit(0x41, 0x5B, 0x41, 0x5A, 0x41, 0x59, 0x41, 0x58, 0x5A, 0x59, 0x58, 0x9D);

        var fastPath = code.Position;
        code.PatchNearJump(alreadyExecuted, fastPath);
        code.Emit(0x48, 0x83, 0xEC, 0x28);
        code.MovRax(initializationFlagAddress);
        code.Emit(0x80, 0x38, 0x00);
        var initialized = code.EmitNearConditionalJump(0x85);
        code.MovRax(hookAddress + 13); code.JumpRax();
        var initializedPath = code.Position;
        code.PatchNearJump(initialized, initializedPath);
        code.MovRax(hookAddress + 0x20); code.JumpRax();
        return code.ToArray();
    }

    private static byte[] BuildAbsoluteJump(ulong target, int length)
    {
        if (length < 12) throw new ArgumentOutOfRangeException(nameof(length));
        var patch = Enumerable.Repeat((byte)0x90, length).ToArray();
        patch[0] = 0x48;
        patch[1] = 0xB8;
        BitConverter.GetBytes(target).CopyTo(patch, 2);
        patch[10] = 0xFF;
        patch[11] = 0xE0;
        return patch;
    }

    private sealed class CharacterEmitter
    {
        private readonly List<byte> _bytes = [];
        public int Position => _bytes.Count;
        public void Emit(params byte[] bytes) => _bytes.AddRange(bytes);
        public void MovRax(ulong value) { Emit(0x48, 0xB8); _bytes.AddRange(BitConverter.GetBytes(value)); }
        public void MovRcx(ulong value) { Emit(0x48, 0xB9); _bytes.AddRange(BitConverter.GetBytes(value)); }
        public void MovRdx(ulong value) { Emit(0x48, 0xBA); _bytes.AddRange(BitConverter.GetBytes(value)); }
        public void CallRax() => Emit(0xFF, 0xD0);
        public void JumpRax() => Emit(0xFF, 0xE0);
        public int EmitNearConditionalJump(byte condition)
        {
            Emit(0x0F, condition, 0, 0, 0, 0);
            return Position - 4;
        }
        public int EmitNearJump()
        {
            Emit(0xE9, 0, 0, 0, 0);
            return Position - 4;
        }
        public void PatchNearJump(int displacementOffset, int targetOffset)
        {
            var displacement = targetOffset - (displacementOffset + 4);
            var bytes = BitConverter.GetBytes(displacement);
            for (var index = 0; index < bytes.Length; index++) _bytes[displacementOffset + index] = bytes[index];
        }
        public byte[] ToArray() => _bytes.ToArray();
    }

    private sealed record AttributeLayout(
        string Key,
        string DisplayName,
        ulong SlotValueOffset,
        ulong ConfigBaseOffset,
        ulong SlotGrowthOffset,
        ulong AggregatedOffset);

    private sealed record CharacterSlot(
        string CharacterId,
        string DisplayName,
        int Level,
        IReadOnlyDictionary<string, int> RawValues,
        IReadOnlyDictionary<string, float> GrowthValues);

    private sealed record AggregatedFiveDimensions(IReadOnlyDictionary<string, int> Values);
    private sealed record CharacterOperationResult(ulong ConfigAddress, AggregatedFiveDimensions Aggregated);
    private sealed record CharacterWrite(ulong ConfigOffset, int ExpectedBase, int NewBase);
}
