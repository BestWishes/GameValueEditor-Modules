using System.Reflection;
using GameValueEditor.Modules.Deskrawl;

namespace GameValueEditor.Modules.LiveTests;

public sealed class DeskrawlSemanticCodeTest : IModuleLiveTest
{
    public string Game => "deskrawl-semantic-code";
    public Task RunAsync(string[] args)
    {
        var type = typeof(DeskrawlGameAdapter).Assembly.GetType("GameValueEditor.Modules.Deskrawl.DeskrawlSemanticCode", true)!;
        var method = type.GetMethod("Fingerprint", BindingFlags.NonPublic | BindingFlags.Static)!;
        const ulong module = 0x180000000;
        string Hash(byte[] bytes, ulong rva, int count) => (string)method.Invoke(null, [bytes, module + rva, count, module, module + 0x1000000])!;
        byte[] body = [0x48, 0x8B, 0x05, 0x20, 0x20, 0x00, 0x00, 0x83, 0xF8, 0x01, 0x75, 0x01, 0x90, 0xC3];
        var original = Hash(body, 0x1000, 5);
        LiveTestSupport.Assert(original == Hash(body, 0x3000, 5), "Code/data relocation changed semantic identity.");
        var changed = (byte[])body.Clone(); changed[9] = 2;
        LiveTestSupport.Assert(original != Hash(changed, 0x1000, 5), "Changed business constant was accepted.");
        changed = (byte[])body.Clone(); changed[11] = 0;
        LiveTestSupport.Assert(original != Hash(changed, 0x1000, 5), "Changed local branch destination was accepted.");
        LiveTestSupport.Assert(Hash([0x48, 0x8B, 0x40, 0x20, 0xC3], 0x1000, 2) !=
                               Hash([0x48, 0x8B, 0x40, 0x28, 0xC3], 0x1000, 2), "Changed member displacement was accepted.");
        LiveTestSupport.Assert(Hash([0x48, 0xB8, 0, 0, 0, 0x80, 1, 0, 0, 0, 0xC3], 0x1000, 2) !=
                               Hash([0x48, 0xB8, 1, 0, 0, 0x80, 1, 0, 0, 0, 0xC3], 0x1000, 2), "An address-looking integer literal was erased.");
        var unique = type.GetMethod("Unique", BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(typeof(int));
        LiveTestSupport.Assert((int)unique.Invoke(null, [new[] { 7 }, "fixture"])! == 7, "Unique semantic candidate was rejected.");
        foreach (var candidates in new[] { Array.Empty<int>(), new[] { 7, 9 } })
        {
            var rejected = false;
            try { unique.Invoke(null, [candidates, "fixture"]); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { rejected = true; }
            LiveTestSupport.Assert(rejected, "Missing/ambiguous semantic candidate was accepted.");
        }
        Console.WriteLine("Semantic code fixtures passed: relocation, constants, branches, members, literal operands, unique/missing/ambiguous candidates. No game access.");
        return Task.CompletedTask;
    }
}
