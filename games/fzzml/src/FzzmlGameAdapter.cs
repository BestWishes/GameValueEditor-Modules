using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using GameValueEditor.ModuleSdk;
using GameValueEditor.Modules.Ui;
using GameValueEditor.Modules.Runtime;

namespace GameValueEditor.Modules.Fzzml;

public sealed partial class FzzmlGameAdapter :
    IInventoryGameAdapter,
    ICharacterAttributesGameAdapter,
    ICoordinatedGameEditorPageProvider,
    IGameCompatibilityDiagnosticsProvider
{
    private const string InventoryEditorId = "game.fzzml.inventory";
    private static readonly object WriteGate = new();

    public string Id => "game.fzzml";
    public string DisplayName => "放置斩魔录专属修改模块";
    public string Description => "提供背包物品与人物属性两项专属编辑功能。";
    public IReadOnlyList<string> LegacyIds => ["game.fzzml.inventory.v1"];
    public IReadOnlyList<GameEditorDescriptor> Editors { get; } =
    [
        new(InventoryEditorId, "背包物品", GameEditorKind.Custom, 100,
            "实时读取和修改背包物品总数，并调用游戏自身保存流程。"),
        new(CharacterEditorId, "人物属性", GameEditorKind.Custom, 200,
            "按人物稳定 ID 修改当前运行中的五维属性；关闭游戏后失效。", SessionOnly: true)
    ];
    public IGameEditorPage CreateEditorPage(string editorId, GameEditorPageContext context) => editorId switch
    {
        InventoryEditorId => new InventoryEditorPage(this, context,
            "实时读取和修改背包物品总数，并调用游戏自身保存流程。"),
        CharacterEditorId => new CharacterEditorPage(this, context, CharacterEditorId,
            "按人物稳定 ID 修改当前运行中的五维属性；关闭游戏后失效。"),
        _ => throw new InvalidOperationException($"放置斩魔录模块没有页面：{editorId}。")
    };

    public bool Supports(GameProcessContext process, GameBuildIdentity fingerprint) =>
        Il2CppRuntimeResolver.IsNamedGame(process, "fzzml", "放置斩魔录") &&
        File.Exists(Path.Combine(Path.GetDirectoryName(process.ExecutablePath)!, "GameAssembly.dll"));

    public IReadOnlyList<GameCompatibilityDiagnostic> GetCompatibilityDiagnostics(GameProcessContext process, GameBuildIdentity fingerprint) =>
    [
        new("游戏名称", Supports(process, fingerprint) ? GameCompatibilityDiagnosticStatus.Passed : GameCompatibilityDiagnosticStatus.Failed,
            "放置斩魔录 / fzzml；不以历史文件哈希拒绝小更新。"),
        new("当前运行数据定位", GameCompatibilityDiagnosticStatus.Information,
            "背包和人物分别从当前 IL2CPP 元数据解析字段及完整方法签名；诊断没有执行数值修改。"),
        new("人物属性", GameCompatibilityDiagnosticStatus.Information, "五维修改仍仅本次运行有效，关闭游戏后失效。")
    ];

    public AdapterFieldValue ReadField(GameProcessContext process, string fieldKey)
    {
        if (TryParseCharacterFieldKey(fieldKey, out var characterId, out var attributeKey))
        {
            var character = ReadCharacters(process).Single(item =>
                string.Equals(item.CharacterId, characterId, StringComparison.Ordinal));
            var attribute = character.Attributes.Single(item =>
                string.Equals(item.Key, attributeKey, StringComparison.Ordinal));
            return new AdapterFieldValue(fieldKey, attribute.RawValueDisplay,
                $"当前运行有效 · 游戏界面总值 {attribute.AggregatedValueDisplay}");
        }
        using var session = new Session(process);
        var inventory = session.FindInventoryRows(fieldKey);
        return new AdapterFieldValue(
            fieldKey,
            inventory.TotalCount.ToString(CultureInfo.InvariantCulture),
            $"实时定位可用 · {inventory.Rows.Count} 条后台记录");
    }

    public IReadOnlyList<AdapterInventoryItem> ReadInventory(GameProcessContext process)
    {
        using var session = new Session(process);
        return session.ReadInventoryItems();
    }

    public AdapterFieldValue WriteField(GameProcessContext process, string fieldKey, string displayValue)
    {
        if (TryParseCharacterFieldKey(fieldKey, out var characterId, out var attributeKey))
        {
            if (!int.TryParse(displayValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetValue) || targetValue < 0)
                throw new InvalidOperationException("人物属性必须是 0 到 2147483647 之间的整数。");
            var character = WriteCharacterAttribute(process, characterId, attributeKey, targetValue);
            var attribute = character.Attributes.Single(item => string.Equals(item.Key, attributeKey, StringComparison.Ordinal));
            return new AdapterFieldValue(fieldKey, attribute.RawValueDisplay,
                $"已实时修改 · 游戏界面总值 {attribute.AggregatedValueDisplay} · 关闭游戏后失效");
        }
        if (!int.TryParse(displayValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var newValue) || newValue < 0)
            throw new InvalidOperationException("背包数量必须是 0 到 2147483647 之间的整数。");

        lock (WriteGate)
        {
            using var session = new Session(process);
            var updated = session.WriteInventoryTotal(fieldKey, newValue);
            return new AdapterFieldValue(
                fieldKey,
                updated.TotalCount.ToString(CultureInfo.InvariantCulture),
                $"已实时写入并保存 · {updated.Rows.Count} 条后台记录");
        }
    }

    private sealed class Session : IDisposable
    {
        private const uint ProcessAccess = 0x0002 | 0x0008 | 0x0010 | 0x0020 | 0x0400;
        private const uint MemRelease = 0x8000;
        private const uint PageExecuteReadWrite = 0x40;
        private readonly Process _process;
        private readonly IntPtr _handle;
        private readonly ulong _moduleBase;
        private readonly ulong _saveManager;
        private readonly BuildLayout _layout;
        private readonly Il2CppRuntimeResolver _runtime;
        private readonly ulong _stringNew;
        private readonly ulong _writeBarrierSetField;

        public Session(GameProcessContext context)
        {
            _process = Process.GetProcessById(context.ProcessId);
            _handle = OpenProcess(ProcessAccess, false, context.ProcessId);
            if (_handle == IntPtr.Zero) { _process.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), "无法连接放置斩魔录进程。"); }
            try
            {
                _runtime = new Il2CppRuntimeResolver(context);
                _moduleBase = _runtime.ModuleBase;
                _layout = new BuildLayout(_runtime);
                _stringNew = _runtime.Export("il2cpp_string_new");
                _writeBarrierSetField = _runtime.Export("il2cpp_gc_wbarrier_set_field");
                _saveManager = _runtime.Call(_moduleBase + _layout.SaveManagerGetInstance);
                if (_saveManager == 0) throw new InvalidOperationException("当前存档尚未加载，请进入存档后刷新。");
            }
            catch { _runtime?.Dispose(); CloseHandle(_handle); _process.Dispose(); throw; }
        }

        public InventoryMatch FindInventoryRows(string itemName)
        {
            if (string.IsNullOrWhiteSpace(itemName)) throw new InvalidOperationException("游戏内物品名不能为空。");
            var snapshot = ReadInventoryRows();
            var matches = snapshot.Rows.Where(row => string.Equals(row.ItemName, itemName.Trim(), StringComparison.Ordinal)).ToList();
            if (matches.Count == 0) throw new InvalidOperationException($"当前背包中没有找到“{itemName}”。");
            return new InventoryMatch(snapshot.RowsAddress, matches);
        }

        public IReadOnlyList<AdapterInventoryItem> ReadInventoryItems()
        {
            var snapshot = ReadInventoryRows();
            return snapshot.Rows
                .GroupBy(row => row.ItemName, StringComparer.Ordinal)
                .Select(group => new AdapterInventoryItem(
                    group.Key,
                    group.Key,
                    group.Sum(row => (long)row.Count)))
                .OrderBy(item => item.DisplayName, StringComparer.CurrentCulture)
                .ToList();
        }

        private InventorySnapshot ReadInventoryRows()
        {
            var allData = ReadUInt64(_saveManager + _layout.SaveManagerSnapshotOffset);
            if (allData == 0) throw new InvalidOperationException("当前存档快照尚未加载。");
            var rows = _runtime.Reference(allData, "inventoryRows", RowsType);
            if (rows == 0) throw new InvalidOperationException("当前背包列表尚未加载。");
            var outerArray = _runtime.ListItems(rows);
            var rowCount = _runtime.ListCount(rows);
            if (outerArray == 0 || rowCount is < 0 or > 100_000)
                throw new InvalidDataException($"背包列表结构无效（行数 {rowCount}）。");

            var activeRows = new List<ActiveRow>();
            for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                var row = ReadUInt64(outerArray + 0x20UL + (ulong)rowIndex * 8);
                if (row == 0) continue;
                var rowArray = _runtime.ListItems(row);
                var columnCount = _runtime.ListCount(row);
                if (rowArray == 0 || columnCount < 3) continue;

                var name = ReadManagedString(ReadUInt64(rowArray + 0x28));
                if (string.IsNullOrWhiteSpace(name)) continue;

                var slotId = ReadManagedString(ReadUInt64(rowArray + 0x20));
                var countPointerSlot = rowArray + 0x30;
                var countText = ReadManagedString(ReadUInt64(countPointerSlot));
                if (!int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
                    throw new InvalidDataException($"物品“{name}”的数量“{countText}”不是整数。");
                activeRows.Add(new ActiveRow(rows, rowIndex, rowArray, countPointerSlot, slotId, name, count));
            }
            return new InventorySnapshot(rows, activeRows);
        }

        public InventoryMatch WriteInventoryTotal(string itemName, int newValue)
        {
            var inventory = FindInventoryRows(itemName);
            var snapshot = ReadUInt64(_saveManager + _layout.SaveManagerSnapshotOffset);
            var rowsPointer = _runtime.Address(snapshot, "inventoryRows", RowsType);
            if (ReadUInt64(rowsPointer) != inventory.RowsAddress)
                throw new InvalidOperationException("当前存档已切换，请刷新后重试；未执行修改。");
            var rowUpdates = new List<RowUpdate>(inventory.Rows.Count);
            var orderedRows = inventory.Rows.OrderBy(candidate => candidate.RowIndex).ToList();
            for (var index = 0; index < orderedRows.Count; index++)
            {
                rowUpdates.Add(new RowUpdate(orderedRows[index], index == 0 ? newValue : 0));
            }
            var hookAddress = _moduleBase + _layout.ExecuteTasks;
            var expected = _layout.ExpectedHookPrologue;
            var original = Read(hookAddress, expected.Length);
            if (!original.AsSpan().SequenceEqual(expected))
                throw new InvalidDataException("游戏主线程入口正在变化，请稍后重试；未执行修改。");

            var codeCapacity = checked(4096 + rowUpdates.Count * 128);
            var textBase = (ulong)codeCapacity;
            var resultBase = textBase + (ulong)rowUpdates.Count * 0x20;
            var dataBase = resultBase + (ulong)rowUpdates.Count * 8;
            var allocationSize = checked((int)dataBase + 512);
            var remote = Il2CppMainThreadHook.AllocateNear(_handle, hookAddress, allocationSize);
            var remoteBase = unchecked((ulong)remote.ToInt64());
            var stringWrites = rowUpdates.Select((update, index) => new ManagedStringWrite(
                update.Row.RowArrayAddress,
                update.Row.CountPointerSlotAddress,
                remoteBase + textBase + (ulong)index * 0x20UL,
                remoteBase + resultBase + (ulong)index * 8UL,
                update.Count,
                ReadUInt64(update.Row.CountPointerSlotAddress))).ToArray();
            var slotTextAddress = remoteBase + dataBase;
            var slotStringResultAddress = remoteBase + dataBase + 0x80;
            var saveResultAddress = remoteBase + dataBase + 0x90;
            var statusAddress = remoteBase + dataBase + 0x98;
            var canFreeRemote = true;

            try
            {
                foreach (var write in stringWrites)
                    Write(write.TextAddress, Encoding.UTF8.GetBytes(write.Count.ToString(CultureInfo.InvariantCulture) + "\0"));
                Write(slotTextAddress, Encoding.UTF8.GetBytes("player.inventory.2d\0"));
                var trampoline = BuildMainThreadTrampoline(new HookParameters(
                    HookAddress: hookAddress,
                    ContinuationAddress: remoteBase + (ulong)codeCapacity - 0x400,
                    SaveManager: _saveManager,
                    SnapshotPointerAddress: _saveManager + _layout.SaveManagerSnapshotOffset,
                    Snapshot: snapshot,
                    RowsPointerAddress: rowsPointer,
                    Rows: inventory.RowsAddress,
                    CountWrites: stringWrites,
                    SlotTextAddress: slotTextAddress,
                    SlotStringResultAddress: slotStringResultAddress,
                    SaveResultAddress: saveResultAddress,
                    StatusAddress: statusAddress,
                    StringNew: _stringNew,
                    WriteBarrierSetField: _writeBarrierSetField,
                    CleanupInventoryRows: _moduleBase + _layout.CleanupInventoryRows,
                    SetInventoryCacheRows: _moduleBase + _layout.SetInventoryCacheRows,
                    ClearPendingInventory2DDeltaState: _moduleBase + _layout.ClearPendingInventory2DDeltaState,
                    RequestSaveInventory2D: _moduleBase + _layout.RequestSaveInventory2D,
                    FlushPendingInventory2DSave: _moduleBase + _layout.FlushPendingInventory2DSave,
                    SaveInventory2DBinary: _moduleBase + _layout.SaveInventory2DBinary));
                if (trampoline.Length >= codeCapacity - 0x400) throw new InvalidDataException("背包操作代码超出缓冲区。");
                Write(remoteBase + (ulong)codeCapacity - 0x400,
                    Il2CppMainThreadHook.Relocate(original, hookAddress, remoteBase + (ulong)codeCapacity - 0x400));
                Write(remoteBase, trampoline);

                if (!VirtualProtectEx(_handle, (IntPtr)(long)hookAddress, (nuint)original.Length, PageExecuteReadWrite, out var oldProtection))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法临时写入游戏主线程入口。");

                var patched = false;
                try
                {
                    var patch = BuildAbsoluteJump(remoteBase, original.Length);
                    canFreeRemote = false; // A game thread may already have fetched the trampoline.
                    patched = true;
                    Il2CppMainThreadHook.WritePatch(_process, _handle, hookAddress, patch);
                    FlushInstructionCache(_handle, (IntPtr)(long)hookAddress, (nuint)patch.Length);

                    var stopwatch = Stopwatch.StartNew();
                    var status = 0;
                    while (status is 0 or 2 && stopwatch.Elapsed < TimeSpan.FromSeconds(5))
                    {
                        Thread.Sleep(10);
                        status = ReadInt32(statusAddress);
                    }

                    if (status == 4) throw new InvalidOperationException("当前存档或背包在操作前已变化，请刷新后重试；未执行修改。");
                    if (status != 1)
                    {
                        canFreeRemote = false;
                        throw new TimeoutException(status == 2
                            ? "游戏主线程已进入修改流程，但未在五秒内完成；为保护进程，本次远程代码内存将保留。"
                            : "游戏主线程五秒内没有执行修改流程。");
                    }

                    if (ReadInt32(saveResultAddress) == 0)
                        throw new InvalidOperationException("游戏自身的 SaveInventory2D_Binary 返回失败。");
                }
                finally
                {
                    if (patched)
                    {
                        Il2CppMainThreadHook.WritePatch(_process, _handle, hookAddress, original);
                        FlushInstructionCache(_handle, (IntPtr)(long)hookAddress, (nuint)original.Length);
                    }
                    VirtualProtectEx(_handle, (IntPtr)(long)hookAddress, (nuint)original.Length, oldProtection, out _);
                }

                Thread.Sleep(250);
                InventoryMatch updated;
                try
                {
                    updated = FindInventoryRows(itemName);
                }
                catch (InvalidOperationException) when (newValue == 0)
                {
                    updated = new InventoryMatch(inventory.RowsAddress, []);
                }
                if (updated.TotalCount != newValue)
                    throw new InvalidOperationException($"实时缓存复核失败：预期后台总数 {newValue}，实际 {updated.TotalCount}。");
                return updated;
            }
            finally
            {
                if (canFreeRemote) VirtualFreeEx(_handle, remote, 0, MemRelease);
            }
        }


        private string ReadManagedString(ulong address)
        {
            if (address == 0) return string.Empty;
            var length = ReadInt32(address + 0x10);
            if (length is < 0 or > 1_000_000) throw new InvalidDataException($"IL2CPP 字符串长度无效：{length}。");
            return Encoding.Unicode.GetString(Read(address + 0x14, checked(length * 2)));
        }


        private int ReadInt32(ulong address) => BitConverter.ToInt32(Read(address, 4));
        private ulong ReadUInt64(ulong address) => BitConverter.ToUInt64(Read(address, 8));

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

        private static byte[] BuildMainThreadTrampoline(HookParameters p)
        {
            var code = new Emitter();
            var stale = new List<int>();
            foreach (var guard in new[] { (p.SnapshotPointerAddress, p.Snapshot), (p.RowsPointerAddress, p.Rows) }
                .Concat(p.CountWrites.Select(write => (write.PointerSlot, write.ExpectedPointer))))
            {
                code.MovRax(guard.Item1); code.MovRdx(guard.Item2); code.Emit(0x48, 0x39, 0x10);
                stale.Add(code.EmitNearConditionalJump(0x85));
            }
            foreach (var write in p.CountWrites)
            {
                code.MovRcx(write.TextAddress);
                code.MovRax(p.StringNew); code.CallRax();
                code.MovRdx(write.ResultAddress); code.Emit(0x48, 0x89, 0x02);
                code.MovRcx(write.RowArray);
                code.MovRdx(write.PointerSlot);
                code.Emit(0x49, 0x89, 0xC0);
                code.MovRax(p.WriteBarrierSetField); code.CallRax();
            }

            code.MovRcx(p.Rows);
            code.Emit(0x33, 0xD2);
            code.MovRax(p.CleanupInventoryRows); code.CallRax();

            code.MovRcx(p.SlotTextAddress);
            code.MovRax(p.StringNew); code.CallRax();
            code.MovRdx(p.SlotStringResultAddress); code.Emit(0x48, 0x89, 0x02);

            code.Emit(0x33, 0xC0);
            code.Emit(0x48, 0x89, 0x44, 0x24, 0x20);
            code.Emit(0x48, 0x89, 0x44, 0x24, 0x28);
            code.Emit(0x48, 0x89, 0x44, 0x24, 0x30);
            code.Emit(0x48, 0x89, 0x44, 0x24, 0x38);
            code.Emit(0xC6, 0x44, 0x24, 0x28, 0x01);
            code.MovRcx(p.SaveManager);
            code.MovRdx(p.Rows);
            code.Emit(0x41, 0xB8, 0x01, 0x00, 0x00, 0x00);
            code.Emit(0x41, 0xB9, 0x01, 0x00, 0x00, 0x00);
            code.MovRax(p.SetInventoryCacheRows); code.CallRax();

            code.MovRcx(p.SaveManager);
            code.MovRax(p.SlotStringResultAddress); code.Emit(0x48, 0x8B, 0x10);
            code.MovRax(p.ClearPendingInventory2DDeltaState); code.CallRax();

            code.MovRcx(p.SaveManager);
            code.MovRax(p.SlotStringResultAddress); code.Emit(0x48, 0x8B, 0x10);
            code.MovR8(p.Rows);
            code.Emit(0x45, 0x33, 0xC9);
            code.MovRax(p.RequestSaveInventory2D); code.CallRax();
            code.MovRdx(p.SaveResultAddress); code.Emit(0x89, 0x02);

            code.MovRcx(p.SaveManager);
            code.MovRax(p.SlotStringResultAddress); code.Emit(0x48, 0x8B, 0x10);
            code.Emit(0x45, 0x33, 0xC0);
            code.MovRax(p.FlushPendingInventory2DSave); code.CallRax();

            code.MovRcx(p.SaveManager);
            code.MovRdx(p.Rows);
            code.Emit(0x45, 0x33, 0xC0);
            code.Emit(0x45, 0x33, 0xC9);
            code.MovRax(p.SaveInventory2DBinary); code.CallRax();
            code.Emit(0x0F, 0xB6, 0xC0);
            code.MovRdx(p.SaveResultAddress); code.Emit(0x89, 0x02);
            code.MovRdx(p.StatusAddress); code.Emit(0xC7, 0x02, 0x01, 0x00, 0x00, 0x00);
            code.Emit(0xE9, 0, 0, 0, 0);
            var success = code.Position - 4;
            foreach (var jump in stale) code.PatchNearJump(jump, code.Position);
            code.MovRdx(p.StatusAddress); code.Emit(0xC7, 0x02, 0x04, 0x00, 0x00, 0x00);
            code.PatchNearJump(success, code.Position);

            return Il2CppMainThreadHook.Wrap(code.ToArray(), p.StatusAddress, p.ContinuationAddress);
        }

        private static byte[] BuildAbsoluteJump(ulong target, int length) => Il2CppMainThreadHook.Jump(target, length);
    }

    private sealed class Emitter
    {
        private readonly List<byte> _bytes = [];
        public int Position => _bytes.Count;
        public void Emit(params byte[] bytes) => _bytes.AddRange(bytes);
        public void MovRax(ulong value) { Emit(0x48, 0xB8); _bytes.AddRange(BitConverter.GetBytes(value)); }
        public void MovRcx(ulong value) { Emit(0x48, 0xB9); _bytes.AddRange(BitConverter.GetBytes(value)); }
        public void MovRdx(ulong value) { Emit(0x48, 0xBA); _bytes.AddRange(BitConverter.GetBytes(value)); }
        public void MovR8(ulong value) { Emit(0x49, 0xB8); _bytes.AddRange(BitConverter.GetBytes(value)); }
        public void CallRax() => Emit(0xFF, 0xD0);
        public void JumpRax() => Emit(0xFF, 0xE0);
        public int EmitNearConditionalJump(byte condition)
        {
            Emit(0x0F, condition, 0, 0, 0, 0);
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

    private sealed record ActiveRow(
        ulong RowsAddress,
        int RowIndex,
        ulong RowArrayAddress,
        ulong CountPointerSlotAddress,
        string SlotId,
        string ItemName,
        int Count);

    private sealed record InventorySnapshot(ulong RowsAddress, IReadOnlyList<ActiveRow> Rows);

    private sealed record InventoryMatch(ulong RowsAddress, IReadOnlyList<ActiveRow> Rows)
    {
        public long TotalCount => Rows.Sum(row => (long)row.Count);
    }

    private sealed record RowUpdate(ActiveRow Row, int Count);
    private sealed record ManagedStringWrite(
        ulong RowArray,
        ulong PointerSlot,
        ulong TextAddress,
        ulong ResultAddress,
        int Count,
        ulong ExpectedPointer);

    private sealed record HookParameters(
        ulong HookAddress,
        ulong ContinuationAddress,
        ulong SaveManager,
        ulong SnapshotPointerAddress,
        ulong Snapshot,
        ulong RowsPointerAddress,
        ulong Rows,
        IReadOnlyList<ManagedStringWrite> CountWrites,
        ulong SlotTextAddress,
        ulong SlotStringResultAddress,
        ulong SaveResultAddress,
        ulong StatusAddress,
        ulong StringNew,
        ulong WriteBarrierSetField,
        ulong CleanupInventoryRows,
        ulong SetInventoryCacheRows,
        ulong ClearPendingInventory2DDeltaState,
        ulong RequestSaveInventory2D,
        ulong FlushPendingInventory2DSave,
        ulong SaveInventory2DBinary);

    private const string RowsType = "System.Collections.Generic.List<System.Collections.Generic.List<System.String>>";
    private sealed class BuildLayout(Il2CppRuntimeResolver runtime)
    {
        private const string SaveNamespace = "Script.Manager.Save";
        private const string SaveType = "Script.Manager.Save.SaveManager";
        private ulong Rva(string ns, string type, string method, bool isStatic, string result, params string[] args) =>
            runtime.Method("Assembly-CSharp.dll", ns, type, method, isStatic, result, args).Pointer - runtime.ModuleBase;
        public ulong SaveManagerGetInstance => Rva(SaveNamespace, "SaveManager", "get_Instance", true, SaveType);
        public ulong SaveManagerSnapshotOffset => runtime.Field(runtime.Class("Assembly-CSharp.dll", SaveNamespace, "SaveManager"), "_cachedSnapshot", SaveType + "/AllParsedData");
        public ulong CleanupInventoryRows => Rva("Script.Systems.StageProgression", "MaterialDungeonChallengeCostService", "CleanupInventoryRows", true, "System.Void", RowsType);
        public ulong SetInventoryCacheRows => Rva(SaveNamespace, "SaveManager", "SetInventoryCacheRows", false, "System.Void",
            RowsType, "System.Boolean", "System.Boolean", "System.Collections.Generic.IReadOnlyCollection<System.Int32>", "System.Boolean");
        public ulong ClearPendingInventory2DDeltaState => Rva(SaveNamespace, "SaveManager", "ClearPendingInventory2DDeltaState", false, "System.Void", "System.String");
        public ulong RequestSaveInventory2D => Rva(SaveNamespace, "SaveManager", "RequestSaveInventory2D", false, "System.Boolean", "System.String", RowsType);
        public ulong FlushPendingInventory2DSave => Rva(SaveNamespace, "SaveManager", "FlushPendingInventory2DSave", false, "System.Void", "System.String");
        public ulong SaveInventory2DBinary => Rva(SaveNamespace, "SaveManager", "SaveInventory2D_Binary", false, "System.Boolean", RowsType, "Script.Manager.Save.SaveStorageCodec/SaveWriteDurability");
        public ulong ExecuteTasks => runtime.Method("UnityEngine.CoreModule.dll", "UnityEngine", "UnitySynchronizationContext", "ExecuteTasks", true, "System.Void").Pointer - runtime.ModuleBase;
        public byte[] ExpectedHookPrologue
        {
            get { var ip = runtime.ModuleBase + ExecuteTasks; var bytes = runtime.Read(ip, 64); return bytes[..Il2CppMainThreadHook.InstructionLength(bytes, ip)]; }
        }
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
