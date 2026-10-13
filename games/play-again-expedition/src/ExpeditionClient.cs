using System.IO;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GameValueEditor.ModuleSdk;

namespace GameValueEditor.Modules.PlayAgainExpedition;

internal sealed class ExpeditionClient : IExpeditionClient
{
    private static readonly object ExchangeGate = new();
    private static readonly JsonSerializerOptions RowOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Lazy<Dictionary<string, JsonElement>> Templates = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Expedition.Requests.json")
            ?? throw new InvalidOperationException("模块缺少内嵌接口。");
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(stream)!;
    });

    public bool Supports(GameProcessContext process, GameBuildIdentity build)
    {
        try { _ = OriginalGameSession.Resolve(process, false); return true; } catch { return false; }
    }
    public bool CanAttachDirectly(GameProcessContext process)
    {
        try
        {
            var session = OriginalGameSession.Resolve(process, false);
            var standard = RuntimeBridge.Listener(RuntimeBridge.DefaultPort);
            return (standard.IsFree && NativeRuntimeActivation.Available(session)) || standard.IsOwnedBy(session.ProcessId) ||
                RuntimeBridge.Listener(RuntimeBridge.LegacyPort).IsOwnedBy(session.ProcessId);
        }
        catch { return false; }
    }
    public string ConnectionWarning { get; private set; } = "";

    public MaterialsSnapshot ReadMaterials(GameProcessContext process)
    {
        var read = Exchange(process, "materials-list", new { });
        var data = read.Observation.GetProperty("material");
        Require(data.GetProperty("readOnly").GetBoolean() && !data.GetProperty("nativeSaveCalled").GetBoolean() && data.GetProperty("saveFormat").GetInt32() > 0);
        var rows = data.GetProperty("rows").Deserialize<MaterialRow[]>(RowOptions)!;
        Require(rows.Length >= 29 && rows.Length <= 157 && rows.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count() == rows.Length &&
            rows.Where(row => row.Id.StartsWith("unrecognized.", StringComparison.Ordinal)).All(row => !row.CanWrite));
        return new(read, rows);
    }

    public MaterialRow WriteMaterial(GameProcessContext process, MaterialsSnapshot read, string id, long target)
    {
        var row = read.Rows.SingleOrDefault(row => row.Id == id) ?? throw new InvalidOperationException("材料不在已验证白名单内。");
        if (!row.CanWrite || row.Value is null || target < row.Minimum || target > row.Maximum) throw new InvalidOperationException("该材料当前不可修改或目标数量超出范围。");
        var data = read.Receipt.Observation.GetProperty("material");
        var scope = Scope(read.Receipt.Observation);
        var input = new { materialId = id, expectedValue = row.Value.Value, targetValue = target,
            slot = data.GetProperty("slot").GetString(), journeyMode = data.GetProperty("journeyMode").GetString(), scopeHash = scope };
        _ = Exchange(process, "materials-set", input, read.Receipt.Session, result =>
        {
            var value = result.GetProperty("material");
            var unchanged = value.GetProperty("status").GetString() == "unchanged" && !value.GetProperty("nativeSaveCalled").GetBoolean();
            var changed = value.GetProperty("status").GetString() == "storage-verified" && value.GetProperty("nativeSaveCalled").GetBoolean() &&
                value.GetProperty("storageVerified").GetBoolean() && value.GetProperty("onlySerializedMaterialChanged").GetBoolean() && result.GetProperty("flushRequested").GetBoolean();
            Require((unchanged || changed) && value.GetProperty("materialId").GetString() == id && value.GetProperty("value").GetInt64() == target &&
                value.GetProperty("previousValue").GetInt64() == row.Value && value.GetProperty("slot").GetString() == input.slot &&
                value.GetProperty("journeyMode").GetString() == input.journeyMode && Scope(result) == scope);
        });
        return row with { Value = target, StoredValue = target, Status = "已核验实时数量与游戏原生保存" + ConnectionWarning };
    }

    public WheelSnapshot ReadWheel(GameProcessContext process)
    {
        var read = Exchange(process, "wheel-inspect", new { });
        var data = read.Observation.GetProperty("probability");
        CheckProbabilityBase(data);
        Require(data.GetProperty("readOnly").GetBoolean());
        var report = data.GetProperty("report");
        var wheelNamespace = report.GetProperty("namespaces").GetProperty("wheel");
        var installed = wheelNamespace.GetProperty("enabled").GetBoolean();
        var settings = wheelNamespace.GetProperty("settings");
        var profile = WheelProfile.FromSessionSettings(settings);
        var ready = data.GetProperty("phase").GetString() == "idle" && !data.GetProperty("pendingWheel").GetBoolean() &&
            report.GetProperty("blocked").ValueKind == JsonValueKind.Null && report.GetProperty("hooksOwned").GetBoolean() &&
            report.GetProperty("rngOriginal").GetBoolean() && report.GetProperty("rngWritable").GetBoolean();
        var rows = report.GetProperty("wheel").EnumerateArray().Select(row => new WheelRow(row.GetProperty("index").GetInt32() + 1,
            RewardName(row), GroupName(row.GetProperty("group").GetString()), Percent(row.GetProperty("originalPercent").GetDouble()),
            Percent(row.GetProperty("percent").GetDouble()))).ToArray();
        var initialized = rows.Length == 16;
        var wheelStatus = report.GetProperty("wheelStatus").GetString();
        return new(read, profile, installed, ready && initialized, !initialized ? wheelStatus == "not-initialized" ?
            "请先在游戏中打开大转盘，再刷新。掉落规则和材料不受影响。" : "当前奖盘包含尚未支持的奖励项；材料和掉落规则不受影响。" :
            ready ? installed ? "已应用会话概率；重启游戏恢复原规则。" : "当前使用游戏原概率。" :
            "请停止战斗并完成转盘结算；当前不允许应用或还原。", rows);
    }

    public void ConfigureWheel(GameProcessContext process, WheelSnapshot read, WheelProfile profile)
    {
        profile.Validate();
        if (!read.Ready) throw new InvalidOperationException("请等待当前游戏操作和转盘结算结束。");
        ValidateAvailableGroups(read, profile);
        var scope = Scope(read.Receipt.Observation);
        var revision = read.Receipt.Observation.GetProperty("probability").GetProperty("revision").GetInt64();
        var input = new { scopeHash = scope, revision, profile };
        _ = Exchange(process, "wheel-configure", input, read.Receipt.Session, result =>
        {
            var data = result.GetProperty("probability"); CheckProbabilityBase(data);
            var report = data.GetProperty("report");
            Require(Scope(result) == scope && data.GetProperty("revision").GetInt64() == revision + 1 && !data.GetProperty("readOnly").GetBoolean() &&
                report.GetProperty("installed").GetBoolean() && report.GetProperty("hooksOwned").GetBoolean() && report.GetProperty("rngOriginal").GetBoolean() &&
                report.GetProperty("blocked").ValueKind == JsonValueKind.Null && report.GetProperty("namespaces").GetProperty("wheel").GetProperty("enabled").GetBoolean() &&
                JsonNode.DeepEquals(JsonNode.Parse(profile.ToJson()), JsonNode.Parse(report.GetProperty("namespaces").GetProperty("wheel").GetProperty("settings").GetRawText())) &&
                OtherProbabilitySettingsUnchanged(read.Receipt.Observation.GetProperty("probability").GetProperty("report"), report));
        });
    }

    internal static void ValidateAvailableGroups(WheelSnapshot read, WheelProfile profile)
    {
        var group = profile.Wheel.GroupPercent;
        var desired = new Dictionary<string, double> { ["ordinary"] = group.Ordinary, ["immortal"] = group.Immortal, ["mythic"] = group.Mythic,
            ["protection"] = group.Protection, ["sacredProtection"] = group.SacredProtection };
        var board = read.Receipt.Observation.GetProperty("probability").GetProperty("report").GetProperty("wheel");
        foreach (var budget in desired.Where(pair => pair.Value > 0))
        {
            var available = board.EnumerateArray().Any(row => row.GetProperty("group").GetString() == budget.Key &&
                (profile.Wheel.TypeWeights?.GetValueOrDefault(row.GetProperty("kind").GetString()!, 1) ?? 1) > 0 &&
                (profile.Wheel.TypeWeights?.GetValueOrDefault("q" + row.GetProperty("quality").GetInt32(), 1) ?? 1) > 0);
            if (!available) throw new InvalidOperationException($"当前奖盘的“{GroupName(budget.Key)}”没有可用奖格，请调整百分比或类内权重后重新应用。");
        }
    }

    public void ResetWheel(GameProcessContext process, WheelSnapshot read)
    {
        if (!read.Ready) throw new InvalidOperationException("请等待当前游戏操作和转盘结算结束。");
        var scope = Scope(read.Receipt.Observation);
        var revision = read.Receipt.Observation.GetProperty("probability").GetProperty("revision").GetInt64();
        _ = Exchange(process, "wheel-reset", new { scopeHash = scope, revision }, read.Receipt.Session, result =>
        {
            var data = result.GetProperty("probability"); CheckProbabilityBase(data);
            Require(Scope(result) == scope && data.GetProperty("revision").GetInt64() == revision + 1 && !data.GetProperty("readOnly").GetBoolean() &&
                data.GetProperty("outcome").GetProperty("restored").GetBoolean() && data.GetProperty("outcome").GetProperty("rngOriginal").GetBoolean() &&
                !data.GetProperty("report").GetProperty("namespaces").GetProperty("wheel").GetProperty("enabled").GetBoolean() &&
                OtherProbabilitySettingsUnchanged(read.Receipt.Observation.GetProperty("probability").GetProperty("report"), data.GetProperty("report")));
        });
    }

    internal static bool OtherProbabilitySettingsUnchanged(JsonElement before, JsonElement after)
        => ProbabilityNamespaceUnchanged(before, after, "drops");

    internal static bool ProbabilityNamespaceUnchanged(JsonElement before, JsonElement after, string name)
    {
        var oldDrops = before.GetProperty("namespaces").GetProperty(name);
        var newDrops = after.GetProperty("namespaces").GetProperty(name);
        return oldDrops.GetProperty("enabled").GetBoolean() == newDrops.GetProperty("enabled").GetBoolean() &&
            JsonNode.DeepEquals(JsonNode.Parse(oldDrops.GetProperty("settings").GetRawText()), JsonNode.Parse(newDrops.GetProperty("settings").GetRawText()));
    }

    public DropsSnapshot ReadDrops(GameProcessContext process)
    {
        var read = Exchange(process, "drops-inspect", new { });
        var data = read.Observation.GetProperty("probability"); CheckProbabilityBase(data);
        Require(data.GetProperty("readOnly").GetBoolean());
        var report = data.GetProperty("report"); var space = report.GetProperty("namespaces").GetProperty("drops");
        var profile = DropProfile.Parse(space.GetProperty("settings").GetRawText());
        var installed = space.GetProperty("enabled").GetBoolean();
        var ready = data.GetProperty("phase").GetString() == "idle" && !data.GetProperty("pendingWheel").GetBoolean() &&
            report.GetProperty("blocked").ValueKind == JsonValueKind.Null && report.GetProperty("hooksOwned").GetBoolean() &&
            report.GetProperty("rngOriginal").GetBoolean() && report.GetProperty("rngWritable").GetBoolean() && report.GetProperty("advancedDropsSupported").GetBoolean();
        var native = DropProfile.Options.ToDictionary(option => option.Path, option => DropProfile.Format(option.Default) +
            (option.Group == "奶牛奖池权重" ? "（相对权重）" : "%"), StringComparer.Ordinal);
        foreach (var pair in new[] { ("equipmentBonusPercent", "nativeDropBonus"), ("upgradeBonusPercent", "nativeUpgradeChance"),
            ("goldBonusPercent", "nativeGoldBonus"), ("ordinaryEquipmentPercent", "nativeOrdinaryChance"), ("abyssImmortalPercent", "nativeImmortalChance") })
            native[pair.Item1] = report.TryGetProperty(pair.Item2, out var value) && value.ValueKind == JsonValueKind.Number ? Percent(value.GetDouble() * 100) : "需在对应模式读取";
        native["mine.stonePercent"] = "60% / 五星矿工 50%";
        var hits = space.GetProperty("hits").EnumerateObject().Sum(property => property.Value.GetInt64());
        var status = ready ? installed ? $"当前会话已应用；规则累计命中 {hits} 次。重启恢复原规则。" : "当前使用游戏原规则；仅勾选要修改的项目。" :
            report.GetProperty("advancedDropsSupported").GetBoolean() ? "请停止战斗并完成转盘结算；若规则异常，请正常重启后刷新。" : "正式游戏掉落入口尚未核验，不允许应用。";
        return new(read, profile, installed, ready, status, native);
    }
    public void ConfigureDrops(GameProcessContext process, DropsSnapshot read, DropProfile profile)
    {
        profile.Validate(); if (profile.IsEmpty) throw new InvalidOperationException("请启用至少一项，或使用恢复原规则。");
        if (!read.Ready) throw new InvalidOperationException(read.Status);
        using var document = JsonDocument.Parse(profile.ToJson());
        ChangeDrops(process, read, "drops-configure", document.RootElement.Clone());
    }
    public void ResetDrops(GameProcessContext process, DropsSnapshot read)
    {
        if (!read.Ready) throw new InvalidOperationException(read.Status);
        ChangeDrops(process, read, "drops-reset", null);
    }
    private void ChangeDrops(GameProcessContext process, DropsSnapshot read, string operation, JsonElement? profile)
    {
        var scope = Scope(read.Receipt.Observation); var revision = read.Receipt.Observation.GetProperty("probability").GetProperty("revision").GetInt64();
        var input = new JsonObject { ["scopeHash"] = scope, ["revision"] = revision };
        if (profile is not null) input["profile"] = JsonNode.Parse(profile.Value.GetRawText());
        _ = Exchange(process, operation, input, read.Receipt.Session, result =>
        {
            var data = result.GetProperty("probability"); CheckProbabilityBase(data); var report = data.GetProperty("report");
            Require(Scope(result) == scope && data.GetProperty("revision").GetInt64() == revision + 1 && !data.GetProperty("readOnly").GetBoolean() &&
                report.GetProperty("blocked").ValueKind == JsonValueKind.Null && report.GetProperty("hooksOwned").GetBoolean() && report.GetProperty("rngOriginal").GetBoolean() &&
                ProbabilityNamespaceUnchanged(read.Receipt.Observation.GetProperty("probability").GetProperty("report"), report, "wheel"));
            var space = report.GetProperty("namespaces").GetProperty("drops");
            if (profile is not null) Require(report.GetProperty("installed").GetBoolean() && space.GetProperty("enabled").GetBoolean() &&
                JsonNode.DeepEquals(JsonNode.Parse(profile.Value.GetRawText()), JsonNode.Parse(space.GetProperty("settings").GetRawText())));
            else Require(!space.GetProperty("enabled").GetBoolean() && data.GetProperty("outcome").GetProperty("restored").GetBoolean() &&
                data.GetProperty("outcome").GetProperty("rngOriginal").GetBoolean());
        });
    }

    public FateSnapshot ReadFate(GameProcessContext process)
    {
        var read = Exchange(process, "fate-inspect", new { });
        var data = read.Observation.GetProperty("fate"); CheckProbabilityBase(data);
        Require(data.GetProperty("readOnly").GetBoolean());
        var report = data.GetProperty("report");
        Require(report.GetProperty("preservesPity").GetBoolean() && report.GetProperty("singleAndTen").GetBoolean());
        var original = FateProfile.Parse(report.GetProperty("original").GetRawText());
        var installed = report.GetProperty("installed").GetBoolean();
        var profile = installed ? FateProfile.Parse(report.GetProperty("profile").GetRawText()) : original;
        var ready = report.GetProperty("ready").GetBoolean();
        var status = report.GetProperty("blocked").ValueKind != JsonValueKind.Null ? "命格规则未确认，请恢复原规则后刷新。" :
            ready ? installed ? "命格概率已应用，单抽和十连均生效；保留百抽神话保底。" : "当前使用游戏原概率；支持单抽和十连，保留百抽神话保底。" :
            "请等待当前游戏操作和转盘结算结束后刷新。";
        return new(read, profile, original, installed, ready, status, report.GetProperty("canRestore").GetBoolean());
    }
    public void ConfigureFate(GameProcessContext process, FateSnapshot read, FateProfile profile)
    { profile.Validate(); ChangeFate(process, read, profile); }
    public void ResetFate(GameProcessContext process, FateSnapshot read) => ChangeFate(process, read, null);
    private void ChangeFate(GameProcessContext process, FateSnapshot read, FateProfile? profile)
    {
        if (profile is null ? !read.CanRestore : !read.Ready) throw new InvalidOperationException(read.Status);
        var scope = Scope(read.Receipt.Observation);
        var revision = read.Receipt.Observation.GetProperty("fate").GetProperty("revision").GetInt64();
        var input = new JsonObject { ["scopeHash"] = scope, ["revision"] = revision };
        if (profile is not null) input["profile"] = JsonNode.Parse(profile.ToJson());
        _ = Exchange(process, profile is null ? "fate-reset" : "fate-configure", input, read.Receipt.Session, result =>
        {
            var data = result.GetProperty("fate"); CheckProbabilityBase(data); var report = data.GetProperty("report");
            Require(Scope(result) == scope && data.GetProperty("revision").GetInt64() == revision + 1 && !data.GetProperty("readOnly").GetBoolean() &&
                report.GetProperty("hooksOwned").GetBoolean() && report.GetProperty("blocked").ValueKind == JsonValueKind.Null &&
                report.GetProperty("installed").GetBoolean() == (profile is not null));
            if (profile is not null) Require(JsonNode.DeepEquals(JsonNode.Parse(profile.ToJson()), JsonNode.Parse(report.GetProperty("profile").GetRawText())));
        });
    }

    public EquipmentSnapshot ReadEquipment(GameProcessContext process)
    {
        var read = Exchange(process, "equipment-inspect", new { });
        var data = read.Observation.GetProperty("equipment"); CheckProbabilityBase(data);
        Require(data.GetProperty("readOnly").GetBoolean());
        var report = data.GetProperty("report");
        Require(report.GetProperty("newEquipmentOnly").GetBoolean() && report.GetProperty("doubleValues").GetBoolean());
        if (!(report.TryGetProperty("upgradeRequired", out var legacy) && legacy.GetBoolean()))
            Require(report.GetProperty("gemBase").GetBoolean() && report.GetProperty("generatedKinds").EnumerateArray().Select(row => row.GetString()).SequenceEqual(new[] { "gear", "gem", "costume", "badge" }));
        var installed = report.GetProperty("installed").GetBoolean(); var ready = report.GetProperty("ready").GetBoolean();
        var status = report.TryGetProperty("probabilityUpgradeRequired", out var probabilityUpgrade) && probabilityUpgrade.GetBoolean() ? "本次游戏仍在使用旧版概率规则，请先在大转盘、掉落页恢复已应用规则，再刷新这些页面和装备页；无需重启。" :
            report.TryGetProperty("upgradeRequired", out var upgrade) && upgrade.GetBoolean() ? "当前装备规则来自旧版，请先点恢复原规则，再刷新并应用新版规则；无需重启游戏。" :
            report.GetProperty("blocked").ValueKind != JsonValueKind.Null ? "装备规则未确认，请恢复原规则后刷新。" : ready ?
            installed ? "新生成装备、宝石、时装和徽章词条取满值已启用；已有物品不变。" : "当前使用游戏随机取值；满值规则仅影响之后新生成的装备、宝石、时装和徽章。" :
            "请等待当前游戏操作和转盘结算结束后刷新。";
        return new(read, installed, ready, report.GetProperty("canRestore").GetBoolean(), status);
    }
    public void ConfigureEquipment(GameProcessContext process, EquipmentSnapshot read) => ChangeEquipment(process, read, true);
    public void ResetEquipment(GameProcessContext process, EquipmentSnapshot read) => ChangeEquipment(process, read, false);
    private void ChangeEquipment(GameProcessContext process, EquipmentSnapshot read, bool enable)
    {
        if (enable ? !read.Ready : !read.CanRestore) throw new InvalidOperationException(read.Status);
        var scope = Scope(read.Receipt.Observation);
        var revision = read.Receipt.Observation.GetProperty("equipment").GetProperty("revision").GetInt64();
        var input = new JsonObject { ["scopeHash"] = scope, ["revision"] = revision };
        if (enable) input["profile"] = JsonNode.Parse(EquipmentPage.EnabledJson);
        _ = Exchange(process, enable ? "equipment-configure" : "equipment-reset", input, read.Receipt.Session, result =>
        {
            var data = result.GetProperty("equipment"); CheckProbabilityBase(data); var report = data.GetProperty("report");
            Require(Scope(result) == scope && data.GetProperty("revision").GetInt64() == revision + 1 && !data.GetProperty("readOnly").GetBoolean() &&
                report.GetProperty("hooksOwned").GetBoolean() && report.GetProperty("blocked").ValueKind == JsonValueKind.Null && report.GetProperty("installed").GetBoolean() == enable);
            if (enable) Require(report.GetProperty("profile").GetProperty("enabled").GetBoolean());
        });
    }

    private static void CheckProbabilityBase(JsonElement data) => Require(data.GetProperty("sessionOnly").GetBoolean() &&
        !data.GetProperty("nativeSaveCalled").GetBoolean() && !data.GetProperty("gameActionCalled").GetBoolean() && data.GetProperty("report").GetProperty("ownerCurrent").GetBoolean());
    private static void Require(bool valid) { if (!valid) throw new InvalidOperationException("接口结果未完整确认，已停止写入，不会自动重试。"); }
    private static string Scope(JsonElement data)
    {
        var scope = data.GetProperty("scopeHash").GetString() ?? "";
        Require(Regex.IsMatch(scope, "^[a-f0-9]{64}$")); return scope;
    }
    private static string Percent(double value) => value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + "%";
    private static string GroupName(string? group) => group switch { "ordinary" => "普通", "immortal" => "不朽", "mythic" => "神话", "protection" => "装备保护卷", "sacredProtection" => "神圣保护卷", _ => "未知奖格" };
    private static string RewardName(JsonElement row)
    {
        var group = row.GetProperty("group").GetString();
        if (group is "protection" or "sacredProtection") return GroupName(group);
        var kind = row.GetProperty("kind").GetString() switch { "gear" => "装备", "costume" => "时装", "badge" => "徽章", "gem" => "宝石", "material" => "材料", _ => "奖励" };
        var quality = row.GetProperty("quality").GetInt32() switch { 3 => "紫色", 4 => "橙色", 5 => "不朽", 6 => "神话", _ => "" };
        return quality + kind;
    }

    internal static string BuildRequest(string name, GameSession session, object input)
    {
        var template = Templates.Value[name];
        var expression = template.GetProperty("params").GetProperty("expression").GetString()!;
        const string configMarker = "__GVE_CONFIG_JSON__", inputMarker = "__GVE_INPUT_JSON__";
        var configPosition = expression.IndexOf(configMarker, StringComparison.Ordinal);
        var inputPosition = expression.IndexOf(inputMarker, StringComparison.Ordinal);
        Require(configPosition >= 0 && inputPosition > configPosition && expression.LastIndexOf(configMarker, StringComparison.Ordinal) == configPosition &&
            expression.LastIndexOf(inputMarker, StringComparison.Ordinal) == inputPosition);
        var config = JsonSerializer.Serialize(new { processId = session.ProcessId, executablePath = session.ExecutablePath, archivePath = session.ArchivePath, profilePath = session.ProfilePath });
        var arguments = JsonSerializer.Serialize(input, WheelProfile.JsonOptions);
        expression = expression[..configPosition] + config + expression[(configPosition + configMarker.Length)..inputPosition] + arguments + expression[(inputPosition + inputMarker.Length)..];
        return JsonSerializer.Serialize(new { id = template.GetProperty("id").GetInt32(), method = "Runtime.evaluate",
            @params = new { expression, awaitPromise = true, returnByValue = true, timeout = 5000 } });
    }

    private SessionRead Exchange(GameProcessContext process, string name, object input, GameSession? expected = null, Action<JsonElement>? confirm = null)
    {
        // All pages share the same game interface. Wait for cleanup rather than
        // treating an ordinary overlapping refresh as a failed game operation.
        lock (ExchangeGate) return ExchangeCore(process, name, input, expected, confirm);
    }

    private SessionRead ExchangeCore(GameProcessContext process, string name, object input, GameSession? expected, Action<JsonElement>? confirm)
    {
        var session = OriginalGameSession.Resolve(process, false);
        if (expected is not null && session != expected) throw new InvalidOperationException("读取回执属于旧游戏实例，请重新刷新。");
        var request = BuildRequest(name, session, input);
        var mutating = confirm is not null;
        using var bridge = RuntimeBridge.Connect(session);
        ConnectionWarning = "";
        using var gate = mutating ? MutationGate.Acquire(RuntimeBridge.Root, RuntimeBridge.Key(session)) : null;
        // Claim occurs only after local validation/discovery, immediately before the only send.
        bridge.AssertEndpoint();
        gate?.Claim();
        try
        {
            var observation = Send(bridge.Uri, request, name.StartsWith("materials-", StringComparison.Ordinal) ? 401 : 402).GetAwaiter().GetResult();
            bridge.AssertEndpoint();
            MutationOutcome.ThrowIfRejected(observation, name, gate);
            _ = Scope(observation);
            confirm?.Invoke(observation);
            gate?.Confirm();
            bridge.Finish(); ConnectionWarning = bridge.Warning;
            return new(session, observation, bridge.Warning);
        }
        catch (MutationRejectedException) { throw; }
        catch { gate?.Uncertain(); throw; }
    }

    internal static async Task<JsonElement> Send(Uri uri, string request, int id)
    {
        using var socket = new ClientWebSocket(); socket.Options.Proxy = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await socket.ConnectAsync(uri, timeout.Token).ConfigureAwait(false);
            await socket.SendAsync(Encoding.UTF8.GetBytes(request), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
            for (var index = 0; index < 32; index++)
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[4096]; WebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(new ArraySegment<byte>(chunk), timeout.Token).ConfigureAwait(false);
                    Require(received.MessageType == WebSocketMessageType.Text);
                    buffer.Write(chunk, 0, received.Count); Require(buffer.Length <= 131072);
                } while (!received.EndOfMessage);
                using var message = JsonDocument.Parse(buffer.ToArray());
                var root = message.RootElement;
                if (!root.TryGetProperty("id", out var responseId)) continue;
                Require(responseId.GetInt32() == id && !root.TryGetProperty("error", out _));
                var result = root.GetProperty("result");
                if (result.TryGetProperty("exceptionDetails", out var error))
                {
                    var description = error.GetProperty("exception").GetProperty("description").GetString() ?? "";
                    var code = Regex.Match(description, @"^Error:\s*([A-Z][A-Z_]{0,80})(?:[\r\n:]|$)");
                    throw new InvalidOperationException(ExplainGuard(code.Success ? code.Groups[1].Value : "RUNTIME_EXECUTION_UNCONFIRMED"));
                }
                return result.GetProperty("result").GetProperty("value").Clone();
            }
            throw new InvalidOperationException("接口未返回确定结果，不会自动重试。");
        }
        finally
        {
            if (socket.State == WebSocketState.Open)
            {
                using var close = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "module request finished", close.Token).ConfigureAwait(false); }
                catch (Exception error) when (error is WebSocketException or OperationCanceledException) { socket.Abort(); }
                // A normal-close handshake failure is not evidence that a confirmed
                // business reply did not execute. Never replace that reply or retry it.
            }
        }
    }

    internal static string ExplainGuard(string code, bool notExecuted = false) => code switch
    {
        "GAME_NOT_ENTERED" or "GAME_NOT_READY" => "请先进入远征存档，再刷新模块。",
        "JOURNEY_MODE_NOT_SUPPORTED" => "当前模块版本无法读取这份材料数据，请使用新版模块后刷新。",
        "GAME_MODE_NOT_SUPPORTED" => "目前只支持远征存档，不支持三国存档。",
        "READ_RECEIPT_SCOPE_CHANGED" or "MATERIAL_OLD_VALUE_CHANGED" or "MATERIAL_VALUE_CHANGED" or "ACTIVE_SAVE_CHANGED" or "READ_INSTANCE_CHANGED" or
            "LIVE_SNAPSHOT_CHANGED" or "CHANGED_BEFORE_ASSIGNMENT" or "CHANGED_BEFORE_SAVE" => "游戏数据或当前存档发生变化，请重新刷新；本次不会自动重试。",
        "MATERIAL_BATTLE_ACTIVE" => "当前战斗尚未结束，请先停止战斗再修改材料。",
        "MATERIAL_BATTLE_PAUSED" => "战斗已暂停但尚未结束，请先退出当前战斗再修改材料。",
        "MATERIAL_OPERATION_PENDING" => "游戏正在处理当前操作，结束后请刷新材料。",
        "MATERIAL_WHEEL_PENDING" => "大转盘动画或奖励尚未结算，请完成后刷新材料。",
        "MATERIAL_SAVE_BLOCKED" => "游戏保存受阻，当前不能修改材料；请先确认游戏能正常保存。",
        "MATERIAL_SAVE_PENDING" => "实时数量和保存数量不同，请正常保存后重新读取材料。",
        "MATERIAL_NOT_DEFINED" or "MATERIAL_CONTRACT_CHANGED" => "当前材料数据结构未通过核验，已停止操作，不会修改游戏。",
        "MATERIAL_READ_ONLY" => "当前材料只读，未执行修改。",
        "MATERIAL_RANGE_INVALID" => "材料数量超出已验证范围，未执行修改。",
        "GAME_BUSY" or "WHEEL_PENDING" or "PROBABILITY_BUSY" or "PROBABILITY_OPERATION_NOT_IDLE" => "请停止战斗并等大转盘结算结束后刷新。",
        "PROBABILITY_IMPLEMENTATION_CHANGED_RESTART_REQUIRED" => "当前游戏实例仍有旧版模块的会话规则，请正常保存并重启一次再连接；这不是启动参数要求。",
        "PROBABILITY_IMPLEMENTATION_CHANGED_RESTORE_REQUIRED" => "当前游戏仍使用旧版模块的概率规则。请先在大转盘和掉落页面恢复已应用的规则，然后刷新、重新应用；不用修改启动参数或重启游戏。",
        "WHEEL_DRAW_BOARD_UNAVAILABLE" => "当前抽奖未能读取实际奖盘，概率规则未生效。请恢复规则后刷新；不会用旧奖盘替换本次奖励。",
        "PROBABILITY_CONSUMER_NOT_SUPPORTED" => "选中的规则在当前游戏中没有找到对应入口，未应用；其它已定位的规则仍可使用。",
        "FATE_PROFILE_INVALID" => "四档命格概率必须在 0～100% 内，合计为 100%。",
        "FATE_BUSY_OR_CONFLICT" => "请等待当前游戏操作结束后刷新命格；不会覆盖其它程序的规则。",
        "FATE_CONSUMER_CHANGED" or "FATE_CONTRACT_CHANGED" or "FATE_POOL_UNAVAILABLE" => "当前命格抽取入口尚未确认，未应用规则；其它内容页面不受影响。",
        "EQUIPMENT_BUSY_OR_CONFLICT" => "请等待当前游戏操作结束后刷新装备；不会覆盖其它程序的规则。",
        "EQUIPMENT_UPGRADE_REQUIRED" => "请先恢复旧版装备规则，再刷新并应用新版；无需重启游戏。",
        "EQUIPMENT_PROBABILITY_UPGRADE_REQUIRED" => "请先在大转盘、掉落页恢复旧版已应用规则，刷新这些页面与装备页后再应用；无需重启。",
        "EQUIPMENT_CONSUMER_CHANGED" or "EQUIPMENT_CONTRACT_CHANGED" => "当前装备生成公式尚未确认，未启用满值规则；其它内容页面不受影响。",
        "EQUIPMENT_GEM_CONSUMER_CHANGED" or "EQUIPMENT_ACCESSORY_CONSUMER_CHANGED" => "当前宝石或其它物品的生成数值尚未确认，未启用满值规则；其它内容页面不受影响。",
        _ => notExecuted ? $"当前操作条件不满足（{code}）。" : $"模块操作未确认（{code}）。请先刷新核验；若有不确定写入，需要正常重启游戏后再操作，不会自动重试。"
    };
}

internal sealed class MutationRejectedException(string message) : InvalidOperationException(message);

internal static class MutationOutcome
{
    internal static void ThrowIfRejected(JsonElement observation, string requestName, MutationGate? gate)
    {
        if (!observation.TryGetProperty("protocol", out _)) return;
        // A malformed or mismatched receipt remains uncertain; no text allowlist.
        if (gate is null || observation.ValueKind != JsonValueKind.Object || observation.EnumerateObject().Count() != 4 ||
            observation.GetProperty("protocol").GetString() != "gve-mutation-outcome-v1" ||
            observation.GetProperty("outcome").GetString() != "not-executed" ||
            observation.GetProperty("requestName").GetString() != requestName ||
            observation.GetProperty("reason").GetString() is not { } reason || !Regex.IsMatch(reason, "\\A[A-Z][A-Z_]{0,80}\\z"))
            throw new InvalidOperationException("模块未返回匹配当前操作的结果，不会自动重试。");
        gate.Reject();
        throw new MutationRejectedException(ExpeditionClient.ExplainGuard(reason, true) + " 本次未执行修改，可刷新后继续。");
    }
}

internal sealed class MutationGate : IDisposable
{
    private readonly FileStream _lease;
    private readonly string _statePath;
    private bool _claimed;
    private MutationGate(string root, string key)
    {
        if (key.Length > 128 || !Regex.IsMatch(key, "^[A-Za-z0-9-]+$")) throw new InvalidOperationException("会话记录键无效。");
        EnsureNoLinks(root);
        Directory.CreateDirectory(root);
        EnsureNoLinks(root);
        EnsureNoLinks(Path.Combine(root, key + ".lock"));
        EnsureNoLinks(Path.Combine(root, key + ".json"));
        _lease = new(Path.Combine(root, key + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _statePath = Path.Combine(root, key + ".json");
        try
        {
            if (File.Exists(_statePath))
            {
                if (new FileInfo(_statePath).Length > 8192) throw new InvalidOperationException("会话记录无效，已停止写入。");
                using var document = JsonDocument.Parse(File.ReadAllBytes(_statePath));
                if (document.RootElement.GetProperty("phase").GetString() is not ("confirmed" or "rejected"))
                    throw new InvalidOperationException("本游戏会话有结果不确定的操作，只允许读取；请正常重启游戏后再修改，不会自动重试。");
            }
        }
        catch { _lease.Dispose(); throw; }
    }
    internal static MutationGate Acquire(string root, string key) => new(root, key);
    internal static void EnsureNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("会话记录目录包含链接，已拒绝写入。");
    }
    internal void Claim() { Persist("pending"); _claimed = true; }
    internal void Confirm() => Persist("confirmed");
    internal void Reject() => Persist("rejected");
    internal void Uncertain() { if (_claimed) { try { Persist("uncertain"); } catch { } } }
    private void Persist(string phase)
    {
        // A torn file is intentionally unreadable and blocks further writes.
        using var stream = new FileStream(_statePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        stream.Write(JsonSerializer.SerializeToUtf8Bytes(new { phase, requestId = Guid.NewGuid(), observedAt = DateTimeOffset.UtcNow }));
        stream.Flush(true);
    }
    public void Dispose() => _lease.Dispose();
}
