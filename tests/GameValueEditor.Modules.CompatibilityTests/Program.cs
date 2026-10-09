using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using GameValueEditor.ModuleSdk;
using GameValueEditor.Modules.Fzzml;
using GameValueEditor.Modules.WorldApart;
using GameValueEditor.Modules.Runtime;
using Iced.Intel;

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string description)
    { _checks++; if (!condition) throw new InvalidOperationException(description); }
    private static void Reject(Action action, string description)
    { try { action(); } catch (Exception error) when (error is InvalidOperationException or System.IO.InvalidDataException) { _checks++; return; } throw new InvalidOperationException(description); }
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--embedded-only"))
            {
                TestIsolatedDependencies();
                Console.WriteLine($"Isolated embedded dependency checks passed: {_checks}.");
                return 0;
            }
            TestTypes(); TestRelocation(); TestHookInOwnProcess(); TestFloatingHookInOwnProcess(); TestManifests();
            if (args.Contains("--read-only-fzzml")) ReadOnlyFzzml();
            if (args.Contains("--read-only-worldapart")) ReadOnlyWorldApart();
            Console.WriteLine($"Compatibility checks passed: {_checks}. No player values or save files written.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void TestIsolatedDependencies()
    {
        foreach (var name in new[] { "Fzzml", "WorldApart" })
        {
            var context = new AssemblyLoadContext($"isolated-{name}", isCollectible: true);
            context.Resolving += (_, reference) => reference.Name == "GameValueEditor.ModuleSdk" ? typeof(GameProcessContext).Assembly : null;
            try
            {
                var assembly = context.LoadFromAssemblyPath(System.IO.Path.Combine(AppContext.BaseDirectory, $"GameValueEditor.Modules.{name}.dll"));
                Check(assembly.GetTypes().Any(type => type.Name.EndsWith("GameAdapter", StringComparison.Ordinal)), "Host cannot enumerate standalone module types before initialization");
                var hook = assembly.GetType("GameValueEditor.Modules.Runtime.Il2CppMainThreadHook")!;
                var bytes = Enumerable.Repeat((byte)0x90, 14).ToArray();
                Check((int)hook.GetMethod("InstructionLength")!.Invoke(null, [bytes, 0x10000000UL])! == 14, "Isolated module decoder failed");
                var decoder = context.Assemblies.Single(candidate => candidate.GetName().Name == "Iced");
                Check(string.IsNullOrEmpty(decoder.Location), "Decoder depended on an external DLL rather than embedded bytes");
                Check(AssemblyLoadContext.GetLoadContext(decoder) == context, "Decoder loaded into the wrong host context");
            }
            finally { context.Unload(); }
        }
    }

    private static void TestTypes()
    {
        Check(Il2CppRuntimeResolver.SameType("System.Int32", "System.Int32"), "Int32 type");
        Check(!Il2CppRuntimeResolver.SameType("System.Int64", "System.Int32"), "Changed field width accepted");
        Check(!Il2CppRuntimeResolver.SameType("Other.SaveManager", "Script.Manager.Save.SaveManager"), "Wrong namespace accepted");
        Check(!Il2CppRuntimeResolver.SameType("System.Collections.Generic.List<System.String>", "System.Collections.Generic.List<System.Int32>"), "Wrong generic element accepted");
        Check(!Il2CppRuntimeResolver.SameType("System.Int32&", "System.Int32"), "By-reference mismatch accepted");
        Check(Il2CppRuntimeResolver.IsNamedGame(new(1, "fzzml", "fzzml.exe", DateTime.UtcNow), "fzzml", "放置斩魔录"), "Fzzml alias lost");
        Check(Il2CppRuntimeResolver.IsNamedGame(new(1, "WorldApart", "WorldApart.exe", DateTime.UtcNow), "WorldApart", "不问凡尘"), "WorldApart alias lost");
        Check(!Il2CppRuntimeResolver.IsNamedGame(new(1, "unrelated", "unrelated.exe", DateTime.UtcNow), "fzzml"), "Unrelated process accepted");
    }
    private static void TestRelocation()
    {
        var prologues = new[] {
            "4883EC28803D3555A800007513488BC14885C07403",
            "4883EC28488B49104885C9740B33D24883C428E9D8DC31FF",
            "40554883EC30488BEC48894C24404889542448",
            "488B05100000004885C00F840A0000004883EC28"
        };
        foreach (var text in prologues)
        {
            var bytes = Convert.FromHexString(text);
            var length = Il2CppMainThreadHook.InstructionLength(bytes, 0x10000000);
            Check(length >= 14 && length <= bytes.Length, "Instruction boundary is incorrect");
            var relocated = Il2CppMainThreadHook.Relocate(bytes[..length], 0x10000000, 0x11000000);
            Check(relocated.Length >= length, "Relocated block missing continuation");
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(relocated)); decoder.IP = 0x11000000;
            var first = decoder.Decode(); Check(!first.IsInvalid, "Invalid relocated instruction");
        }
        var jump = Il2CppMainThreadHook.Jump(0x123456789ABC, 18);
        Check(jump[0] == 0xFF && jump[1] == 0x25 && BitConverter.ToUInt64(jump, 6) == 0x123456789ABC, "Jump clobbers a register");
        Reject(() => Il2CppMainThreadHook.InstructionLength(Convert.FromHexString("C390909090909090909090909090"), 0x1000), "Too-short method accepted");
    }
    private static void TestHookInOwnProcess()
    {
        // Synthetic native function, allocated in THIS test process, never a game.
        var allocation = VirtualAlloc(IntPtr.Zero, 4096, 0x3000, 0x40);
        if (allocation == IntPtr.Zero) throw new InvalidOperationException("Synthetic native allocation failed");
        try
        {
            var address = unchecked((ulong)allocation.ToInt64());
            var status = address + 0xF00; var counter = address + 0xF08;
            var body = new byte[] { 0x48, 0xB8 }.Concat(BitConverter.GetBytes(counter))
                .Concat(new byte[] { 0xFF, 0x00, 0x48, 0xB8 }).Concat(BitConverter.GetBytes(status))
                .Concat(new byte[] { 0xC7, 0x00, 1, 0, 0, 0 }).ToArray();
            var original = Enumerable.Repeat((byte)0x90, 14).ToArray();
            var native = original.Concat(new byte[] { 0x8B, 0xC1, 0x83, 0xC0, 7, 0xC3 }).ToArray();
            Marshal.Copy(native, 0, allocation, native.Length);
            var relocated = Il2CppMainThreadHook.Relocate(original, address, address + 0x800);
            Marshal.Copy(relocated, 0, (IntPtr)(long)(address + 0x800), relocated.Length);
            var wrapped = Il2CppMainThreadHook.Wrap(body, status, address + 0x800);
            Marshal.Copy(wrapped, 0, (IntPtr)(long)(address + 0x200), wrapped.Length);
            var patch = Il2CppMainThreadHook.Jump(address + 0x200, 14);
            Marshal.Copy(patch, 0, allocation, patch.Length);
            var function = Marshal.GetDelegateForFunctionPointer<NativeFunction>(allocation);
            Check(function(11) == 18, "Original RCX/continuation lost");
            Check(Marshal.ReadInt32((IntPtr)(long)status) == 1, "Callback status not completed");
            Check(function(37) == 44, "Fast path altered original function");
            Check(Marshal.ReadInt32((IntPtr)(long)counter) == 1, "Callback executed more than once");
            Parallel.For(0, 64, value => { if (function(value) != value + 7) throw new InvalidOperationException("Concurrent hook changed original result"); });
            Check(Marshal.ReadInt32((IntPtr)(long)counter) == 1, "Concurrent callback executed twice");
        }
        finally { VirtualFree(allocation, 0, 0x8000); }
    }
    private static void TestFloatingHookInOwnProcess()
    {
        var allocation = VirtualAlloc(IntPtr.Zero, 4096, 0x3000, 0x40);
        if (allocation == IntPtr.Zero) throw new InvalidOperationException("Synthetic floating allocation failed");
        try
        {
            var address = unchecked((ulong)allocation.ToInt64());
            var status = address + 0xF00;
            // Original function: add a RIP-relative constant to XMM0, then return.
            var original = new byte[] { 0xF2, 0x0F, 0x58, 0x05 }.Concat(BitConverter.GetBytes(0xF20 - 8))
                .Concat(Enumerable.Repeat((byte)0x90, 6)).ToArray();
            Marshal.Copy(original.Concat(new byte[] { 0xC3 }).ToArray(), 0, allocation, 15);
            Marshal.Copy(BitConverter.GetBytes(7d), 0, (IntPtr)(long)(address + 0xF20), 8);
            var relocated = Il2CppMainThreadHook.Relocate(original, address, address + 0x800);
            Marshal.Copy(relocated, 0, (IntPtr)(long)(address + 0x800), relocated.Length);
            var body = new byte[] { 0x66, 0x0F, 0xEF, 0xC0, 0x48, 0xB8 }.Concat(BitConverter.GetBytes(status))
                .Concat(new byte[] { 0xC7, 0x00, 1, 0, 0, 0 }).ToArray();
            var wrapped = Il2CppMainThreadHook.Wrap(body, status, address + 0x800);
            Marshal.Copy(wrapped, 0, (IntPtr)(long)(address + 0x200), wrapped.Length);
            Marshal.Copy(Il2CppMainThreadHook.Jump(address + 0x200, 14), 0, allocation, 14);
            var function = Marshal.GetDelegateForFunctionPointer<NativeFloatingFunction>(allocation);
            Check(function(11.5) == 18.5, "XMM0/RIP-relative relocation lost");
            Check(function(37.25) == 44.25, "Floating fast path altered original function");
        }
        finally { VirtualFree(allocation, 0, 0x8000); }
    }
    private static void TestManifests()
    {
        Check(new FzzmlGameAdapter().DisplayName.Contains("放置斩魔录"), "Wrong fzzml title");
        Check(new WorldApartGameAdapter().DisplayName.Contains("不问凡尘"), "Wrong WorldApart title");
        foreach (var assembly in new[] { typeof(FzzmlGameAdapter).Assembly, typeof(WorldApartGameAdapter).Assembly })
        {
            Check(assembly.GetManifestResourceNames().Contains("ModuleRuntime.Iced.dll"), "Standalone module is missing embedded decoder");
            var adapter = assembly.GetTypes().Single(t => t.Name is "FzzmlGameAdapter" or "WorldApartGameAdapter");
            Check(adapter.GetField("SupportedBuilds", BindingFlags.NonPublic | BindingFlags.Static) == null, "Historical RVA table still used");
        }
    }
    private static GameProcessContext Context(Process process) => new(process.Id, process.ProcessName, process.MainModule!.FileName, process.StartTime.ToUniversalTime());
    private static Process DataProcess(string name)
    {
        var candidates = Process.GetProcessesByName(name);
        var matches = candidates.Where(process => process.Modules.Cast<ProcessModule>().Any(module =>
            module.ModuleName.Equals("GameAssembly.dll", StringComparison.OrdinalIgnoreCase))).ToArray();
        if (matches.Length != 1)
        {
            foreach (var candidate in candidates) candidate.Dispose();
            throw new InvalidOperationException($"Expected one loaded {name} data process, found {matches.Length}.");
        }
        foreach (var candidate in candidates.Where(candidate => candidate != matches[0])) candidate.Dispose();
        return matches[0];
    }
    private static readonly GameBuildIdentity ChangedHash = new("changed-exe", "changed-build", "changed-code", "changed-metadata");
    private static void ReadOnlyFzzml()
    {
        using var process = DataProcess("fzzml"); var context = Context(process);
        var adapter = new FzzmlGameAdapter();
        Check(adapter.Supports(context, ChangedHash), "Current game rejected solely because hashes changed");
        var rows = adapter.ReadInventory(context); Check(rows.Count > 0, "Inventory empty");
        Console.WriteLine($"Fzzml read-only: {rows.Count} inventory types.");
        var sessionType = typeof(FzzmlGameAdapter).GetNestedType("CharacterSession", BindingFlags.NonPublic)!;
        using var session = (IDisposable)Activator.CreateInstance(sessionType, [context])!;
        var slots = (System.Collections.ICollection)sessionType.GetMethod("ReadCharacterSlots", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, null)!;
        Check(slots.Count > 0, "Raw character list empty");
        Console.WriteLine($"Fzzml read-only: {slots.Count} character slots; no aggregator hook or save called.");
        using var runtime = new Il2CppRuntimeResolver(context);
        var save = runtime.Class("Assembly-CSharp.dll", "Script.Manager.Save", "SaveManager");
        Console.WriteLine($"Current snapshot offset: 0x{runtime.Field(save, "_cachedSnapshot", "Script.Manager.Save.SaveManager/AllParsedData"):X}");
        // Resolve every write call without calling it.
        var layoutType = typeof(FzzmlGameAdapter).GetNestedType("BuildLayout", BindingFlags.NonPublic)!;
        var moduleRuntimeType = typeof(FzzmlGameAdapter).Assembly.GetType(typeof(Il2CppRuntimeResolver).FullName!)!;
        using var moduleRuntime = (IDisposable)Activator.CreateInstance(moduleRuntimeType, [context])!;
        var layout = Activator.CreateInstance(layoutType, [moduleRuntime])!;
        foreach (var property in layoutType.GetProperties()) { _ = property.GetValue(layout); _checks++; }
        const string configType = "Script.ScriptTableObj.Player.PlayerUnitConfig";
        const string saveType = "Script.Manager.Save.SaveManager";
        const string managerType = "Script.Manager.Core.ConfigManager";
        const string aggregateType = "Script.Player.Listen.AggregatedAttributes";
        _ = runtime.Method("Assembly-CSharp.dll", "Script.Manager.UI.CharacterMenu", "CharacterMenuRealmManager", "TryGetUnitConfigById", true, "System.Boolean", "System.String", configType + "&"); _checks++;
        _ = runtime.Method("Assembly-CSharp.dll", "Script.Manager.Core", "ConfigManager", "get_Instance", true, managerType); _checks++;
        _ = runtime.Method("Assembly-CSharp.dll", "Script.Player.Listen", "PlayerAttributeAggregator", "Compute", true, aggregateType, "System.String", saveType, managerType); _checks++;
        _ = runtime.Method("Assembly-CSharp.dll", "Script.Player.Listen", "PlayerAttributeEventHub", "RaiseAttributesChanged", true, "System.Void", "System.String", aggregateType); _checks++;
        var configClass = runtime.Class("Assembly-CSharp.dll", "Script.ScriptTableObj.Player", "PlayerUnitConfig");
        var aggregateClass = runtime.Class("Assembly-CSharp.dll", "Script.Player.Listen", "AggregatedAttributes");
        foreach (var key in new[] { "constitution", "strength", "spirit", "agility", "vitality" })
        {
            _ = runtime.Field(configClass, key, "System.Int32"); _checks++;
            _ = runtime.Field(aggregateClass, key, "System.Int32"); _checks++;
        }
        Reject(() => runtime.Field(configClass, "strength", "System.Int64"), "Changed actual field type accepted");
        Reject(() => runtime.Method("Assembly-CSharp.dll", "Script.Player.Listen", "PlayerAttributeAggregator", "Compute", true, aggregateType, "System.String", configType, managerType), "Wrong overload signature accepted");
    }
    private static void ReadOnlyWorldApart()
    {
        using var process = DataProcess("WorldApart"); var context = Context(process);
        var adapter = new WorldApartGameAdapter();
        Check(adapter.Supports(context, ChangedHash), "WorldApart changed hashes rejected");
        var inventory = adapter.ReadInventory(context); Check(inventory.Count > 0, "WorldApart inventory empty");
        var characters = adapter.ReadCharacters(context); Check(characters.Count == 1 && characters[0].Attributes.Count > 0, "WorldApart attributes empty");
        Console.WriteLine($"WorldApart read-only: {inventory.Count} inventory types, {characters[0].Attributes.Count} attributes.");
        var layoutType = typeof(WorldApartGameAdapter).GetNestedType("BuildLayout", BindingFlags.NonPublic)!;
        var runtimeType = typeof(WorldApartGameAdapter).Assembly.GetType(typeof(Il2CppRuntimeResolver).FullName!)!;
        using var runtime = (IDisposable)Activator.CreateInstance(runtimeType, [context])!;
        var layout = Activator.CreateInstance(layoutType, [runtime])!;
        foreach (var property in layoutType.GetProperties()) { _ = property.GetValue(layout); _checks++; }
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NativeFunction(int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate double NativeFloatingFunction(double value);
    [DllImport("kernel32.dll")] private static extern IntPtr VirtualAlloc(IntPtr address, nuint size, uint type, uint protect);
    [DllImport("kernel32.dll")] private static extern bool VirtualFree(IntPtr address, nuint size, uint type);
}
