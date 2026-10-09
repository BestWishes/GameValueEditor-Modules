using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using GameValueEditor.ModuleSdk;
using GameValueEditor.Modules.Runtime;

namespace GameValueEditor.Modules.Fzzml;

public sealed partial class FzzmlGameAdapter
{
    private const string CharacterEditorId = "game.fzzml.character-attributes";
    private static readonly AttributeLayout[] CharacterAttributes =
    [
        new("constitution", "根骨"),
        new("strength", "力道"),
        new("spirit", "神识"),
        new("agility", "身法"),
        new("vitality", "体魄")
    ];

    public bool SupportsCharacterAttributes(GameProcessContext process) =>
        Il2CppRuntimeResolver.IsNamedGame(process, "fzzml", "放置斩魔录");

    public IReadOnlyList<AdapterCharacterItem> ReadCharacters(GameProcessContext process)
    {
        if (!SupportsCharacterAttributes(process))
            throw new InvalidOperationException("当前 fzzml 构建尚未支持人物属性编辑；背包物品功能仍可单独使用。");

        lock (WriteGate)
        {
            using var session = new CharacterSession(process);
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
            using var session = new CharacterSession(process);
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
        private const uint MemRelease = 0x8000;
        private const uint PageExecuteReadWrite = 0x40;
        private readonly Process _process;
        private readonly IntPtr _handle;
        private readonly ulong _moduleBase;
        private readonly ulong _saveManager;
        private readonly ulong _stringNew;
        private readonly BuildLayout _layout;
        private readonly Il2CppRuntimeResolver _runtime;

        public CharacterSession(GameProcessContext context)
        {
            _process = Process.GetProcessById(context.ProcessId);
            _handle = OpenProcess(ProcessAccess, false, context.ProcessId);
            if (_handle == IntPtr.Zero) { _process.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
            try
            {
                _runtime = new Il2CppRuntimeResolver(context);
                _moduleBase = _runtime.ModuleBase;
                _layout = new BuildLayout(_runtime);
                _stringNew = _runtime.Export("il2cpp_string_new");
                _saveManager = _runtime.Call(_moduleBase + _layout.SaveManagerGetInstance);
                if (_saveManager == 0) throw new InvalidOperationException("当前存档尚未加载，请进入存档后刷新。");
            }
            catch { _runtime?.Dispose(); CloseHandle(_handle); _process.Dispose(); throw; }
        }

        private ulong CharacterMethod(string ns, string klass, string method, string result, params string[] args) =>
            _runtime.Method("Assembly-CSharp.dll", ns, klass, method, true, result, args).Pointer;
        private const string ConfigType = "Script.ScriptTableObj.Player.PlayerUnitConfig";
        private const string ConfigManagerType = "Script.Manager.Core.ConfigManager";
        private const string SaveType = "Script.Manager.Save.SaveManager";
        private const string AggregateType = "Script.Player.Listen.AggregatedAttributes";

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
            var oldBase = _runtime.ReadInt(_runtime.Address(before.ConfigAddress, attribute.Key, "System.Int32"));
            var newBaseLong = (long)oldBase + targetValue - currentRaw;
            if (newBaseLong is < int.MinValue or > int.MaxValue)
                throw new InvalidOperationException("目标属性超出游戏可表达的 Int32 范围。");

            var after = ExecuteCharacterOperation(characterId,
                new CharacterWrite(_runtime.Field(_runtime.ObjectClass(before.ConfigAddress), attribute.Key, "System.Int32"), oldBase, (int)newBaseLong));
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
            var playerDefault = _runtime.Reference(allData, "playerDefault", "Script.Player.Save.PlayerSaveData");
            if (playerDefault == 0) throw new InvalidOperationException("当前人物存档尚未加载。");
            var units = _runtime.Reference(playerDefault, "units", "System.Collections.Generic.List<Script.Player.Save.PlayerSaveData/UnitSlotData>");
            if (units == 0) throw new InvalidOperationException("当前人物列表尚未加载。");
            var array = _runtime.ListItems(units);
            var count = _runtime.ListCount(units);
            if (array == 0 || count is < 0 or > 10_000)
                throw new InvalidDataException($"人物列表结构无效（数量 {count}）。");

            var result = new List<CharacterSlot>(count);
            for (var index = 0; index < count; index++)
            {
                var address = ReadUInt64(array + 0x20UL + (ulong)index * 8UL);
                if (address == 0) continue;
                var characterId = ReadManagedString(_runtime.Reference(address, "unitId", "System.String"));
                if (string.IsNullOrWhiteSpace(characterId)) continue;
                var displayName = ReadManagedString(_runtime.Reference(address, "displayName", "System.String"));
                var rawValues = CharacterAttributes.ToDictionary(
                    item => item.Key,
                    item => _runtime.ReadInt(_runtime.Address(address, item.Key, "System.Int32")),
                    StringComparer.Ordinal);
                var growthValues = CharacterAttributes.ToDictionary(
                    item => item.Key,
                    item => ReadSingle(_runtime.Address(address, "growth" + char.ToUpperInvariant(item.Key[0]) + item.Key[1..], "System.Single")),
                    StringComparer.Ordinal);
                result.Add(new CharacterSlot(
                    characterId,
                    string.IsNullOrWhiteSpace(displayName) ? characterId : displayName,
                    _runtime.ReadInt(_runtime.Address(address, "level", "System.Int32")),
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
            var expected = _layout.ExpectedHookPrologue;
            var original = Read(hookAddress, expected.Length);
            if (!original.AsSpan().SequenceEqual(expected))
                throw new InvalidDataException("游戏主线程入口正在变化，请稍后重试；未执行人物属性操作。");

            var remote = Il2CppMainThreadHook.AllocateNear(_handle, hookAddress, 4096);
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
                    remoteBase + 0x600,
                    unitText,
                    unitString,
                    config,
                    configManager,
                    aggregate,
                    status,
                    _stringNew,
                    CharacterMethod("Script.Manager.UI.CharacterMenu", "CharacterMenuRealmManager", "TryGetUnitConfigById", "System.Boolean", "System.String", ConfigType + "&"),
                    CharacterMethod("Script.Manager.Core", "ConfigManager", "get_Instance", ConfigManagerType),
                    CharacterMethod("Script.Player.Listen", "PlayerAttributeAggregator", "Compute", AggregateType, "System.String", SaveType, ConfigManagerType),
                    write == null ? 0 : CharacterMethod("Script.Player.Listen", "PlayerAttributeEventHub", "RaiseAttributesChanged", "System.Void", "System.String", AggregateType),
                    _saveManager,
                    write);
                if (code.Length >= 0x600) throw new InvalidDataException("人物操作代码超出缓冲区。");
                Write(remoteBase + 0x600, Il2CppMainThreadHook.Relocate(original, hookAddress, remoteBase + 0x600));
                Write(remoteBase, code);
                if (!VirtualProtectEx(_handle, (IntPtr)(long)hookAddress, (nuint)original.Length,
                        PageExecuteReadWrite, out oldProtection))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法临时写入游戏主线程入口。");
                canFreeRemote = false;
                patched = true;
                Il2CppMainThreadHook.WritePatch(_process, _handle, hookAddress, Il2CppMainThreadHook.Jump(remoteBase, original.Length));
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
                    canFreeRemote = false;
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
                    Il2CppMainThreadHook.WritePatch(_process, _handle, hookAddress, original);
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
                item => _runtime.ReadInt(_runtime.Address(address, item.Key, "System.Int32")),
                StringComparer.Ordinal));


        private string ReadManagedString(ulong address)
        {
            if (address == 0) return string.Empty;
            var length = ReadInt32(address + 0x10);
            if (length is < 0 or > 1_000_000) throw new InvalidDataException($"IL2CPP 字符串长度无效：{length}。");
            return Encoding.Unicode.GetString(Read(address + 0x14, checked(length * 2)));
        }


        private int ReadInt32(ulong address) => BitConverter.ToInt32(Read(address, 4));
        private ulong ReadUInt64(ulong address) => BitConverter.ToUInt64(Read(address, 8));
        private float ReadSingle(ulong address) => BitConverter.ToSingle(Read(address, 4));

        private byte[] Read(ulong address, int count)
        {
            _runtime.CheckAlive();
            var bytes = new byte[count];
            if (!ReadProcessMemory(_handle, (IntPtr)(long)address, bytes, (nuint)count, out var read) || read != (nuint)count)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"读取目标内存失败：0x{address:X}。");
            return bytes;
        }

        private void Write(ulong address, byte[] bytes)
        {
            _runtime.CheckAlive();
            if (!WriteProcessMemory(_handle, (IntPtr)(long)address, bytes, (nuint)bytes.Length, out var written) || written != (nuint)bytes.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"写入目标内存失败：0x{address:X}。");
        }

        public void Dispose()
        {
            _runtime.Dispose();
            if (_handle != IntPtr.Zero) CloseHandle(_handle);
            _process.Dispose();
        }
    }

    private static byte[] BuildCharacterTrampoline(
        ulong hookAddress,
        ulong continuationAddress,
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
            code.Emit(0x81, 0xB8); code.Emit(BitConverter.GetBytes(checked((int)write.ConfigOffset)));
            code.Emit(BitConverter.GetBytes(write.ExpectedBase));
            mismatch = code.EmitNearConditionalJump(0x85);
            code.Emit(0xC7, 0x80); code.Emit(BitConverter.GetBytes(checked((int)write.ConfigOffset)));
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
        return Il2CppMainThreadHook.Wrap(code.ToArray(), statusAddress, continuationAddress);
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

    private sealed record AttributeLayout(string Key, string DisplayName);

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
