using System.Diagnostics;
using GameValueEditor.Modules.WorldApart;
using GameValueEditor.ModuleSdk;

namespace GameValueEditor.Modules.LiveTests;

public sealed class WorldApartLiveTest : IModuleLiveTest
{
    public string Game => "worldapart";

    public async Task RunAsync(string[] args)
    {
        using var liveProcess = Process.GetProcessesByName("WorldApart")
            .FirstOrDefault(candidate =>
            {
                try
                {
                    return candidate.Modules.Cast<ProcessModule>().Any(module =>
                        string.Equals(module.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase));
                }
                catch
                {
                    return false;
                }
            }) ?? throw new InvalidOperationException("WorldApart data process is not running.");
        var (process, build) = await LiveTestSupport.CreateContextAsync(liveProcess);
        var adapter = new WorldApartGameAdapter();
        LiveTestSupport.Assert(adapter.Supports(process, build),
            $"WorldApart module rejected the current build: exe={build.ExecutableSha256}, assembly={build.GameAssemblySha256}, metadata={build.MetadataSha256}");

        var inventory = ((IInventoryGameAdapter)adapter).ReadInventory(process);
        LiveTestSupport.Assert(inventory.Count > 0, "WorldApart inventory was empty.");
        var inventorySample = inventory.First(item => item.Count is >= 0 and <= int.MaxValue);
        var inventorySameValue = adapter.WriteField(process, inventorySample.FieldKey, inventorySample.CountDisplay);
        LiveTestSupport.Assert(inventorySameValue.DisplayValue == inventorySample.CountDisplay,
            "WorldApart inventory same-value write verification failed.");

        var characterAdapter = (ICharacterAttributesGameAdapter)adapter;
        var character = characterAdapter.ReadCharacters(process).Single();
        var attribute = character.Attributes.First(candidate => candidate.CanWrite && candidate.RawValue >= 0);
        var characterSameValue = characterAdapter.WriteCharacterAttribute(
            process, character.CharacterId, attribute.Key, attribute.RawValue);
        LiveTestSupport.Assert(characterSameValue.Attributes.Single(candidate => candidate.Key == attribute.Key).RawValue ==
                               attribute.RawValue,
            "WorldApart character same-value write verification failed.");

        var declared = ((IGameVersionMetadataProvider)adapter).ReadGameVersionMetadata(process);
        LiveTestSupport.Assert(!string.IsNullOrWhiteSpace(declared.Version) &&
                               !string.IsNullOrWhiteSpace(declared.ProductName),
            "WorldApart game-declared version metadata was empty.");
    }
}
