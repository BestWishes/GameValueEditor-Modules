using System.Diagnostics;
using System.Globalization;
using GameValueEditor.ModuleSdk;
using GameValueEditor.Modules.Deskrawl;

namespace GameValueEditor.Modules.LiveTests;

// Run explicitly only after a save backup. Remove the test delta, not intervening rewards.
public sealed class DeskrawlWriteSmokeTest : IModuleLiveTest
{
    public string Game => "deskrawl-write-smoke";
    public async Task RunAsync(string[] args)
    {
        using var live = Process.GetProcessesByName("Deskrawl").Single();
        var (process, build) = await LiveTestSupport.CreateContextAsync(live);
        var adapter = new DeskrawlGameAdapter();
        LiveTestSupport.Assert(adapter.Supports(process, build), "Runtime validation failed before write smoke test.");
        const string editor = "game.deskrawl.character-attributes";
        const string entity = "current-character";
        var key = ModuleFieldKey.Create(editor, entity, "gold");
        long ReadGold() => adapter.ReadEditorEntities(process, editor).Single().Fields.Single(field => field.Key == "gold").Value;
        var before = ReadGold();
        var delta = before == int.MaxValue ? -1 : 1;
        var confirmed = false;
        try
        {
            var changed = adapter.WriteField(process, key, (before + delta).ToString(CultureInfo.InvariantCulture));
            LiveTestSupport.Assert(long.Parse(changed.DisplayValue, CultureInfo.InvariantCulture) == before + delta,
                "Business write did not return the verified target; do not automatically retry.");
            confirmed = true;
            LiveTestSupport.Assert(ReadGold() >= before + delta, "Independent authority read rejected the gold delta.");
        }
        finally
        {
            if (confirmed)
            {
                var current = ReadGold();
                var target = current - delta;
                LiveTestSupport.Assert(target is >= 0 and <= int.MaxValue, "Restoration became unsafe; stopped without guessing.");
                var restored = adapter.WriteField(process, key, target.ToString(CultureInfo.InvariantCulture));
                LiveTestSupport.Assert(long.Parse(restored.DisplayValue, CultureInfo.InvariantCulture) == target,
                    "Test delta restoration was not verified; do not retry automatically.");
            }
        }
        Console.WriteLine("Deskrawl business write/save smoke passed: gold test delta applied and removed through the game's own methods on its main thread; intervening rewards preserved. This is not restart-persistence or panel-effect evidence.");
    }
}
