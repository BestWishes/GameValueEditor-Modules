using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using GameValueEditor.Modules.Deskrawl;

namespace GameValueEditor.Modules.LiveTests;

// Research-only, reads metadata/code and prints contracts. Never calls a candidate.
public sealed class DeskrawlContractCaptureTest : IModuleLiveTest
{
    public string Game => "deskrawl-contract-capture";
    public async Task RunAsync(string[] args)
    {
        using var game = Process.GetProcessesByName("Deskrawl").Single();
        var (process, _) = await LiveTestSupport.CreateContextAsync(game);
        var assembly = typeof(DeskrawlGameAdapter).Assembly;
        var runtimeType = assembly.GetType("GameValueEditor.Modules.Runtime.Il2CppRuntimeResolver", true)!;
        using var runtime = (IDisposable)Activator.CreateInstance(runtimeType, process)!;
        var method = runtimeType.GetMethod("Method", [typeof(string), typeof(string), typeof(string), typeof(string), typeof(bool), typeof(string), typeof(string[])])!;
        var codeType = assembly.GetType("GameValueEditor.Modules.Deskrawl.DeskrawlSemanticCode", true)!;
        var fingerprint = codeType.GetMethod("Fingerprint", BindingFlags.NonPublic | BindingFlags.Static)!;
        var canonicalMethod = codeType.GetMethod("Canonical", BindingFlags.NonPublic | BindingFlags.Static)!;
        var hashMethod = codeType.GetMethod("Hash", BindingFlags.NonPublic | BindingFlags.Static)!;
        var moduleBase = (ulong)runtimeType.GetProperty("ModuleBase")!.GetValue(runtime)!;
        var moduleEnd = (ulong)runtimeType.GetProperty("ModuleEnd")!.GetValue(runtime)!;
        using var stream = File.OpenRead(Path.Combine(Path.GetDirectoryName(process.ExecutablePath)!, "GameAssembly.dll"));
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        using var mapping = JsonDocument.Parse(File.ReadAllText(args.Single(arg => arg.StartsWith("--mapping="))[10..]));
        var contracts = new List<object>();
        foreach (var entry in mapping.RootElement.EnumerateArray())
        {
            var role = entry.GetProperty("Role").GetString()!;
            var candidates = entry.GetProperty("Matches").EnumerateArray().ToArray();
            LiveTestSupport.Assert(candidates.Length == 1, "Research mapping is not unique: " + role);
            var owner = candidates[0].GetProperty("Class").GetString()!;
            var signature = candidates[0].GetProperty("Signature").GetString()!;
            var split = signature.IndexOf(' ');
            var result = signature[..split];
            var open = signature.IndexOf('(');
            var name = signature[(split + 1)..open];
            var parameters = signature[(open + 1)..^1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Split(' ')[0]).ToArray();
            var isStatic = role.StartsWith("hh|") || role.StartsWith("gh|") ||
                role is "GameManager|dkg" or "Inventory|fbq" or "PlayerData|fmc" or "PlayerTalentBook|fnu" or
                    "SaveSystem|edw" or "Player|fko" or "LootManager|duh" or "FishingMapData|ccq";
            var image = entry.GetProperty("Assembly").GetString()! + ".dll";
            var ns = image == "ACTk.Runtime.dll" ? "CodeStage.AntiCheat.ObscuredTypes" : "";
            var native = method.Invoke(runtime, [image, ns, owner, name, isStatic, result, parameters])!;
            var address = (ulong)native.GetType().GetProperty("Pointer")!.GetValue(native)!;
            var count = entry.GetProperty("Instructions").GetInt32();
            var block = pe.GetSectionData(checked((int)(address - moduleBase)));
            var bytes = block.GetContent(0, Math.Min(block.Length, count * 15)).ToArray();
            var hash = (string)fingerprint.Invoke(null, [bytes, address, count, moduleBase, moduleEnd])!;
            var canonical = (string)canonicalMethod.Invoke(null, [bytes, address, count, moduleBase, moduleEnd])!;
            var displacement = role switch
            {
                "PlayerData|fmg" => ("mov rcx,[rax+0E0h]", "mov rcx,[rax+0D8h]"),
                "PlayerTalentBook|fot" => ("mov rcx,[rax+38h]", "mov rcx,[rax+30h]"),
                "Player|fkt" => ("mov byte ptr [rcx+160h],1", "mov byte ptr [rcx+158h],1"),
                "Player|bjy" => ("mov r8,[rdi+350h]", "mov r8,[rdi+328h]"),
                "hh|dzd" => ("mov rcx,[rax+48h]", "mov rcx,[rax+40h]"),
                _ => ("", "")
            };
            string? legacyHash = null;
            if (displacement.Item1.Length > 0)
            {
                LiveTestSupport.Assert(canonical.Split(displacement.Item1).Length == 2,
                    "Reviewed member displacement was not unique: " + role);
                legacyHash = (string)hashMethod.Invoke(null, [canonical.Replace(displacement.Item1, displacement.Item2, StringComparison.Ordinal)])!;
            }
            contracts.Add(new { Role = role, Image = image, Namespace = ns, Owner = owner,
                Static = isStatic, Result = result, Parameters = parameters, Instructions = count, Hash = hash, LegacyHash = legacyHash });
        }
        Console.WriteLine(JsonSerializer.Serialize(contracts));
    }
}
