using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using GameValueEditor.ModuleSdk;
using GameValueEditor.Modules.Deskrawl;

namespace GameValueEditor.Modules.LiveTests;

public sealed class DeskrawlLiveTest : IModuleLiveTest
{
    private const string CharacterEditorId = "game.deskrawl.character-attributes";
    private const string CharacterEntityId = "current-character";
    public string Game => "deskrawl";

    public async Task RunAsync(string[] args)
    {
        using var liveProcess = Process.GetProcessesByName("Deskrawl").SingleOrDefault()
                                ?? throw new InvalidOperationException("Deskrawl is not running.");
        var (process, build) = await LiveTestSupport.CreateContextAsync(liveProcess);
        var adapter = new DeskrawlGameAdapter();
        LiveTestSupport.Assert(adapter.Supports(process, build), "Deskrawl semantic core was rejected.");
        LiveTestSupport.Assert(!adapter.Supports(process, new("", "", "", "")),
            "Deskrawl accepted an unverified build.");

        var diagnostics = adapter.GetCompatibilityDiagnostics(process, build);
        Console.WriteLine(JsonSerializer.Serialize(diagnostics));
        LiveTestSupport.Assert(diagnostics.Any(item => item.DisplayName == "人物属性" &&
                                                       item.Status == GameCompatibilityDiagnosticStatus.Passed),
            "Character capability validation failed.");
        LiveTestSupport.Assert(diagnostics.Any(item => item.DisplayName == "完整材料定义" &&
                                                       item.Status == GameCompatibilityDiagnosticStatus.Passed),
            "Material capability validation failed.");
        LiveTestSupport.Assert(diagnostics.Any(item => item.DisplayName == "装备词条最高值" &&
                                                       item.Status == GameCompatibilityDiagnosticStatus.Passed),
            "Equipment maximum capability validation failed.");
        LiveTestSupport.Assert(diagnostics.Any(item => item.DisplayName == "掉落规则" &&
                                                       item.Status == GameCompatibilityDiagnosticStatus.Passed),
            "Drop rules capability validation failed.");
        LiveTestSupport.Assert(diagnostics.Any(item => item.DisplayName == "钓鱼" &&
                                                       item.Status == GameCompatibilityDiagnosticStatus.Passed),
            "Fishing capability validation failed.");
        LiveTestSupport.Assert(diagnostics.Any(item => item.DisplayName == "可堆叠背包物品" &&
                                                       item.Status == GameCompatibilityDiagnosticStatus.Passed),
            "Stackable inventory capability validation failed.");

        var character = adapter.ReadEditorEntities(process, CharacterEditorId).Single();
        LiveTestSupport.Assert(character.Fields.Count >= 10 && character.Fields.All(field => field.Value >= 0),
            "Character snapshot is incomplete.");
        LiveTestSupport.Assert(character.Fields.Any(field => field.Key == "talent-points") &&
                               character.Fields.Any(field => field.Key == "paragon-points"),
            "Talent points and paragon points were not exposed separately.");
        foreach (var key in new[] { "gold-find", "item-find-rate", "xp-gain", "hp-potion-find" })
            LiveTestSupport.Assert(character.Fields.Any(field => field.Key == key),
                $"Percentage field {key} was not exposed in character attributes.");
        foreach (var key in new[] { "gold-find", "item-find-rate", "xp-gain", "hp-potion-find" })
        {
            var current = character.Fields.Single(field => field.Key == key).ValueDisplay;
            var fieldKey = ModuleFieldKey.Create(CharacterEditorId, CharacterEntityId, key);
            LiveTestSupport.Assert(adapter.WriteField(process, fieldKey, current).DisplayValue == current,
                $"Final-consumer override for {key} did not verify.");
        }
        await Task.Delay(500);
        var stableCharacter = adapter.ReadEditorEntities(process, CharacterEditorId).Single();
        foreach (var key in new[] { "gold-find", "item-find-rate", "xp-gain", "hp-potion-find" })
            LiveTestSupport.Assert(stableCharacter.Fields.Single(field => field.Key == key).Value ==
                                   character.Fields.Single(field => field.Key == key).Value,
                $"Final-consumer override for {key} did not remain stable.");
        var materials = adapter.ReadInventory(process);
        LiveTestSupport.Assert(materials.Count > 0 && materials.All(item => item.Count >= 0),
            "Material definitions/counts are incomplete.");
        LiveTestSupport.Assert(materials.Any(item => item.Count == 0),
            "The full definition table did not expose any zero-count material.");
        LiveTestSupport.Assert(materials.All(item => item.DisplayName.Any(character => character is >= '\u3400' and <= '\u9FFF')),
            "At least one material did not use a Chinese localized display name.");

        var stackableMethod = typeof(DeskrawlGameAdapter).GetMethod(
            "ReadStackableInventory",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Stackable inventory reader is missing.");
        var stackables = (IReadOnlyList<AdapterInventoryItem>)stackableMethod.Invoke(adapter, [process])!;
        LiveTestSupport.Assert(stackables.Count > 0 && stackables.All(item => item.Count > 0),
            "Stackable inventory must contain only currently owned items.");

        var stackableRoundTrip = args.SingleOrDefault(argument =>
            argument.StartsWith("--roundtrip-stackable=", StringComparison.OrdinalIgnoreCase));
        if (stackableRoundTrip is not null)
        {
            var assetName = stackableRoundTrip[(stackableRoundTrip.IndexOf('=') + 1)..];
            var item = stackables.Single(candidate => candidate.FieldKey.EndsWith($"|{assetName}", StringComparison.Ordinal));
            var target = checked(item.Count + 1);
            try
            {
                var changed = adapter.WriteField(process, item.FieldKey, target.ToString(CultureInfo.InvariantCulture));
                LiveTestSupport.Assert(long.Parse(changed.DisplayValue, CultureInfo.InvariantCulture) == target,
                    "Stackable item +1 write did not verify.");
            }
            finally
            {
                var restored = adapter.WriteField(process, item.FieldKey, item.Count.ToString(CultureInfo.InvariantCulture));
                LiveTestSupport.Assert(long.Parse(restored.DisplayValue, CultureInfo.InvariantCulture) == item.Count,
                    "Stackable item restore did not verify.");
            }
        }

        var equipmentKey = ModuleFieldKey.Create(
            "game.deskrawl.equipment-generation", "generation-rules", "maximum-rolls");
        try
        {
            LiveTestSupport.Assert(adapter.WriteField(process, equipmentKey, "1").DisplayValue == "1",
                "Equipment maximum patch did not verify.");
        }
        finally
        {
            adapter.WriteField(process, equipmentKey, "0");
        }
        LiveTestSupport.Assert(adapter.ReadField(process, equipmentKey).DisplayValue == "0",
            "Equipment maximum patch was not restored.");

        var successKey = ModuleFieldKey.Create(
            "game.deskrawl.fishing-loot", "fishing-rules", "success-rate");
        try
        {
            LiveTestSupport.Assert(adapter.WriteField(process, successKey, "100").DisplayValue == "100",
                "Fishing success override did not verify.");
        }
        finally
        {
            adapter.WriteField(process, successKey, "-1");
        }
        LiveTestSupport.Assert(adapter.ReadField(process, successKey).DisplayValue == "-1",
            "Fishing success formula was not restored.");

        var equipmentDropKey = ModuleFieldKey.Create(
            "game.deskrawl.drop-rules", "drop-rules", "equipment-drop-rate");
        var specialDropKey = ModuleFieldKey.Create(
            "game.deskrawl.drop-rules", "drop-rules", "offline-tradable-drop-multiplier");
        var currentSpecial = adapter.ReadField(process, specialDropKey).DisplayValue;
        try
        {
            LiveTestSupport.Assert(adapter.WriteField(process, equipmentDropKey, "100").DisplayValue == "100",
                "Equipment drop rate override did not verify.");
            LiveTestSupport.Assert(adapter.WriteField(process, specialDropKey, currentSpecial).DisplayValue == currentSpecial,
                "Offline tradable drop multiplier override did not verify.");
        }
        finally
        {
            adapter.WriteField(process, equipmentDropKey, "-1");
            adapter.WriteField(process, specialDropKey, "-1");
        }
        LiveTestSupport.Assert(adapter.ReadField(process, equipmentDropKey).DisplayValue == "-1",
            "Equipment drop weights were not restored.");
        LiveTestSupport.Assert(adapter.ReadField(process, specialDropKey).Status.Contains("游戏当前", StringComparison.Ordinal),
            "Offline tradable drop multiplier was not restored.");

        if (args.Contains("--reversible-gold", StringComparer.OrdinalIgnoreCase))
        {
            var gold = character.Fields.Single(field => field.Key == "gold");
            var target = gold.Value < int.MaxValue ? gold.Value + 1 : gold.Value - 1;
            var fieldKey = ModuleFieldKey.Create(CharacterEditorId, CharacterEntityId, "gold");
            var changed = adapter.WriteField(process, fieldKey, target.ToString());
            LiveTestSupport.Assert(long.Parse(changed.DisplayValue) == target, "Gold +1 write did not verify.");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                PendingObservation = true,
                Field = "gold",
                Original = gold.Value,
                Current = target,
                RestoreWith = gold.Value
            }));
            return;
        }

        var materialArgument = args.SingleOrDefault(argument =>
            argument.StartsWith("--reversible-material=", StringComparison.OrdinalIgnoreCase));
        if (materialArgument is not null)
        {
            var assetName = materialArgument[(materialArgument.IndexOf('=') + 1)..];
            if (string.IsNullOrWhiteSpace(assetName))
                throw new InvalidOperationException("The reversible material asset name is empty.");
            var material = materials.Single(item =>
                string.Equals(item.FieldKey, assetName, StringComparison.Ordinal));
            var target = material.Count < int.MaxValue ? material.Count + 1L : material.Count - 1L;
            var changed = adapter.WriteField(process, material.FieldKey,
                target.ToString(CultureInfo.InvariantCulture));
            LiveTestSupport.Assert(long.Parse(changed.DisplayValue, CultureInfo.InvariantCulture) == target,
                "Material +1 write did not verify.");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                PendingObservation = true,
                Field = "material",
                AssetName = material.FieldKey,
                Original = material.Count,
                Current = target,
                RestoreWith = material.Count
            }));
            return;
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            VerifiedSnapshot = true,
            ReversibleRulePatchesRestored = true,
            KnownBuild = build.GameAssemblySha256.Equals(
                "F8221364BE8101C60D76BE3E031F2CD5BE376A7FB6B9B73A7CFB45E2A52A9FAB",
                StringComparison.OrdinalIgnoreCase),
            CharacterFields = character.Fields.Count,
            Character = character.Fields.Select(field => new { field.Key, field.DisplayName, field.Value, field.Status }).ToArray(),
            Materials = materials.Count,
            StackableItems = stackables.Count,
            StackableSamples = stackables.Take(12).Select(item => new { item.FieldKey, item.DisplayName, item.Count }).ToArray(),
            ZeroCountMaterials = materials.Count(item => item.Count == 0),
            MaterialDefinitions = materials.Select(item => new { item.FieldKey, item.DisplayName, item.Count }).ToArray(),
            build.GameAssemblySha256,
            build.MetadataSha256
        }));
    }
}
