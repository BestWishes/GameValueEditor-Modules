using System.Diagnostics;
using System.Text.Json;
using GameValueEditor.Modules.LastEpoch;
using GameValueEditor.ModuleSdk;

namespace GameValueEditor.Modules.LiveTests;

public sealed class LastEpochLiveTest : IModuleLiveTest
{
    public string Game => "last-epoch";

    public async Task RunAsync(string[] args)
    {
        using var liveProcess = Process.GetProcessesByName("Last Epoch").SingleOrDefault()
                                ?? throw new InvalidOperationException("Last Epoch is not running.");
        var (process, build) = await LiveTestSupport.CreateContextAsync(liveProcess);
        var adapter = new LastEpochGameAdapter();
        LiveTestSupport.Assert(adapter.Supports(process, build), "Updated offline game was not accepted.");
        LiveTestSupport.Assert(adapter.Supports(process, new("", "", "", "")),
            "Module still depends on a historical fingerprint whitelist.");
        var diagnostics = adapter.GetCompatibilityDiagnostics(process, build);
        LiveTestSupport.Assert(diagnostics.Any(item => item.DisplayName == "IL2CPP 语义结构" &&
            item.Status == GameCompatibilityDiagnosticStatus.Passed), "Current root resolution failed.");
        var loaded = adapter.SupportsCharacterAttributes(process);
        var features = new Dictionary<string, object>();
        if (loaded)
        {
            var character = adapter.ReadCharacters(process).Single();
            LiveTestSupport.Assert(character.Attributes.Count >= 10 && character.Level > 0, "Current character read was incomplete.");
            features["character"] = new { character.Level, Fields = character.Attributes.Count,
                Unavailable = character.Attributes.Where(field => !field.CanWrite).Select(field => new { field.Key, field.Status }).ToArray() };
            foreach (var editor in adapter.Editors.Where(editor => editor.Id != "game.last-epoch.character-attributes"))
            {
                var rows = adapter.ReadEditorEntities(process, editor.Id);
                if (editor.Id == "game.last-epoch.materials")
                    LiveTestSupport.Assert(rows.Count > 100 && rows.All(row => row.Fields.All(field => field.Value >= 0)),
                        "Updated material definitions/counts were incomplete.");
                if (editor.Id == "game.last-epoch.world")
                    LiveTestSupport.Assert(rows.Count > 0 && rows.SelectMany(row => row.Fields).All(field => field.Value is >= 20 and <= 120),
                        "Updated inline camera lens field resolution failed.");
                features[editor.Id] = new { Rows = rows.Count, Fields = rows.Sum(row => row.Fields.Count) };
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { ReadOnly = true, FingerprintWhitelistRequired = false,
            LoadedCharacter = loaded, build.GameAssemblySha256, build.MetadataSha256, Features = features }));
    }
}
