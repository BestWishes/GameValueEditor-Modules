using System.Diagnostics;
using GameValueEditor.Modules.Fzzml;
using GameValueEditor.ModuleSdk;

namespace GameValueEditor.Modules.LiveTests;

public sealed class FzzmlLiveTest : IModuleLiveTest
{
    public string Game => "fzzml";

    public async Task RunAsync(string[] args)
    {
        using var liveProcess = Process.GetProcessesByName("fzzml").FirstOrDefault()
                                ?? throw new InvalidOperationException("fzzml is not running.");
        var (process, build) = await LiveTestSupport.CreateContextAsync(liveProcess);
        var adapter = new FzzmlGameAdapter();
        LiveTestSupport.Assert(adapter.Supports(process, build),
            $"fzzml module rejected the current build: exe={build.ExecutableSha256}, assembly={build.GameAssemblySha256}, metadata={build.MetadataSha256}");
        var liveValue = adapter.ReadField(process, "赤阳花");
        LiveTestSupport.Assert(long.TryParse(liveValue.DisplayValue, out var liveCount) && liveCount >= 0,
            $"Unexpected live inventory count: {liveValue.DisplayValue}");
        var inventory = ((IInventoryGameAdapter)adapter).ReadInventory(process);
        LiveTestSupport.Assert(inventory.Single(item => item.FieldKey == "赤阳花").Count == liveCount,
            "Inventory enumeration and keyed read disagree.");

        var characterAdapter = (ICharacterAttributesGameAdapter)adapter;
        LiveTestSupport.Assert(characterAdapter.SupportsCharacterAttributes(process),
            "Character attributes are unavailable for the current build.");
        var characters = characterAdapter.ReadCharacters(process);
        LiveTestSupport.Assert(characters.Count > 0 && characters.All(character => character.Attributes.Count == 5),
            "Character editor did not enumerate five dimensions per character.");
        var firstCharacter = characters[0];
        var firstAttribute = firstCharacter.Attributes[0];
        var sameValue = characterAdapter.WriteCharacterAttribute(
            process, firstCharacter.CharacterId, firstAttribute.Key, firstAttribute.RawValue);
        LiveTestSupport.Assert(sameValue.Attributes.Single(attribute => attribute.Key == firstAttribute.Key).RawValue ==
                               firstAttribute.RawValue,
            "Character same-value main-thread validation failed.");

        var setArgument = args.FirstOrDefault(argument =>
            argument.StartsWith("--set=", StringComparison.OrdinalIgnoreCase));
        if (setArgument is not null)
        {
            var requested = setArgument[(setArgument.IndexOf('=') + 1)..];
            LiveTestSupport.Assert(int.TryParse(requested, out var requestedValue) && requestedValue is >= 0 and <= 1000,
                "The optional inventory write is limited to 0-1000.");
            var written = adapter.WriteField(process, "赤阳花", requested);
            LiveTestSupport.Assert(written.DisplayValue == requested, "Inventory write verification failed.");
        }
    }
}
