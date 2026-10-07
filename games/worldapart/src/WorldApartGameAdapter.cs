using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using GameValueEditor.ModuleSdk;
using GameValueEditor.Modules.Ui;

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
    private static readonly ConcurrentDictionary<string, BuildLayout> LayoutCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyList<BuildLayout> SupportedBuilds =
    [
        new(
            "35369BA362352B5A80A2E5844CD93F5A5FFFD18CEE61EB4861B9F4E920CDE835",
            "E4BFA837BD5F43BF5FFBE3E28C80B40CD3E20B24CE63941CE2280874DF2FA056",
            "55F65FE395395CAA4C3C8DE6F874107AB92742CA638F1E0F77ECB609A22154FA",
            TablesGetCurrent: 0x14B3DB0,
            L10nTextGetValue: 0x18CA6B0,
            GameStoreManagerGetCurrentPlayer: 0xDEBE90,
            GameStoreManagerGetCurrentStore: 0xDEBF80,
            GameStoreSaveAutoSlotImmediately: 0xDEF5D0,
            BagSort: 0xEA1930,
            BagRefreshRedDots: 0xEA06E0,
            AudioUpdate: 0x148E6E0,
            AudioServiceUpdate: 0x7AC3D0,
            RaiseNullReference: 0x5DE3B0,
            ExpectedAudioUpdate: Convert.FromHexString(
                "4883EC28488B49104885C9740B33D24883C428E9D8DC31FFE8B3FC14FF"),
            PlayerSetInteractAttributeValue: 0xDC5EE0,
            CombatSetBaseAttribute: 0xE67710,
            TalentMarkSpiritRootDirty: 0xE97860,
            TalentRebuildSpiritRoot: 0xE979C0,
            IntDictionarySetItem: 0x4ED6B60,
            ApplicationGetVersion: 0,
            ApplicationGetProductName: 0,
            ApplicationGetBuildGuid: 0),
        new(
            "35369BA362352B5A80A2E5844CD93F5A5FFFD18CEE61EB4861B9F4E920CDE835",
            "EDE8A956051C0C831F79E0874AFF28297F41D3FA18C18AD2CA2FF16C33D0938D",
            "BBECA25F98CFC56BFE48A5BD6DC90B9AADFEB1A6BB8F0DC03CA8DA2EF26DBDBC",
            TablesGetCurrent: 0x14E74D0,
            L10nTextGetValue: 0x1A1F3A0,
            GameStoreManagerGetCurrentPlayer: 0xE2DF40,
            GameStoreManagerGetCurrentStore: 0xE2E030,
            GameStoreSaveAutoSlotImmediately: 0xE31680,
            BagSort: 0xEDA1E0,
            BagRefreshRedDots: 0xED8F80,
            AudioUpdate: 0x14D3660,
            AudioServiceUpdate: 0x7AC3D0,
            RaiseNullReference: 0x5DE430,
            ExpectedAudioUpdate: Convert.FromHexString(
                "4883EC28488B49104885C9740B33D24883C428E9588D2DFFE8B3AD10FF"),
            PlayerSetInteractAttributeValue: 0xDF50C0,
            CombatSetBaseAttribute: 0xE9F740,
            TalentMarkSpiritRootDirty: 0xEC82B0,
            TalentRebuildSpiritRoot: 0xEC8410,
            IntDictionarySetItem: 0x4EE3C10,
            ApplicationGetVersion: 0x631D330,
            ApplicationGetProductName: 0x631D1E0,
            ApplicationGetBuildGuid: 0x631CF90)
    ];

    public string Id => "game.worldapart";
    public string DisplayName => "WorldApart 专属修改模块";
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
        TryResolveLayout(process, fingerprint, out _);

    public IReadOnlyList<GameCompatibilityDiagnostic> GetCompatibilityDiagnostics(
        GameProcessContext process,
        GameBuildIdentity fingerprint)
    {
        var supported = Supports(process, fingerprint);
        return
        [
            new("完整运行构建指纹",
                supported ? GameCompatibilityDiagnosticStatus.Passed : GameCompatibilityDiagnosticStatus.Failed,
                supported ? "当前构建匹配已验证的 GameAssembly 与 metadata 完整指纹组合；实际读取前还会核对运行进程。" :
                    "当前运行文件组合未验证；不会依据短函数签名套用旧偏移。"),
            new("模块自有页面", GameCompatibilityDiagnosticStatus.Information,
                $"已注册 {Editors.Count} 个独立 WPF 页面；诊断未执行页面读写。")
        ];
    }

    public GameDeclaredVersionInfo ReadGameVersionMetadata(GameProcessContext process)
    {
        using var session = new Session(process, ResolveLayout(process), initializeRoots: false);
        return session.ReadGameDeclaredVersion();
    }

    public IReadOnlyList<AdapterInventoryItem> ReadInventory(GameProcessContext process)
    {
        using var session = new Session(process, ResolveLayout(process));
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
            using var session = new Session(process, ResolveLayout(process));
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
                using var session = new Session(process, ResolveLayout(process));
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

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool TryResolveLayout(
        GameProcessContext process,
        GameBuildIdentity fingerprint,
        out BuildLayout layout)
    {
        layout = SupportedBuilds.FirstOrDefault(candidate => candidate.Matches(
            fingerprint.ExecutableSha256,
            fingerprint.GameAssemblySha256,
            fingerprint.MetadataSha256))!;
        if (layout is not null) return true;
        // Third-party distributions commonly replace only the launcher EXE. The IL2CPP code and
        // metadata are the actual layout authority, so an unchanged pair is safe even when the
        // executable hash differs or the install directory has moved.
        layout = SupportedBuilds.FirstOrDefault(candidate => candidate.MatchesRuntimeContent(
            fingerprint.GameAssemblySha256,
            fingerprint.MetadataSha256))!;
        return layout is not null;
    }

    private static BuildLayout ResolveLayout(GameProcessContext process)
    {
        try
        {
            var root = Path.GetDirectoryName(process.ExecutablePath) ?? string.Empty;
            var executableName = Path.GetFileNameWithoutExtension(process.ExecutablePath);
            var assembly = Path.Combine(root, "GameAssembly.dll");
            var metadata = ResolveMetadataPath(root, executableName);
            using var actualProcess = Process.GetProcessById(process.ProcessId);
            var startTime = actualProcess.StartTime.ToUniversalTime();
            if (startTime != process.StartTimeUtc.ToUniversalTime() || actualProcess.HasExited ||
                !SamePath(actualProcess.MainModule?.FileName, process.ExecutablePath))
                throw new InvalidOperationException("WorldApart 进程身份已变化，请重新连接游戏。");
            var loadedAssembly = actualProcess.Modules.Cast<ProcessModule>().SingleOrDefault(module =>
                module.ModuleName.Equals("GameAssembly.dll", StringComparison.OrdinalIgnoreCase));
            if (!SamePath(loadedAssembly?.FileName, assembly))
                throw new InvalidOperationException("运行进程加载的 GameAssembly 与待验证文件不一致。");
            var paths = new[] { process.ExecutablePath, assembly, metadata };
            var before = paths.Select(ReadStamp).ToArray();
            var cacheKey = $"{process.ProcessId}|{startTime.Ticks}|{string.Join<FileStamp>("|", before)}";
            if (LayoutCache.Count >= 32) LayoutCache.Clear();
            var verified = LayoutCache.GetOrAdd(cacheKey, _ =>
            {
                var executableHash = ComputeSha256(process.ExecutablePath);
                var assemblyHash = ComputeSha256(assembly);
                var metadataHash = ComputeSha256(metadata);
                var identity = new GameBuildIdentity(executableHash, string.Empty, assemblyHash, metadataHash);
                if (!before.SequenceEqual(paths.Select(ReadStamp)) || actualProcess.HasExited)
                    throw new InvalidOperationException("构建验证期间文件或进程已变化，已停止操作。");
                if (TryResolveLayout(process, identity, out var layout)) return layout;
                throw new InvalidOperationException(
                    "当前 WorldApart 构建尚未通过专属修改布局校验。为避免误写，未知版本不会套用旧偏移。");
            });
            if (!before.SequenceEqual(paths.Select(ReadStamp)) || actualProcess.HasExited)
                throw new InvalidOperationException("已验证构建文件或进程已变化，已停止操作。");
            return verified;
        }
        catch (FileNotFoundException exception)
        {
            throw new InvalidOperationException("当前 WorldApart 安装缺少专属修改所需的 IL2CPP 文件。", exception);
        }
    }

    private static string ResolveMetadataPath(string root, string executableName)
    {
        var preferred = Path.Combine(root, $"{executableName}_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        if (File.Exists(preferred)) return preferred;
        var known = Path.Combine(root, "WorldApart_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        if (File.Exists(known)) return known;
        if (!Directory.Exists(root)) return preferred;
        var candidates = Directory.EnumerateDirectories(root, "*_Data", SearchOption.TopDirectoryOnly)
            .Select(directory => Path.Combine(directory, "il2cpp_data", "Metadata", "global-metadata.dat"))
            .Where(File.Exists)
            .Take(2)
            .ToList();
        return candidates.Count == 1 ? candidates[0] : preferred;
    }

    private static bool SamePath(string? left, string right) => left is not null &&
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static FileStamp ReadStamp(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("构建文件缺失或为链接，已停止操作。");
        return new(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private sealed record FileStamp(string Path, long Length, long ModifiedTicks);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr handle, out long creation, out long exit, out long kernel, out long user);

    private sealed partial class Session : IDisposable
    {
        private const uint ProcessAccess = 0x0002 | 0x0008 | 0x0010 | 0x0020 | 0x0400;
        private const uint MemCommitReserve = 0x1000 | 0x2000;
        private const uint MemRelease = 0x8000;
        private const uint PageExecuteReadWrite = 0x40;
        private const uint Infinite = 0xFFFFFFFF;

        private readonly Process _process;
        private readonly IntPtr _handle;
        private readonly ulong _moduleBase;
        private readonly BuildLayout _layout;
        private readonly ulong _domainGet;
        private readonly ulong _threadAttach;
        private readonly ulong _threadDetach;
        private readonly ulong _classGetMethodFromName;
        private ulong _player;
        private ulong _tables;

        public Session(GameProcessContext context, BuildLayout layout, bool initializeRoots = true)
        {
            var processId = context.ProcessId;
            _layout = layout;
            _process = Process.GetProcessById(processId);
            var module = _process.Modules.Cast<ProcessModule>().SingleOrDefault(candidate =>
                string.Equals(candidate.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("目标进程没有加载 GameAssembly.dll。请选择 WorldApart 的实际游戏进程。");
            _moduleBase = unchecked((ulong)module.BaseAddress.ToInt64());
            _handle = OpenProcess(ProcessAccess, false, processId);
            if (_handle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "无法连接 WorldApart 进程。可以尝试以管理员身份运行本应用。");

            try
            {
                if (!GetProcessTimes(_handle, out var created, out _, out _, out _) ||
                    DateTime.FromFileTimeUtc(created) != context.StartTimeUtc.ToUniversalTime() ||
                    !SamePath(module.FileName, Path.Combine(Path.GetDirectoryName(context.ExecutablePath)!, "GameAssembly.dll")))
                    throw new InvalidOperationException("WorldApart 进程实例或加载文件已变化，请重新连接。");
                var exports = PortableExportResolver.Read(module.FileName);
                _domainGet = _moduleBase + exports.GetRequired("il2cpp_domain_get");
                _threadAttach = _moduleBase + exports.GetRequired("il2cpp_thread_attach");
                _threadDetach = _moduleBase + exports.GetRequired("il2cpp_thread_detach");
                _classGetMethodFromName = _moduleBase + exports.GetRequired("il2cpp_class_get_method_from_name");
                if (initializeRoots) RefreshRoots();
            }
            catch
            {
                CloseHandle(_handle);
                _process.Dispose();
                throw;
            }
        }

        private void RefreshRoots()
        {
            _player = CallPointerFunction(_moduleBase + _layout.GameStoreManagerGetCurrentPlayer);
            if (_player == 0) throw new InvalidOperationException("当前玩家存档尚未加载。请进入可操作的游戏存档后重试。");
            _tables = CallPointerFunction(_moduleBase + _layout.TablesGetCurrent);
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
            var bag = ReadUInt64(_player + 0x40);
            if (bag == 0) throw new InvalidOperationException("当前背包尚未加载。");
            var rows = ReadBagRows().Where(row => row.ItemId == itemId).OrderBy(row => row.Address).ToList();
            if (rows.Count == 0)
                throw new InvalidOperationException($"当前背包中没有物品 ID {itemId}。本版本暂不创建从未获得过的物品。");

            var writes = rows.Select((row, index) => new MemoryWrite(row.Address + 0x14, index == 0 ? targetValue : 0)).ToArray();
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
            var bag = ReadUInt64(_player + 0x40);
            if (bag == 0) throw new InvalidOperationException("当前背包尚未加载。");
            var list = ReadUInt64(bag + 0x18);
            if (list == 0) throw new InvalidOperationException("当前背包物品列表尚未加载。");
            var array = ReadUInt64(list + 0x10);
            var count = ReadInt32(list + 0x18);
            if (array == 0 || count is < 0 or > 100_000)
                throw new InvalidDataException($"背包列表结构无效（记录数 {count}）。");

            var rows = new List<BagRow>(count);
            for (var index = 0; index < count; index++)
            {
                var item = ReadUInt64(array + 0x20UL + (ulong)index * 8UL);
                if (item == 0) continue;
                var itemId = ReadInt32(item + 0x10);
                var itemCount = ReadInt32(item + 0x14);
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
            var table = ReadUInt64(_tables + 0x248);
            if (table == 0) return result;
            var configs = ReadReferenceList(ReadUInt64(table + 0x18), "物品配置表");
            foreach (var config in configs)
            {
                var id = ReadInt32(config + 0x10);
                if (!requested.Contains(id)) continue;
                var value = CallPointerFunction(_moduleBase + _layout.L10nTextGetValue, config + 0x18);
                var text = ReadManagedString(value);
                if (!string.IsNullOrWhiteSpace(text)) result[id] = text;
                if (result.Count == requested.Count) break;
            }
            return result;
        }

        private IReadOnlyList<ulong> ReadReferenceList(ulong list, string description)
        {
            if (list == 0) return [];
            var array = ReadUInt64(list + 0x10);
            var count = ReadInt32(list + 0x18);
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

        private ulong CallPointerFunction(ulong target, ulong arg1 = 0, ulong arg2 = 0, ulong arg3 = 0)
        {
            var result = Allocate(8);
            var codeAddress = Allocate(256);
            try
            {
                var code = new Emitter();
                code.Emit(0x53, 0x48, 0x83, 0xEC, 0x20);
                code.MovRax(_domainGet); code.CallRax();
                code.Emit(0x48, 0x89, 0xC1);
                code.MovRax(_threadAttach); code.CallRax();
                code.Emit(0x48, 0x89, 0xC3);
                code.MovRcx(arg1); code.MovRdx(arg2); code.MovR8(arg3); code.Emit(0x45, 0x33, 0xC9);
                code.MovRax(target); code.CallRax();
                code.MovRdx(unchecked((ulong)result.ToInt64())); code.Emit(0x48, 0x89, 0x02);
                code.Emit(0x48, 0x89, 0xD9);
                code.MovRax(_threadDetach); code.CallRax();
                code.Emit(0x33, 0xC0, 0x48, 0x83, 0xC4, 0x20, 0x5B, 0xC3);
                Write(unchecked((ulong)codeAddress.ToInt64()), code.ToArray());
                RunRemoteThread(codeAddress);
                return ReadUInt64(unchecked((ulong)result.ToInt64()));
            }
            finally
            {
                VirtualFreeEx(_handle, result, 0, MemRelease);
                VirtualFreeEx(_handle, codeAddress, 0, MemRelease);
            }
        }

        private ulong ResolveMethodFromObject(ulong instance, string methodName, int argumentCount)
        {
            if (instance == 0) throw new InvalidOperationException($"无法从空对象解析方法 {methodName}。");
            var klass = ReadUInt64(instance);
            if (klass == 0) throw new InvalidOperationException($"对象没有可用的 IL2CPP 类型，无法解析方法 {methodName}。");

            var nameBytes = Encoding.ASCII.GetBytes(methodName + "\0");
            var remoteName = Allocate(nameBytes.Length);
            try
            {
                var remoteNameAddress = unchecked((ulong)remoteName.ToInt64());
                Write(remoteNameAddress, nameBytes);
                var method = CallPointerFunction(
                    _classGetMethodFromName,
                    klass,
                    remoteNameAddress,
                    unchecked((ulong)argumentCount));
                if (method == 0)
                    throw new InvalidOperationException($"当前 IL2CPP 类型没有找到方法 {methodName}/{argumentCount}。");
                return method;
            }
            finally
            {
                VirtualFreeEx(_handle, remoteName, 0, MemRelease);
            }
        }

        private void RunRemoteThread(IntPtr codeAddress)
        {
            var thread = CreateRemoteThread(_handle, IntPtr.Zero, 0, codeAddress, IntPtr.Zero, 0, out _);
            if (thread == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建 IL2CPP 查询线程。");
            try
            {
                if (WaitForSingleObject(thread, Infinite) != 0)
                    throw new InvalidOperationException("等待 IL2CPP 查询线程失败。");
            }
            finally
            {
                CloseHandle(thread);
            }
        }

        private void ExecuteOnMainThread(MainThreadRequest request)
        {
            var hookAddress = _moduleBase + _layout.AudioUpdate;
            var expected = _layout.ExpectedAudioUpdate;
            var original = Read(hookAddress, expected.Length);
            if (!original.AsSpan().SequenceEqual(expected))
                throw new InvalidDataException(
                    "WorldApart 主线程入口与受支持构建不一致，已拒绝写入。请为当前版本重新验证适配器。");

            var remote = Allocate(4096);
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
                    _layout);
                Write(remoteBase, code);
                if (!VirtualProtectEx(_handle, (IntPtr)(long)hookAddress, (nuint)13,
                        PageExecuteReadWrite, out oldProtection))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法临时写入 WorldApart 主线程入口。");
                Write(hookAddress, BuildAbsoluteJump(remoteBase, 13));
                patched = true;
                FlushInstructionCache(_handle, (IntPtr)(long)hookAddress, 13);

                var stopwatch = Stopwatch.StartNew();
                var status = 0;
                while (status is 0 or 2 or 3 && stopwatch.Elapsed < TimeSpan.FromSeconds(5))
                {
                    Thread.Sleep(10);
                    status = ReadInt32(statusAddress);
                }
                if (status is 0 or 2 or 3)
                {
                    canFreeRemote = status == 0;
                    throw new TimeoutException(status == 0
                        ? "WorldApart 主线程五秒内没有执行修改流程。"
                        : "WorldApart 主线程已进入修改流程，但未在五秒内安全返回。");
                }
                if (status == 4)
                    throw new InvalidOperationException("当前存档对象已失效，请返回游戏后刷新再试。");
                if (status != 1)
                    throw new InvalidOperationException($"WorldApart 修改流程返回未知状态 {status}。");
                if (ReadUInt64(saveResultAddress) == 0)
                    throw new InvalidOperationException("游戏自身的自动存档流程没有返回有效结果。");
            }
            finally
            {
                if (patched)
                {
                    Write(hookAddress, original.AsSpan(0, 13).ToArray());
                    FlushInstructionCache(_handle, (IntPtr)(long)hookAddress, 13);
                }
                if (oldProtection != 0)
                    VirtualProtectEx(_handle, (IntPtr)(long)hookAddress, 13, oldProtection, out _);
                if (canFreeRemote) VirtualFreeEx(_handle, remote, 0, MemRelease);
            }
        }

        private static byte[] BuildMainThreadTrampoline(
            MainThreadRequest request,
            ulong statusAddress,
            ulong saveResultAddress,
            ulong moduleBase,
            BuildLayout layout)
        {
            var code = new Emitter();
            code.MovRax(statusAddress);
            code.Emit(0x83, 0x38, 0x00);
            var fastPathJump = code.EmitNearConditionalJump(0x85);

            code.Emit(0x9C, 0x50, 0x51, 0x52, 0x41, 0x50, 0x41, 0x51, 0x41, 0x52, 0x41, 0x53);
            code.Emit(0x48, 0x83, 0xEC, 0x28);
            code.MovRax(statusAddress); code.Emit(0xC7, 0x00, 0x02, 0x00, 0x00, 0x00);

            foreach (var write in request.Writes)
            {
                code.MovRax(write.Address);
                code.Emit(0xC7, 0x00);
                code.Emit(BitConverter.GetBytes(write.Value));
            }

            EmitOperation(code, request, moduleBase, layout);

            code.Emit(0x33, 0xC9, 0x33, 0xD2);
            code.MovRax(moduleBase + layout.GameStoreManagerGetCurrentStore); code.CallRax();
            code.Emit(0x48, 0x85, 0xC0);
            var noStoreJump = code.EmitNearConditionalJump(0x84);
            code.Emit(0x48, 0x89, 0xC1, 0x33, 0xD2);
            code.MovRax(moduleBase + layout.GameStoreSaveAutoSlotImmediately); code.CallRax();
            code.MovRdx(saveResultAddress); code.Emit(0x48, 0x89, 0x02);
            code.MovRax(statusAddress); code.Emit(0xC7, 0x00, 0x03, 0x00, 0x00, 0x00);
            var afterFailureJump = code.EmitNearJump();

            var noStore = code.Position;
            code.PatchNearJump(noStoreJump, noStore);
            code.MovRax(statusAddress); code.Emit(0xC7, 0x00, 0x04, 0x00, 0x00, 0x00);

            var restore = code.Position;
            code.PatchNearJump(afterFailureJump, restore);
            code.Emit(0x48, 0x83, 0xC4, 0x28);
            code.Emit(0x41, 0x5B, 0x41, 0x5A, 0x41, 0x59, 0x41, 0x58, 0x5A, 0x59, 0x58, 0x9D);

            EmitOriginalAudioUpdate(code, moduleBase, layout, statusAddress, markComplete: true);

            var fastPath = code.Position;
            code.PatchNearJump(fastPathJump, fastPath);
            EmitOriginalAudioUpdate(code, moduleBase, layout, statusAddress, markComplete: false);
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

        private static void EmitOriginalAudioUpdate(
            Emitter code,
            ulong moduleBase,
            BuildLayout layout,
            ulong statusAddress,
            bool markComplete)
        {
            code.Emit(0x48, 0x83, 0xEC, 0x28);
            code.Emit(0x48, 0x8B, 0x49, 0x10, 0x48, 0x85, 0xC9);
            var nullJump = code.EmitShortConditionalJump(0x74);
            code.Emit(0x33, 0xD2);
            code.MovRax(moduleBase + layout.AudioServiceUpdate); code.CallRax();
            code.Emit(0x48, 0x83, 0xC4, 0x28);
            if (markComplete)
            {
                code.MovRax(statusAddress);
                code.Emit(0x83, 0x38, 0x03);
                var keepFailureStatus = code.EmitShortConditionalJump(0x75);
                code.Emit(0xC7, 0x00, 0x01, 0x00, 0x00, 0x00);
                var completed = code.Position;
                code.PatchShortJump(keepFailureStatus, completed);
            }
            code.Emit(0xC3);
            var nullPath = code.Position;
            code.PatchShortJump(nullJump, nullPath);
            code.MovRax(moduleBase + layout.RaiseNullReference); code.CallRax();
            code.Emit(0xCC);
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

        private Dictionary<int, DictionaryEntry<int>> ReadIntDictionary(ulong dictionary, string description)
        {
            var result = new Dictionary<int, DictionaryEntry<int>>();
            if (dictionary == 0) return result;
            var entries = ReadUInt64(dictionary + 0x18);
            var count = ReadInt32(dictionary + 0x20);
            if (entries == 0 || count is < 0 or > 100_000)
                throw new InvalidDataException($"{description}结构无效（记录数 {count}）。");
            for (var index = 0; index < count; index++)
            {
                var entry = entries + 0x20UL + (ulong)index * 0x10UL;
                if (ReadInt32(entry) < 0) continue;
                var key = ReadInt32(entry + 0x08);
                result[key] = new DictionaryEntry<int>(entry + 0x0C, ReadInt32(entry + 0x0C));
            }
            return result;
        }

        private Dictionary<int, DictionaryEntry<float>> ReadFloatDictionary(ulong dictionary, string description)
        {
            var result = new Dictionary<int, DictionaryEntry<float>>();
            if (dictionary == 0) return result;
            var entries = ReadUInt64(dictionary + 0x18);
            var count = ReadInt32(dictionary + 0x20);
            if (entries == 0 || count is < 0 or > 100_000)
                throw new InvalidDataException($"{description}结构无效（记录数 {count}）。");
            for (var index = 0; index < count; index++)
            {
                var entry = entries + 0x20UL + (ulong)index * 0x10UL;
                if (ReadInt32(entry) < 0) continue;
                var key = ReadInt32(entry + 0x08);
                result[key] = new DictionaryEntry<float>(entry + 0x0C, ReadSingle(entry + 0x0C));
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

        private IntPtr Allocate(int size)
        {
            var address = VirtualAllocEx(_handle, IntPtr.Zero, (nuint)size, MemCommitReserve, PageExecuteReadWrite);
            if (address == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法在 WorldApart 进程分配临时内存。");
            return address;
        }

        private int ReadInt32(ulong address) => BitConverter.ToInt32(Read(address, 4));
        private ulong ReadUInt64(ulong address) => BitConverter.ToUInt64(Read(address, 8));
        private float ReadSingle(ulong address) => BitConverter.ToSingle(Read(address, 4));

        private byte[] Read(ulong address, int count)
        {
            var bytes = new byte[count];
            if (!ReadProcessMemory(_handle, (IntPtr)(long)address, bytes, (nuint)count, out var read) || read != (nuint)count)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"读取 WorldApart 内存失败：0x{address:X}。");
            return bytes;
        }

        private void Write(ulong address, byte[] bytes)
        {
            if (!WriteProcessMemory(_handle, (IntPtr)(long)address, bytes, (nuint)bytes.Length, out var written) ||
                written != (nuint)bytes.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"写入 WorldApart 内存失败：0x{address:X}。");
        }

        public void Dispose()
        {
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

    private sealed record BuildLayout(
        string ExecutableSha256,
        string GameAssemblySha256,
        string MetadataSha256,
        ulong TablesGetCurrent,
        ulong L10nTextGetValue,
        ulong GameStoreManagerGetCurrentPlayer,
        ulong GameStoreManagerGetCurrentStore,
        ulong GameStoreSaveAutoSlotImmediately,
        ulong BagSort,
        ulong BagRefreshRedDots,
        ulong AudioUpdate,
        ulong AudioServiceUpdate,
        ulong RaiseNullReference,
        byte[] ExpectedAudioUpdate,
        ulong PlayerSetInteractAttributeValue,
        ulong CombatSetBaseAttribute,
        ulong TalentMarkSpiritRootDirty,
        ulong TalentRebuildSpiritRoot,
        ulong IntDictionarySetItem,
        ulong ApplicationGetVersion,
        ulong ApplicationGetProductName,
        ulong ApplicationGetBuildGuid)
    {
        public bool Matches(string executable, string assembly, string metadata) =>
            string.Equals(ExecutableSha256, executable, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(GameAssemblySha256, assembly, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(MetadataSha256, metadata, StringComparison.OrdinalIgnoreCase);

        public bool MatchesRuntimeContent(string assembly, string metadata) =>
            !string.IsNullOrWhiteSpace(assembly) &&
            !string.IsNullOrWhiteSpace(metadata) &&
            string.Equals(GameAssemblySha256, assembly, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(MetadataSha256, metadata, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record BagRow(ulong Address, int ItemId, int Count);
    private sealed record MemoryWrite(ulong Address, int Value);
    private sealed record DictionaryEntry<T>(ulong ValueAddress, T Value);
    private sealed record MainThreadRequest(
        IReadOnlyList<MemoryWrite> Writes,
        MainThreadOperation Operation,
        ulong Owner,
        int AttributeId,
        int TargetValue,
        ulong MethodInfo = 0);

    private enum MainThreadOperation
    {
        None,
        InventoryRefresh,
        SetInteractAttribute,
        SetCombatBaseAttribute,
        SetSpiritRoot
    }

    private static class PortableExportResolver
    {
        private static readonly ConcurrentDictionary<string, ExportMap> Cache = new(StringComparer.OrdinalIgnoreCase);

        public static ExportMap Read(string path)
        {
            var info = new FileInfo(path);
            var key = $"{path}|{info.Length}:{info.LastWriteTimeUtc.Ticks}";
            return Cache.GetOrAdd(key, _ => Parse(path));
        }

        private static ExportMap Parse(string path)
        {
            var bytes = File.ReadAllBytes(path);
            int ReadInt32At(int offset) => BitConverter.ToInt32(bytes, offset);
            uint ReadUInt32At(int offset) => BitConverter.ToUInt32(bytes, offset);
            ushort ReadUInt16At(int offset) => BitConverter.ToUInt16(bytes, offset);

            var peOffset = ReadInt32At(0x3C);
            if (ReadUInt32At(peOffset) != 0x00004550)
                throw new InvalidDataException("GameAssembly.dll 不是有效的 PE 文件。");
            var sectionCount = ReadUInt16At(peOffset + 6);
            var optionalHeaderSize = ReadUInt16At(peOffset + 20);
            var optionalHeader = peOffset + 24;
            var dataDirectoryOffset = ReadUInt16At(optionalHeader) switch
            {
                0x20B => optionalHeader + 112,
                0x10B => optionalHeader + 96,
                _ => throw new InvalidDataException("GameAssembly.dll 的 PE 可选头格式不受支持。")
            };
            var sectionsOffset = optionalHeader + optionalHeaderSize;
            var sections = new List<PeSection>(sectionCount);
            for (var index = 0; index < sectionCount; index++)
            {
                var offset = sectionsOffset + index * 40;
                sections.Add(new PeSection(
                    ReadUInt32At(offset + 12),
                    ReadUInt32At(offset + 8),
                    ReadUInt32At(offset + 20),
                    ReadUInt32At(offset + 16)));
            }

            int RvaToOffset(uint rva)
            {
                foreach (var section in sections)
                {
                    var size = Math.Max(section.VirtualSize, section.RawSize);
                    if (rva >= section.VirtualAddress && rva < section.VirtualAddress + size)
                        return checked((int)(section.RawAddress + rva - section.VirtualAddress));
                }
                throw new InvalidDataException($"无法在 GameAssembly.dll 中映射 RVA 0x{rva:X}。");
            }

            var exportDirectoryRva = ReadUInt32At(dataDirectoryOffset);
            if (exportDirectoryRva == 0) throw new InvalidDataException("GameAssembly.dll 没有导出表。");
            var exportDirectory = RvaToOffset(exportDirectoryRva);
            var numberOfNames = ReadUInt32At(exportDirectory + 24);
            var functionsOffset = RvaToOffset(ReadUInt32At(exportDirectory + 28));
            var namesOffset = RvaToOffset(ReadUInt32At(exportDirectory + 32));
            var ordinalsOffset = RvaToOffset(ReadUInt32At(exportDirectory + 36));
            var exports = new Dictionary<string, ulong>(StringComparer.Ordinal);
            for (uint index = 0; index < numberOfNames; index++)
            {
                var nameOffset = RvaToOffset(ReadUInt32At(checked(namesOffset + (int)index * 4)));
                var end = nameOffset;
                while (end < bytes.Length && bytes[end] != 0) end++;
                if (end == bytes.Length) throw new InvalidDataException("GameAssembly.dll 导出名称未正确终止。");
                var name = Encoding.ASCII.GetString(bytes, nameOffset, end - nameOffset);
                var ordinal = ReadUInt16At(checked(ordinalsOffset + (int)index * 2));
                exports[name] = ReadUInt32At(checked(functionsOffset + ordinal * 4));
            }
            return new ExportMap(exports);
        }

        private sealed record PeSection(uint VirtualAddress, uint VirtualSize, uint RawAddress, uint RawSize);
    }

    private sealed record ExportMap(IReadOnlyDictionary<string, ulong> Entries)
    {
        public ulong GetRequired(string name) => Entries.TryGetValue(name, out var rva)
            ? rva
            : throw new InvalidDataException($"GameAssembly.dll 缺少导出函数 {name}。");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, nuint size, uint allocationType, uint protection);
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
    private static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr threadAttributes, nuint stackSize, IntPtr startAddress, IntPtr parameter, uint creationFlags, out uint threadId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushInstructionCache(IntPtr process, IntPtr baseAddress, nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
