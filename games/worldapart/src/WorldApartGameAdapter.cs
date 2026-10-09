using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using GameValueEditor.ModuleSdk;
using GameValueEditor.Modules.Ui;
using GameValueEditor.Modules.Runtime;

namespace GameValueEditor.Modules.WorldApart;

public sealed partial class WorldApartGameAdapter :
    IInventoryGameAdapter,
    ICharacterAttributesGameAdapter,
    IGameVersionMetadataProvider,
    ICoordinatedGameEditorPageProvider,
    IGameCompatibilityDiagnosticsProvider
{
    private const string InventoryEditorId = "game.worldapart.inventory";
    private const string CharacterEditorId = "game.worldapart.character-attributes";
    private static readonly object WriteGate = new();
    public string Id => "game.worldapart";
    public string DisplayName => "不问凡尘专属修改模块";
    public string Description => "提供背包物品与人物属性两项专属编辑功能，并调用游戏自己的保存流程。";
    public IReadOnlyList<GameEditorDescriptor> Editors { get; } =
    [
        new(InventoryEditorId, "背包物品", GameEditorKind.Custom, 100,
            "按稳定物品 ID 实时读取和修改背包物品总数，并调用游戏自身保存流程。"),
        new(CharacterEditorId, "人物属性", GameEditorKind.Custom, 200,
            "修改当前玩家的资源、基础属性、探索属性、五行灵根和战斗属性，并调用游戏自身保存流程。")
    ];
    public IGameEditorPage CreateEditorPage(string editorId, GameEditorPageContext context) => editorId switch
    {
        InventoryEditorId => new InventoryEditorPage(this, context,
            "按稳定物品 ID 实时读取和修改背包物品总数，并调用游戏自身保存流程。"),
        CharacterEditorId => new CharacterEditorPage(this, context, CharacterEditorId,
            "修改当前玩家的资源、基础属性、探索属性、五行灵根和战斗属性，并调用游戏自身保存流程。"),
        _ => throw new InvalidOperationException($"WorldApart 模块没有页面：{editorId}。")
    };

    public bool Supports(GameProcessContext process, GameBuildIdentity fingerprint) =>
        Il2CppRuntimeResolver.IsNamedGame(process, "WorldApart", "不问凡尘") &&
        File.Exists(Path.Combine(Path.GetDirectoryName(process.ExecutablePath)!, "GameAssembly.dll"));

    public IReadOnlyList<GameCompatibilityDiagnostic> GetCompatibilityDiagnostics(GameProcessContext process, GameBuildIdentity fingerprint) =>
    [
        new("游戏名称", Supports(process, fingerprint) ? GameCompatibilityDiagnosticStatus.Passed : GameCompatibilityDiagnosticStatus.Failed,
            "不问凡尘 / WorldApart；不以历史文件哈希拒绝小更新。"),
        new("当前运行数据定位", GameCompatibilityDiagnosticStatus.Information,
            "背包、人物及原生保存方法从当前 IL2CPP 元数据解析；诊断没有执行数值修改。")
    ];

    public GameDeclaredVersionInfo ReadGameVersionMetadata(GameProcessContext process)
    {
        using var session = new Session(process, initializeRoots: false);
        return session.ReadGameDeclaredVersion();
    }

    public IReadOnlyList<AdapterInventoryItem> ReadInventory(GameProcessContext process)
    {
        using var session = new Session(process);
        return session.ReadInventory();
    }

    public AdapterFieldValue ReadField(GameProcessContext process, string fieldKey)
    {
        if (!ModuleFieldKey.TryParse(fieldKey, out var editorId, out var entityId, out var fieldId))
            throw new InvalidOperationException("专属字段语义键格式无效。请删除旧快捷入口后重新保存。");

        if (string.Equals(editorId, InventoryEditorId, StringComparison.Ordinal))
        {
            if (!int.TryParse(entityId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId) ||
                !string.Equals(fieldId, "count", StringComparison.Ordinal))
                throw new InvalidOperationException("背包快捷入口的物品语义键无效。");
            using var session = new Session(process);
            var item = session.ReadInventory().SingleOrDefault(candidate =>
                TryParseInventoryFieldKey(candidate.FieldKey, out var candidateId) && candidateId == itemId)
                ?? throw new InvalidOperationException($"当前背包中没有物品 ID {itemId}。物品重新获得后可继续使用此快捷入口。");
            return new AdapterFieldValue(fieldKey, item.CountDisplay, "已按物品 ID 重新定位当前背包对象");
        }

        if (string.Equals(editorId, CharacterEditorId, StringComparison.Ordinal))
        {
            var character = ReadCharacters(process).Single(candidate =>
                string.Equals(candidate.CharacterId, entityId, StringComparison.Ordinal));
            var attribute = character.Attributes.Single(candidate =>
                string.Equals(candidate.Key, fieldId, StringComparison.Ordinal));
            return new AdapterFieldValue(fieldKey, attribute.RawValueDisplay,
                $"已按属性语义键重新定位 · 游戏界面总值 {attribute.AggregatedValueDisplay}");
        }

        throw new InvalidOperationException($"不属于 WorldApart 模块的字段：{editorId}。");
    }

    public AdapterFieldValue WriteField(GameProcessContext process, string fieldKey, string displayValue)
    {
        if (!int.TryParse(displayValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetValue) || targetValue < 0)
            throw new InvalidOperationException("目标值必须是 0 到 2147483647 之间的整数。");
        if (!ModuleFieldKey.TryParse(fieldKey, out var editorId, out var entityId, out var fieldId))
            throw new InvalidOperationException("专属字段语义键格式无效。请删除旧快捷入口后重新保存。");

        if (string.Equals(editorId, InventoryEditorId, StringComparison.Ordinal))
        {
            if (!int.TryParse(entityId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId) ||
                !string.Equals(fieldId, "count", StringComparison.Ordinal))
                throw new InvalidOperationException("背包快捷入口的物品语义键无效。");
            lock (WriteGate)
            {
                using var session = new Session(process);
                var updated = session.WriteInventory(itemId, targetValue);
                return new AdapterFieldValue(fieldKey, updated.CountDisplay, "已实时写入、刷新并调用游戏自身保存流程");
            }
        }

        if (string.Equals(editorId, CharacterEditorId, StringComparison.Ordinal))
        {
            var character = WriteCharacterAttribute(process, entityId, fieldId, targetValue);
            var attribute = character.Attributes.Single(candidate => string.Equals(candidate.Key, fieldId, StringComparison.Ordinal));
            return new AdapterFieldValue(fieldKey, attribute.RawValueDisplay,
                $"已实时写入并保存 · 游戏界面总值 {attribute.AggregatedValueDisplay}");
        }

        throw new InvalidOperationException($"不属于 WorldApart 模块的字段：{editorId}。");
    }

    private static bool TryParseInventoryFieldKey(string fieldKey, out int itemId)
    {
        itemId = 0;
        return ModuleFieldKey.TryParse(fieldKey, out var editorId, out var entityId, out var fieldId) &&
               string.Equals(editorId, InventoryEditorId, StringComparison.Ordinal) &&
               string.Equals(fieldId, "count", StringComparison.Ordinal) &&
               int.TryParse(entityId, NumberStyles.Integer, CultureInfo.InvariantCulture, out itemId);
    }

    private sealed partial class Session : IDisposable
    {
        private const uint ProcessAccess = 0x0002 | 0x0008 | 0x0010 | 0x0020 | 0x0400;
        private const uint MemRelease = 0x8000;
        private const uint PageExecuteReadWrite = 0x40;

        private readonly Process _process;
        private readonly IntPtr _handle;
        private readonly ulong _moduleBase;
        private readonly BuildLayout _layout;
        private readonly Il2CppRuntimeResolver _runtime;
        private ulong _player;
        private ulong _tables;

        public Session(GameProcessContext context, bool initializeRoots = true)
        {
            _process = Process.GetProcessById(context.ProcessId);
            _handle = OpenProcess(ProcessAccess, false, context.ProcessId);
            if (_handle == IntPtr.Zero) { _process.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), "无法连接不问凡尘进程。"); }
            try
            {
                _runtime = new Il2CppRuntimeResolver(context);
                _moduleBase = _runtime.ModuleBase;
                _layout = new BuildLayout(_runtime);
                if (initializeRoots) RefreshRoots();
            }
            catch { _runtime?.Dispose(); CloseHandle(_handle); _process.Dispose(); throw; }
        }

        private void RefreshRoots()
        {
            _player = CallPointerFunction(_moduleBase + _layout.GameStoreManagerGetCurrentPlayer);
            if (_player == 0) throw new InvalidOperationException("当前玩家存档尚未加载。请进入可操作的游戏存档后重试。");
            _tables = _runtime.StaticReference(_runtime.Class("Assembly-CSharp.dll", "LubanDatas", "Tables"), "Current", "LubanDatas.Tables");
            if (_tables == 0) throw new InvalidOperationException("WorldApart 配置表尚未加载完成，请稍后重试。");
        }

        public IReadOnlyList<AdapterInventoryItem> ReadInventory()
        {
            RefreshRoots();
            var rows = ReadBagRows();
            var names = ReadItemNames(rows.Select(row => row.ItemId).Distinct());
            return rows.GroupBy(row => row.ItemId)
                .Select(group =>
                {
                    var itemId = group.Key;
                    var name = names.TryGetValue(itemId, out var resolved) && !string.IsNullOrWhiteSpace(resolved)
                        ? resolved
                        : $"物品 #{itemId}";
                    return new AdapterInventoryItem(
                        ModuleFieldKey.Create(InventoryEditorId, itemId.ToString(CultureInfo.InvariantCulture), "count"),
                        name,
                        group.Sum(row => (long)row.Count));
                })
                .OrderBy(item => item.DisplayName, StringComparer.CurrentCulture)
                .ToList();
        }

        public GameDeclaredVersionInfo ReadGameDeclaredVersion()
        {
            if (_layout.ApplicationGetVersion == 0)
                return new GameDeclaredVersionInfo(string.Empty, string.Empty, string.Empty);
            var version = ReadManagedString(CallPointerFunction(_moduleBase + _layout.ApplicationGetVersion));
            var productName = _layout.ApplicationGetProductName == 0
                ? string.Empty
                : ReadManagedString(CallPointerFunction(_moduleBase + _layout.ApplicationGetProductName));
            var buildGuid = _layout.ApplicationGetBuildGuid == 0
                ? string.Empty
                : ReadManagedString(CallPointerFunction(_moduleBase + _layout.ApplicationGetBuildGuid));
            return new GameDeclaredVersionInfo(version, productName, buildGuid);
        }

        public AdapterInventoryItem WriteInventory(int itemId, int targetValue)
        {
            RefreshRoots();
            var bag = _runtime.Reference(_player, "bag", "Game.Model.Components.BagModel");
            if (bag == 0) throw new InvalidOperationException("当前背包尚未加载。");
            var rows = ReadBagRows().Where(row => row.ItemId == itemId).OrderBy(row => row.Address).ToList();
            if (rows.Count == 0)
                throw new InvalidOperationException($"当前背包中没有物品 ID {itemId}。本版本暂不创建从未获得过的物品。");

            var writes = rows.Select((row, index) => new MemoryWrite(_runtime.Address(row.Address, "Count", "System.Int32"), index == 0 ? targetValue : 0)).ToArray();
            ExecuteOnMainThread(new MainThreadRequest(
                writes,
                Operation: MainThreadOperation.InventoryRefresh,
                Owner: bag,
                AttributeId: 0,
                TargetValue: targetValue));

            Thread.Sleep(150);
            RefreshRoots();
            var updated = ReadInventory().SingleOrDefault(candidate =>
                TryParseInventoryFieldKey(candidate.FieldKey, out var candidateId) && candidateId == itemId);
            if (targetValue == 0 && updated is null)
            {
                var fallbackName = ReadItemNames([itemId]).GetValueOrDefault(itemId, $"物品 #{itemId}");
                return new AdapterInventoryItem(
                    ModuleFieldKey.Create(InventoryEditorId, itemId.ToString(CultureInfo.InvariantCulture), "count"),
                    fallbackName,
                    0);
            }
            if (updated is null || updated.Count != targetValue)
                throw new InvalidOperationException(
                    $"背包回读失败：预期物品 ID {itemId} 的总数为 {targetValue}，实际为 {updated?.Count ?? 0}。");
            return updated;
        }

        private List<BagRow> ReadBagRows()
        {
            var bag = _runtime.Reference(_player, "bag", "Game.Model.Components.BagModel");
            if (bag == 0) throw new InvalidOperationException("当前背包尚未加载。");
            var list = _runtime.Reference(bag, "<Items>k__BackingField", "System.Collections.Generic.List<Game.Model.Components.BagItemBase>");
            if (list == 0) throw new InvalidOperationException("当前背包物品列表尚未加载。");
            var array = _runtime.ListItems(list);
            var count = _runtime.ListCount(list);
            if (array == 0 || count is < 0 or > 100_000)
                throw new InvalidDataException($"背包列表结构无效（记录数 {count}）。");

            var rows = new List<BagRow>(count);
            for (var index = 0; index < count; index++)
            {
                var item = ReadUInt64(array + 0x20UL + (ulong)index * 8UL);
                if (item == 0) continue;
                var itemId = _runtime.ReadInt(_runtime.Address(item, "ItemId", "LubanDatas.TbItemId"));
                var itemCount = _runtime.ReadInt(_runtime.Address(item, "Count", "System.Int32"));
                if (itemId <= 0 || itemCount < 0) continue;
                rows.Add(new BagRow(item, itemId, itemCount));
            }
            return rows;
        }

        private Dictionary<int, string> ReadItemNames(IEnumerable<int> itemIds)
        {
            var requested = itemIds.ToHashSet();
            var result = new Dictionary<int, string>();
            if (requested.Count == 0) return result;
            var table = _runtime.Reference(_tables, "<TbItem>k__BackingField", "LubanDatas.TbItem");
            if (table == 0) return result;
            var configs = ReadReferenceList(_runtime.Reference(table, "_dataList", "System.Collections.Generic.List<LubanDatas.data.Item>"), "物品配置表");
            foreach (var config in configs)
            {
                var id = _runtime.ReadInt(_runtime.Address(config, "<id>k__BackingField", "LubanDatas.TbItemId"));
                if (!requested.Contains(id)) continue;
                var value = CallPointerFunction(_moduleBase + _layout.L10nTextGetValue, _runtime.Address(config, "<itemName>k__BackingField", "LubanDatas.L10nText"));
                var text = ReadManagedString(value);
                if (!string.IsNullOrWhiteSpace(text)) result[id] = text;
                if (result.Count == requested.Count) break;
            }
            return result;
        }

        private IReadOnlyList<ulong> ReadReferenceList(ulong list, string description)
        {
            if (list == 0) return [];
            var array = _runtime.ListItems(list);
            var count = _runtime.ListCount(list);
            if (array == 0 || count is < 0 or > 1_000_000)
                throw new InvalidDataException($"{description}结构无效（记录数 {count}）。");
            var values = new List<ulong>(count);
            for (var index = 0; index < count; index++)
            {
                var value = ReadUInt64(array + 0x20UL + (ulong)index * 8UL);
                if (value != 0) values.Add(value);
            }
            return values;
        }

        private ulong CallPointerFunction(ulong target, ulong arg1 = 0, ulong arg2 = 0, ulong arg3 = 0) =>
            _runtime.Call(target, arg1, arg2, arg3);

        private ulong ResolveMethodFromObject(ulong instance, string methodName, int argumentCount) =>
            _runtime.Method(_runtime.ObjectClass(instance), methodName, false, "System.Void", "System.Int32", "System.Int32").Info;

        private void ExecuteOnMainThread(MainThreadRequest request)
        {
            request = request with { Writes = request.Writes.Select(write => write with { Expected = ReadInt32(write.Address) }).ToArray() };
            var hookAddress = _moduleBase + _layout.ExecuteTasks;
            var expected = _layout.ExpectedHookPrologue;
            var original = Read(hookAddress, expected.Length);
            if (!original.AsSpan().SequenceEqual(expected))
                throw new InvalidDataException(
                    "游戏主线程入口正在变化，请稍后重试；未执行修改。");

            var remote = Il2CppMainThreadHook.AllocateNear(_handle, hookAddress, 4096);
            var remoteBase = unchecked((ulong)remote.ToInt64());
            var statusAddress = remoteBase + 0xF00;
            var saveResultAddress = remoteBase + 0xF08;
            var patched = false;
            var oldProtection = 0u;
            var canFreeRemote = true;
            try
            {
                var code = BuildMainThreadTrampoline(
                    request,
                    statusAddress,
                    saveResultAddress,
                    _moduleBase,
                    _layout,
                    _player);
                code = Il2CppMainThreadHook.Wrap(code, statusAddress, remoteBase + 0xA00);
                if (code.Length >= 0xA00) throw new InvalidDataException("操作代码超出缓冲区。");
                Write(remoteBase + 0xA00, Il2CppMainThreadHook.Relocate(original, hookAddress, remoteBase + 0xA00));
                Write(remoteBase, code);
                if (!VirtualProtectEx(_handle, (IntPtr)(long)hookAddress, (nuint)original.Length,
                        PageExecuteReadWrite, out oldProtection))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法临时写入 WorldApart 主线程入口。");
                canFreeRemote = false;
                patched = true;
                Il2CppMainThreadHook.WritePatch(_process, _handle, hookAddress, Il2CppMainThreadHook.Jump(remoteBase, original.Length));
                FlushInstructionCache(_handle, (IntPtr)(long)hookAddress, (nuint)original.Length);

                var stopwatch = Stopwatch.StartNew();
                var status = 0;
                while (status is 0 or 2 or 3 && stopwatch.Elapsed < TimeSpan.FromSeconds(5))
                {
                    Thread.Sleep(10);
                    status = ReadInt32(statusAddress);
                }
                if (status is 0 or 2 or 3)
                {
                    canFreeRemote = false;
                    throw new TimeoutException(status == 0
                        ? "WorldApart 主线程五秒内没有执行修改流程。"
                        : "WorldApart 主线程已进入修改流程，但未在五秒内安全返回。");
                }
                if (status == 4)
                    throw new InvalidOperationException("当前存档对象已失效，请返回游戏后刷新再试。");
                if (status == 5)
                    throw new InvalidOperationException("数据在操作前已变化，请刷新后重试；未执行修改。");
                if (status != 1)
                    throw new InvalidOperationException($"WorldApart 修改流程返回未知状态 {status}。");
                if (ReadUInt64(saveResultAddress) == 0)
                    throw new InvalidOperationException("游戏自身的自动存档流程没有返回有效结果。");
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

        private static byte[] BuildMainThreadTrampoline(
            MainThreadRequest request,
            ulong statusAddress,
            ulong saveResultAddress,
            ulong moduleBase,
            BuildLayout layout,
            ulong expectedPlayer)
        {
            var code = new Emitter();
            // Confirm the current save before touching any previous object address.
            code.Emit(0x33, 0xC9, 0x33, 0xD2);
            code.MovRax(moduleBase + layout.GameStoreManagerGetCurrentPlayer); code.CallRax();
            code.MovRdx(expectedPlayer); code.Emit(0x48, 0x39, 0xD0);
            var changedPlayer = code.EmitNearConditionalJump(0x85);
            code.Emit(0x33, 0xC9, 0x33, 0xD2);
            code.MovRax(moduleBase + layout.GameStoreManagerGetCurrentStore); code.CallRax();
            code.Emit(0x48, 0x85, 0xC0);
            var noStoreJump = code.EmitNearConditionalJump(0x84);
            code.MovRdx(saveResultAddress + 8); code.Emit(0x48, 0x89, 0x02);
            var changedValues = new List<int>();
            foreach (var write in request.Writes)
            {
                code.MovRax(write.Address); code.Emit(0x81, 0x38); code.Emit(BitConverter.GetBytes(write.Expected));
                changedValues.Add(code.EmitNearConditionalJump(0x85));
            }
            foreach (var write in request.Writes)
            {
                code.MovRax(write.Address);
                code.Emit(0xC7, 0x00);
                code.Emit(BitConverter.GetBytes(write.Value));
            }

            EmitOperation(code, request, moduleBase, layout);

            code.MovRax(saveResultAddress + 8); code.Emit(0x48, 0x8B, 0x08, 0x33, 0xD2);
            code.MovRax(moduleBase + layout.GameStoreSaveAutoSlotImmediately); code.CallRax();
            code.MovRdx(saveResultAddress); code.Emit(0x48, 0x89, 0x02);
            code.MovRax(statusAddress); code.Emit(0xC7, 0x00, 0x01, 0x00, 0x00, 0x00);
            var afterFailureJump = code.EmitNearJump();

            var noStore = code.Position;
            code.PatchNearJump(noStoreJump, noStore);
            code.PatchNearJump(changedPlayer, noStore);
            code.MovRax(statusAddress); code.Emit(0xC7, 0x00, 0x04, 0x00, 0x00, 0x00);
            var failedStore = code.EmitNearJump();
            foreach (var jump in changedValues) code.PatchNearJump(jump, code.Position);
            code.MovRax(statusAddress); code.Emit(0xC7, 0x00, 0x05, 0x00, 0x00, 0x00);

            var restore = code.Position;
            code.PatchNearJump(afterFailureJump, restore);
            code.PatchNearJump(failedStore, restore);
            return code.ToArray();
        }

        private static void EmitOperation(Emitter code, MainThreadRequest request, ulong moduleBase, BuildLayout layout)
        {
            switch (request.Operation)
            {
                case MainThreadOperation.InventoryRefresh:
                    code.MovRcx(request.Owner); code.Emit(0x33, 0xD2);
                    code.MovRax(moduleBase + layout.BagSort); code.CallRax();
                    code.MovRcx(request.Owner); code.Emit(0x33, 0xD2);
                    code.MovRax(moduleBase + layout.BagRefreshRedDots); code.CallRax();
                    break;
                case MainThreadOperation.None:
                    break;
                default:
                    EmitCharacterOperation(code, request, moduleBase, layout);
                    break;
            }
        }

        static partial void EmitCharacterOperation(Emitter code, MainThreadRequest request, ulong moduleBase, BuildLayout layout);

        private Dictionary<int, DictionaryEntry<int>> ReadIntDictionary(ulong dictionary, string description) =>
            ReadDictionary(dictionary, description, address => ReadInt32(address), "System.Int32");
        private Dictionary<int, DictionaryEntry<float>> ReadFloatDictionary(ulong dictionary, string description) =>
            ReadDictionary(dictionary, description, address => ReadSingle(address), "System.Single");

        private Dictionary<int, DictionaryEntry<T>> ReadDictionary<T>(ulong dictionary, string description, Func<ulong, T> readValue, string valueType)
        {
            var result = new Dictionary<int, DictionaryEntry<T>>();
            if (dictionary == 0) return result;
            var klass = _runtime.ObjectClass(dictionary);
            var entriesType = _runtime.FieldType(klass, "_entries");
            if (!entriesType.EndsWith("[]", StringComparison.Ordinal)) throw new InvalidDataException(description + "不是字典条目数组。");
            var entries = _runtime.Reference(dictionary, "_entries", entriesType);
            var count = _runtime.ReadInt(_runtime.Address(dictionary, "_count", "System.Int32"));
            if (entries == 0) { if (count == 0) return result; throw new InvalidDataException(description + "条目尚未加载。"); }
            if (count is < 0 or > 100_000 || count > ReadInt32(entries + 0x18)) throw new InvalidDataException(description + "记录数无效。");
            var arrayClass = _runtime.ObjectClass(entries);
            var entryClass = _runtime.Call(_runtime.Export("il2cpp_class_get_element_class"), arrayClass);
            var stride = _runtime.Call(_runtime.Export("il2cpp_array_element_size"), arrayClass);
            var keyType = _runtime.FieldType(entryClass, "key");
            if (keyType != "System.Int32" && keyType != "Game.Model.Player.Components.CombatAttrId" && keyType != "LubanDatas.TbInteractAttributeId")
                throw new InvalidDataException(description + "键类型已变化。");
            var keyOffset = _runtime.Field(entryClass, "key", keyType) - 0x10;
            var valueOffset = _runtime.Field(entryClass, "value", valueType) - 0x10;
            var hashType = _runtime.FieldType(entryClass, "hashCode");
            if (hashType is not ("System.Int32" or "System.UInt32")) throw new InvalidDataException(description + "哈希类型已变化。");
            var hashOffset = _runtime.Field(entryClass, "hashCode", hashType) - 0x10;
            var nextOffset = _runtime.Field(entryClass, "next", "System.Int32") - 0x10;
            if (stride is < 12 or > 64 || keyOffset + 4 > stride || valueOffset + 4 > stride) throw new InvalidDataException(description + "条目布局无效。");
            for (var index = 0; index < count; index++)
            {
                var entry = entries + 0x20UL + (ulong)index * stride;
                if (hashType == "System.Int32" ? ReadInt32(entry + hashOffset) < 0 : ReadInt32(entry + nextOffset) < -1) continue;
                var key = ReadInt32(entry + keyOffset);
                if (!result.TryAdd(key, new DictionaryEntry<T>(entry + valueOffset, readValue(entry + valueOffset)))) throw new InvalidDataException(description + "键重复。");
            }
            return result;
        }

        private string ReadManagedString(ulong address)
        {
            if (address == 0) return string.Empty;
            var length = ReadInt32(address + 0x10);
            if (length is < 0 or > 1_000_000)
                throw new InvalidDataException($"IL2CPP 字符串长度无效：{length}。");
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
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"读取 WorldApart 内存失败：0x{address:X}。");
            return bytes;
        }

        private void Write(ulong address, byte[] bytes)
        {
            _runtime.CheckAlive();
            if (!WriteProcessMemory(_handle, (IntPtr)(long)address, bytes, (nuint)bytes.Length, out var written) ||
                written != (nuint)bytes.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"写入 WorldApart 内存失败：0x{address:X}。");
        }

        public void Dispose()
        {
            _runtime.Dispose();
            if (_handle != IntPtr.Zero) CloseHandle(_handle);
            _process.Dispose();
        }
    }

    private sealed class Emitter
    {
        private readonly List<byte> _bytes = [];
        public int Position => _bytes.Count;
        public void Emit(params byte[] bytes) => _bytes.AddRange(bytes);
        public void MovRax(ulong value) { Emit(0x48, 0xB8); Emit(BitConverter.GetBytes(value)); }
        public void MovRcx(ulong value) { Emit(0x48, 0xB9); Emit(BitConverter.GetBytes(value)); }
        public void MovRdx(ulong value) { Emit(0x48, 0xBA); Emit(BitConverter.GetBytes(value)); }
        public void MovR8(ulong value) { Emit(0x49, 0xB8); Emit(BitConverter.GetBytes(value)); }
        public void MovR9(ulong value) { Emit(0x49, 0xB9); Emit(BitConverter.GetBytes(value)); }
        public void MovEdx(int value) { Emit(0xBA); Emit(BitConverter.GetBytes(value)); }
        public void MovR8d(int value) { Emit(0x41, 0xB8); Emit(BitConverter.GetBytes(value)); }
        public void MovEax(int value) { Emit(0xB8); Emit(BitConverter.GetBytes(value)); }
        public void CallRax() => Emit(0xFF, 0xD0);
        public int EmitNearConditionalJump(byte condition) { Emit(0x0F, condition, 0, 0, 0, 0); return Position - 4; }
        public int EmitNearJump() { Emit(0xE9, 0, 0, 0, 0); return Position - 4; }
        public int EmitShortConditionalJump(byte opcode) { Emit(opcode, 0); return Position - 1; }
        public void PatchNearJump(int displacementOffset, int targetOffset)
        {
            var displacement = targetOffset - (displacementOffset + 4);
            var bytes = BitConverter.GetBytes(displacement);
            for (var index = 0; index < bytes.Length; index++) _bytes[displacementOffset + index] = bytes[index];
        }
        public void PatchShortJump(int displacementOffset, int targetOffset)
        {
            var displacement = targetOffset - (displacementOffset + 1);
            if (displacement is < sbyte.MinValue or > sbyte.MaxValue)
                throw new InvalidOperationException("生成的主线程跳板短跳转超出范围。");
            _bytes[displacementOffset] = unchecked((byte)(sbyte)displacement);
        }
        public byte[] ToArray() => _bytes.ToArray();
    }

    private sealed class BuildLayout(Il2CppRuntimeResolver runtime)
    {
        private ulong Rva(string ns, string klass, string name, bool isStatic, string result, params string[] parameters) =>
            runtime.Method("Assembly-CSharp.dll", ns, klass, name, isStatic, result, parameters).Pointer - runtime.ModuleBase;
        public ulong L10nTextGetValue => Rva("LubanDatas", "L10nText", "get_Value", false, "System.String");
        public ulong GameStoreManagerGetCurrentPlayer => Rva("Game.Model", "GameStoreManager", "get_CurrentPlayer", true, "Game.Model.PlayerModel");
        public ulong GameStoreManagerGetCurrentStore => Rva("Game.Model", "GameStoreManager", "get_CurrentStore", true, "Game.Model.GameStore");
        public ulong GameStoreSaveAutoSlotImmediately => Rva("Game.Model", "GameStore", "SaveAutoSlotImmediately", false, "System.String");
        public ulong BagSort => Rva("Game.Model.Components", "BagModel", "SortBag", false, "System.Void");
        public ulong BagRefreshRedDots => Rva("Game.Model.Components", "BagModel", "RefreshRedDots", false, "System.Void");
        public ulong PlayerSetInteractAttributeValue => Rva("Game.Model", "PlayerModel", "SetInteractAttributeValue", false, "System.Void", "LubanDatas.TbInteractAttributeId", "System.Int32");
        public ulong CombatSetBaseAttribute => Rva("Game.Model.Player.Components", "CombatModel", "SetBaseAttr", false, "System.Void", "Game.Model.Player.Components.CombatAttrId", "System.Single");
        public ulong TalentMarkSpiritRootDirty => Rva("Game.Model.Player.Components", "TalentPathModel", "MarkSpiritRootCombatEffectDirty", false, "System.Void");
        public ulong TalentRebuildSpiritRoot => Rva("Game.Model.Player.Components", "TalentPathModel", "RebuildSpiritRootCombatEffectCache", false, "System.Boolean");
        private ulong UnityMethod(string method)
        {
            try { return runtime.Method("UnityEngine.CoreModule.dll", "UnityEngine", "Application", method, true, "System.String").Pointer - runtime.ModuleBase; }
            catch (InvalidOperationException) { return 0; } // Display-only metadata is optional, not an editor capability gate.
        }
        public ulong ApplicationGetVersion => UnityMethod("get_version");
        public ulong ApplicationGetProductName => UnityMethod("get_productName");
        public ulong ApplicationGetBuildGuid => UnityMethod("get_buildGUID");
        public ulong ExecuteTasks => runtime.Method("UnityEngine.CoreModule.dll", "UnityEngine", "UnitySynchronizationContext", "ExecuteTasks", true, "System.Void").Pointer - runtime.ModuleBase;
        public byte[] ExpectedHookPrologue
        {
            get { var ip = runtime.ModuleBase + ExecuteTasks; var bytes = runtime.Read(ip, 64); return bytes[..Il2CppMainThreadHook.InstructionLength(bytes, ip)]; }
        }
    }

    private sealed record BagRow(ulong Address, int ItemId, int Count);
    private sealed record MemoryWrite(ulong Address, int Value, int Expected = 0);
    private sealed record DictionaryEntry<T>(ulong ValueAddress, T Value);
    private sealed record MainThreadRequest(
        IReadOnlyList<MemoryWrite> Writes,
        MainThreadOperation Operation,
        ulong Owner,
        int AttributeId,
        int TargetValue,
        ulong MethodInfo = 0,
        ulong DictionaryOwner = 0);

    private enum MainThreadOperation
    {
        None,
        InventoryRefresh,
        SetInteractAttribute,
        SetCombatBaseAttribute,
        SetSpiritRoot
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, nuint size, uint freeType);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtectEx(IntPtr process, IntPtr address, nuint size, uint newProtection, out uint oldProtection);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr baseAddress, byte[] buffer, nuint size, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr baseAddress, byte[] buffer, nuint size, out nuint read);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushInstructionCache(IntPtr process, IntPtr baseAddress, nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
