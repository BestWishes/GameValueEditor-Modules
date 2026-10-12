using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using GameValueEditor.ModuleSdk;
using GameValueEditor.Modules.LastEpoch;

internal static class Program
{
    private static int _assertions;
    private static readonly string MaterialsEditorId = (string)typeof(LastEpochGameAdapter)
        .GetField("MaterialsEditorId", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
    private static void Check(bool condition, string message) { _assertions++; if (!condition) throw new InvalidOperationException(message); }
    [STAThread]
    private static int Main()
    {
        Exception? failure = null;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
        {
            try { CheckRuntimeLayouts(); CheckNativeSessionGate(); await RunAsync(); await CheckRefreshAsync(); }
            catch (Exception error) { failure = error; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        Dispatcher.Run();
        if (failure is not null) { Console.Error.WriteLine(failure); return 1; }
        Console.WriteLine($"Module real-page write/snapshot regressions passed: {_assertions} assertions; no game or file writes.");
        return 0;
    }
    private static void CheckRuntimeLayouts()
    {
        var guard = typeof(LastEpochGameAdapter).Assembly.GetType("GameValueEditor.Modules.LastEpoch.LastEpochBuildGuard")!;
        Check(guard.GetMethod("IsVerifiedBuild", BindingFlags.Static | BindingFlags.NonPublic) is null &&
            !guard.Assembly.GetManifestResourceNames().Any(name => name.Contains("VerifiedBuilds", StringComparison.Ordinal)),
            "Historical hash whitelist still controls module connection");
        try
        {
            guard.GetMethod("EnsureCurrentProcess", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [Environment.ProcessId, null]);
            throw new InvalidOperationException("Non-game process accepted");
        }
        catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { _assertions++; }
        var runtime = typeof(LastEpochGameAdapter).Assembly.GetType("GameValueEditor.Modules.LastEpoch.LastEpochRuntime")!;
        var valueAddress = runtime.GetMethod("DictionaryEntryValueAddress", BindingFlags.Static | BindingFlags.NonPublic)!;
        Check((ulong)valueAddress.Invoke(null, [0x1000UL])! == 0x1010UL, "Inline Dictionary entry was mistaken for a game object");
        var payloadBuilder = runtime.GetMethod("BuildExperienceGainHookPayload", BindingFlags.Static | BindingFlags.NonPublic)!;
        var sessionType = runtime.GetNestedType("SessionGuard", BindingFlags.NonPublic)!;
        var session = Activator.CreateInstance(sessionType, [0x1000UL, 0x2000UL, 0x3000UL, 0x4000UL, 0x20, 0x5000UL])!;
        foreach (var offset in new[] { 0x20, 0x100, 0x234 })
        {
            var payload = (byte[])payloadBuilder.Invoke(null, [0x100000UL, 0x200000UL, 0x300000UL, 0x400000UL, offset, session])!;
            var instruction = new byte[] { 0x48, 0x8B, 0x89 }.Concat(BitConverter.GetBytes(offset)).ToArray();
            Check(payload.Length == 0x300 && Enumerable.Range(0, 0x200 - instruction.Length + 1)
                .Any(index => payload.AsSpan(index, instruction.Length).SequenceEqual(instruction)), "Experience hook did not use the current stats offset");
            Check(BitConverter.ToUInt64(payload, 0x240) == 0x5000, "Persistent hook did not bind its character");
        }
        foreach (var name in new[] { "BuildItemDropHookPayload", "BuildGoldQuantityHookPayload" })
        {
            var payload = (byte[])runtime.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [0x100000UL, 0x200000UL, 2f, session])!;
            Check(payload.Length == 0x300 && BitConverter.ToUInt64(payload, 0x240) == 0x5000,
                "Reward hook did not bind its current character: " + name);
        }
        Check(runtime.GetMethod("IsOfflineProcess", BindingFlags.Static | BindingFlags.Public) is null,
            "Command-line launch gate was retained");
        var diagnostics = new LastEpochGameAdapter().GetCompatibilityDiagnostics(
            new(Environment.ProcessId, "unrelated", "", DateTime.UtcNow), new("", "", "", ""));
        Check(diagnostics.Single(item => item.DisplayName == "IL2CPP 语义结构").Status == GameCompatibilityDiagnosticStatus.Information,
            "An unperformed structure check was reported as failed");
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int NativeGate(int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate float NativeDrop(float value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int NativeGold(IntPtr a, IntPtr b, int quantity, IntPtr d);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate long NativeExperience(IntPtr tracker, long quantity, IntPtr c, IntPtr d);
    [DllImport("kernel32.dll")] private static extern IntPtr VirtualAlloc(IntPtr address, nuint size, uint allocation, uint protect);
    [DllImport("kernel32.dll")] private static extern bool VirtualFree(IntPtr address, nuint size, uint free);

    private static void CheckNativeSessionGate()
    {
        // Execute the production gate against synthetic objects in THIS process, never the game.
        var memory = VirtualAlloc(IntPtr.Zero, 4096, 0x3000, 0x40);
        if (memory == IntPtr.Zero) throw new InvalidOperationException("Native gate fixture allocation failed");
        try
        {
            ulong address = unchecked((ulong)memory.ToInt64());
            IntPtr Ptr(int offset) => IntPtr.Add(memory, offset);
            var runtime = typeof(LastEpochGameAdapter).Assembly.GetType("GameValueEditor.Modules.LastEpoch.LastEpochRuntime")!;
            var emitterType = runtime.GetNestedType("Emitter", BindingFlags.NonPublic)!;
            var code = Activator.CreateInstance(emitterType)!;
            void Emit(params byte[] bytes) => emitterType.GetMethod("Emit")!.Invoke(code, [bytes]);
            Emit(0x8B, 0xC1, 0x83, 0xF9, 0x05); // mov eax, ecx; cmp ecx, 5 (test preserved RAX/flags)
            var sessionType = runtime.GetNestedType("SessionGuard", BindingFlags.NonPublic)!;
            var session = Activator.CreateInstance(sessionType,
                [address + 0x800, address + 0x801, address + 0x808, address + 0x810, 0x20, address + 0xA00])!;
            var denied = runtime.GetMethod("EmitSessionGate", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [code, session, address + 0x818])!;
            // LEA does not change flags. Only the admitted session receives the test modifier.
            Emit(0x8D, 0x80, 100, 0, 0, 0);
            runtime.GetMethod("EmitSessionBypass", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [code, denied]);
            Emit(0x83, 0xD0, 0x00, 0xC3); // adc eax, 0; ret
            var bytes = (byte[])emitterType.GetMethod("ToArray")!.Invoke(code, null)!;
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            var function = Marshal.GetDelegateForFunctionPointer<NativeGate>(memory);
            void Reset()
            {
                Marshal.WriteByte(Ptr(0x800), 0); Marshal.WriteByte(Ptr(0x801), 0);
                Marshal.WriteInt64(Ptr(0x808), unchecked((long)(address + 0x900)));
                Marshal.WriteInt64(Ptr(0x810), unchecked((long)(address + 0xB00)));
                Marshal.WriteInt64(Ptr(0x818), unchecked((long)(address + 0xA00)));
                Marshal.WriteInt64(Ptr(0x920), unchecked((long)(address + 0xA00)));
            }
            foreach (var scenario in new[] { "offline", "online", "server", "unknown", "no-tracker", "no-actor", "new-character", "rebound" })
            {
                Reset();
                switch (scenario)
                {
                    case "online": Marshal.WriteByte(Ptr(0x800), 1); break;
                    case "server": Marshal.WriteByte(Ptr(0x801), 1); break;
                    case "unknown": Marshal.WriteByte(Ptr(0x800), 2); break;
                    case "no-tracker": Marshal.WriteInt64(Ptr(0x808), 0); break;
                    case "no-actor": Marshal.WriteInt64(Ptr(0x810), 0); break;
                    case "new-character": Marshal.WriteInt64(Ptr(0x920), unchecked((long)(address + 0xA40))); break;
                    case "rebound": Marshal.WriteInt64(Ptr(0x920), unchecked((long)(address + 0xA40))); Marshal.WriteInt64(Ptr(0x818), unchecked((long)(address + 0xA40))); break;
                }
                bool allowed = scenario is "offline" or "rebound";
                foreach (int value in new[] { 3, 5, 8 })
                    Check(function(value) == value + (value < 5 ? 1 : 0) + (allowed ? 100 : 0),
                        "Production gate damaged original registers/flags or allowed wrong session: " + scenario);
                Parallel.For(0, 64, value =>
                {
                    if (function(value) != value + (value < 5 ? 1 : 0) + (allowed ? 100 : 0))
                        throw new InvalidOperationException("Concurrent session gate changed original semantics");
                });
                Check(true, "Concurrent gate");
            }
            CheckPersistentPayloads(memory, address, runtime, session);
        }
        finally { VirtualFree(memory, 0, 0x8000); }
    }

    private static void CheckPersistentPayloads(IntPtr memory, ulong address, Type runtime, object session)
    {
        IntPtr Ptr(int offset) => IntPtr.Add(memory, offset);
        void Copy(int offset, byte[] bytes) => Marshal.Copy(bytes, 0, Ptr(offset), bytes.Length);
        byte[] U64(ulong value) => BitConverter.GetBytes(value);
        foreach (var kind in new[] { "item", "gold", "experience" })
        foreach (var state in new[] { "offline", "online", "no-character", "new-character" })
        {
            Marshal.WriteByte(Ptr(0x800), state == "online" ? (byte)1 : (byte)0);
            Marshal.WriteByte(Ptr(0x801), 0);
            Marshal.WriteInt64(Ptr(0x808), state == "no-character" ? 0 : unchecked((long)(address + 0x900)));
            Marshal.WriteInt64(Ptr(0x810), unchecked((long)(address + 0xB00)));
            Marshal.WriteInt64(Ptr(0x920), unchecked((long)(address + (state == "new-character" ? 0xA40UL : 0xA00UL))));
            Marshal.WriteInt64(Ptr(0xC20), unchecked((long)(address + 0xB00)));
            bool allowed = state == "offline";
            byte[] payload;
            if (kind == "experience")
            {
                payload = (byte[])runtime.GetMethod("BuildExperienceGainHookPayload", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, [address, address + 0x400, 0UL, address + 0x480, 0x20, session])!;
                Copy(0x480, [0xB8, 0, 0, 0x80, 0x3F, 0x66, 0x0F, 0x6E, 0xC0, 0xC3]); // GetTotalIncreased -> 1f
                Copy(0x40E, [0x48, 0x83, 0xC4, 0x30, 0x41, 0x5F, 0x41, 0x5E, 0x5E,
                    0x48, 0x8B, 0x5C, 0x24, 0x20, 0x48, 0x89, 0xD0, 0xC3]);
            }
            else
            {
                payload = (byte[])runtime.GetMethod(kind == "item" ? "BuildItemDropHookPayload" : "BuildGoldQuantityHookPayload",
                    BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [address, address + 0x400, 2f, session])!;
                if (kind == "gold")
                    Copy(0x410, [0x48, 0x83, 0xC4, 0x58, 0x41, 0x5E, 0x41, 0x5C, 0x5F, 0x5E, 0x5B, 0x5D,
                        0x44, 0x89, 0xC0, 0xC3]);
                else
                {
                    Copy(0x40E, [0xC3]);
                    // Harness supplies the original game's RSI/R14 and preserves Windows nonvolatiles.
                    var harness = new List<byte>([0x56, 0x41, 0x56, 0x48, 0x83, 0xEC, 0x38,
                        0xF3, 0x44, 0x0F, 0x7F, 0x64, 0x24, 0x20, 0x48, 0xBE]);
                    harness.AddRange(U64(address + 0xB00)); harness.AddRange([0x49, 0xBE]);
                    harness.AddRange(U64(address + 0xB00)); harness.AddRange([0x48, 0xB8]);
                    harness.AddRange(U64(address)); harness.AddRange([0xFF, 0xD0,
                        0xF3, 0x44, 0x0F, 0x6F, 0x64, 0x24, 0x20,
                        0x48, 0x83, 0xC4, 0x38, 0x41, 0x5E, 0x5E, 0xC3]);
                    Copy(0x600, harness.ToArray());
                }
            }
            Copy(0, payload);
            if (kind == "item")
            {
                var function = Marshal.GetDelegateForFunctionPointer<NativeDrop>(Ptr(0x600));
                Check(function(21f) == (allowed ? 42f : 21f), "Packaged item payload did not bypass correctly: " + state);
            }
            else if (kind == "gold")
            {
                var function = Marshal.GetDelegateForFunctionPointer<NativeGold>(memory);
                Check(function(IntPtr.Zero, IntPtr.Zero, 21, IntPtr.Zero) == (allowed ? 42 : 21),
                    "Packaged gold payload did not preserve original quantity: " + state);
            }
            else
            {
                var function = Marshal.GetDelegateForFunctionPointer<NativeExperience>(memory);
                Check(function(Ptr(0xC00), 21, IntPtr.Zero, IntPtr.Zero) == (allowed ? 42 : 21),
                    "Packaged experience payload did not preserve original settlement: " + state);
            }
            Check(Marshal.ReadInt64(Ptr(0x210)) == (allowed ? 1 : 0), "Bypassed payload still recorded a modification: " + kind + "/" + state);
        }
    }
    private static Task Call(IGameEditorPage page, string name)
    {
        try { return (Task)page.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null)!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static async Task RunAsync()
    {
        foreach (var kind in new[] { "inventory", "character", "entity", "materials" })
        foreach (var scenario in new[] { "normal", "prompt-cancel", "expire-after-prompt", "failure", "missing-write" })
        {
            using var lifetime = new CancellationTokenSource();
            var adapter = new FakeAdapter();
            var host = new BridgeHost { Prompt = scenario == "prompt-cancel" ? null : "20" };
            if (scenario == "expire-after-prompt") host.PromptReturned = lifetime.Cancel;
            if (scenario == "failure") host.Failure = new InvalidOperationException("bridge failure");
            var context = new GameEditorPageContext(new(1, "simulation", "", DateTime.UnixEpoch), new("", "test", "", ""),
                scenario == "missing-write" ? new ReadOnlyHost() : host, lifetime.Token);
            var typeName = kind switch { "inventory" => "GameValueEditor.Modules.Ui.InventoryEditorPage",
                "character" => "GameValueEditor.Modules.Ui.CharacterEditorPage", "entity" => "GameValueEditor.Modules.Ui.EntityEditorPage",
                _ => "GameValueEditor.Modules.LastEpoch.LastEpochMaterialsEditorPage" };
            object[] arguments = kind switch { "inventory" => [adapter, context, "test"],
                "character" => [adapter, context, "test.character", "test"], "entity" => [adapter, context, "test.entity", "test"], _ => [adapter, context] };
            using var page = (IGameEditorPage)Activator.CreateInstance(typeof(LastEpochGameAdapter).Assembly.GetType(typeName)!, arguments)!;
            await Call(page, "RefreshAsync");
            if (scenario == "normal")
            {
                await Call(page, "EditSelectedAsync");
                var expected = ModuleFieldKey.Create(kind switch { "inventory" => "test.inventory", "character" => "test.character",
                    "entity" => "test.entity", _ => MaterialsEditorId }, "item", kind == "materials" ? "quantity" : "count");
                Check(host.Writes == 1 && host.Key == expected && host.Value == "20", "Real page bypassed bridge or changed its semantic key: " + kind);
                Check(adapter.DirectWrites == 0, "Real page directly invoked adapter.WriteField: " + kind);
                continue;
            }
            if (scenario == "prompt-cancel") await Call(page, "EditSelectedAsync");
            else
            {
                try { await Call(page, "EditSelectedAsync"); throw new InvalidOperationException("Expected page failure."); }
                catch (OperationCanceledException) when (scenario == "expire-after-prompt") { _assertions++; }
                catch (InvalidOperationException e) when (scenario == "failure" && e.Message == "bridge failure" ||
                    scenario == "missing-write" && e.Message.Contains("更新主程序", StringComparison.Ordinal)) { _assertions++; }
            }
            Check(adapter.DirectWrites == 0 && host.Writes == (scenario == "failure" ? 1 : 0), "Cancelled/failed/old-host page fell back to a direct adapter write: " + kind);
        }
    }
    private static IGameEditorPage CreatePage(string kind, FakeAdapter adapter, IGameEditorHostServices host, CancellationToken lifetime)
    {
        var context = new GameEditorPageContext(new(1, "simulation", "", DateTime.UnixEpoch), new("", "test", "", ""), host, lifetime);
        var typeName = kind switch { "inventory" => "GameValueEditor.Modules.Ui.InventoryEditorPage",
            "character" => "GameValueEditor.Modules.Ui.CharacterEditorPage", "entity" => "GameValueEditor.Modules.Ui.EntityEditorPage",
            _ => "GameValueEditor.Modules.LastEpoch.LastEpochMaterialsEditorPage" };
        object[] arguments = kind switch { "inventory" => [adapter, context, "test"],
            "character" => [adapter, context, "test.character", "test"], "entity" => [adapter, context, "test.entity", "test"], _ => [adapter, context] };
        return (IGameEditorPage)Activator.CreateInstance(typeof(LastEpochGameAdapter).Assembly.GetType(typeName)!, arguments)!;
    }
    private static string PageValue(IGameEditorPage page, string kind)
    {
        var name = kind switch { "inventory" => "_items", "character" => "_characters", "entity" => "_entities", _ => "_allEntities" };
        var items = (System.Collections.IEnumerable)page.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        var first = items.Cast<object>().Single();
        return first switch { AdapterInventoryItem item => item.CountDisplay,
            AdapterCharacterItem character => character.Attributes.Single().RawValueDisplay,
            AdapterEditorEntity entity => entity.Fields.Single().ValueDisplay, _ => throw new InvalidOperationException("Unexpected row.") };
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task CheckRefreshAsync()
    {
        foreach (var kind in new[] { "inventory", "character", "entity", "materials" })
        foreach (var scenario in new[] { "normal", "changed", "run-stale", "late-error", "real-error", "busy", "expired", "missing-capability" })
        {
            using var lifetime = new CancellationTokenSource();
            var adapter = new FakeAdapter(); var host = new BridgeHost();
            host.Written = value => adapter.Value = long.Parse(value);
            using var page = CreatePage(kind, adapter, scenario == "missing-capability" ? new NoSnapshotHost() : host, lifetime.Token);
            if (scenario == "missing-capability")
            {
                try { await Call(page, "RefreshAsync"); throw new InvalidOperationException("Missing snapshot capability was accepted."); }
                catch (InvalidOperationException e) when (e.Message.Contains("安全刷新", StringComparison.Ordinal)) { _assertions++; }
                Check(adapter.Reads == 0, "New page silently fell back to an uncoordinated read: " + kind);
                continue;
            }
            await Call(page, "RefreshAsync");
            Check(PageValue(page, kind) == "10" && host.Applies == 1, "Initial page snapshot failed: " + kind);
            if (scenario == "busy")
            {
                host.Busy = true; var oldReads = adapter.Reads;
                try { await Call(page, "RefreshAsync"); throw new InvalidOperationException("Busy snapshot was applied."); }
                catch (GameEditorSnapshotChangedException) { _assertions++; }
                Check(adapter.Reads == oldReads && host.Applies == 1, "Busy page still entered a game read: " + kind);
                host.Busy = false; await Call(page, "RefreshAsync"); continue;
            }
            var entered = Gate(); var release = Gate();
            adapter.BeforeRead = () =>
            {
                entered.TrySetResult(); release.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                if (scenario is "late-error" or "real-error") throw new System.IO.IOException("old read error");
            };
            var reading = scenario == "run-stale"
                ? (Task)page.GetType().BaseType!.GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(page, [(Func<Task>)(() => Call(page, "RefreshAsync"))])!
                : Call(page, "RefreshAsync");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                if (scenario is "changed" or "late-error" or "run-stale")
                {
                    await host.WriteFieldAsync("key", "20"); adapter.BeforeRead = null;
                    await Call(page, "RefreshAsync");
                    Check(PageValue(page, kind) == "20", "New page snapshot did not display20: " + kind);
                }
                if (scenario == "expired") lifetime.Cancel();
            }
            finally { release.TrySetResult(); }
            if (scenario is "changed" or "late-error")
            {
                try { await reading; throw new InvalidOperationException("Obsolete page snapshot was accepted."); }
                catch (GameEditorSnapshotChangedException) { _assertions++; }
                Check(PageValue(page, kind) == "20" && host.Applies == 2, "Old real-page snapshot replaced a newer UI value: " + kind);
            }
            else if (scenario == "run-stale")
            {
                await reading;
                Check(PageValue(page, kind) == "20" && host.Applies == 2, "Guarded stale page refresh replaced new data: " + kind);
                Check(host.Errors == 0 && host.LastStatus.Contains("重新刷新", StringComparison.Ordinal) && page.View.IsEnabled,
                    "Stale refresh showed a game error or failed to re-enable the page: " + kind);
            }
            else if (scenario == "real-error")
            {
                try { await reading; throw new InvalidOperationException("Current read error was swallowed."); }
                catch (System.IO.IOException) { _assertions++; }
                Check(PageValue(page, kind) == "10" && host.Applies == 1, "Current read error mutated page data: " + kind);
            }
            else if (scenario == "expired")
            {
                try { await reading; throw new InvalidOperationException("Expired page applied a snapshot."); }
                catch (OperationCanceledException) { _assertions++; }
                Check(PageValue(page, kind) == "10" && host.Applies == 1, "Expired page result mutated data: " + kind);
            }
            else { await reading; Check(host.Applies == 2, "Unchanged real-page snapshot was rejected: " + kind); }
            Check(adapter.Reads == (scenario is "changed" or "late-error" or "run-stale" ? 3 : 2), "Page retried a stale/failed read automatically: " + kind);
            if (scenario != "expired")
            { adapter.BeforeRead = null; adapter.Value = 30; await Call(page, "RefreshAsync"); Check(PageValue(page, kind) == "30", "Explicit refresh did not recover: " + kind); }
        }
    }
    private sealed class BridgeHost : IGameEditorHostServices, IGameEditorFieldOperations, IGameEditorSnapshotOperations
    {
        internal string? Prompt;
        internal Action? PromptReturned;
        internal Exception? Failure;
        internal int Writes;
        internal string? Key, Value;
        internal Action<string>? Written;
        internal long Revision;
        internal bool Busy;
        internal int Applies;
        internal int Errors;
        internal string LastStatus = "";
        public Task<string?> PromptValueAsync(GameEditorTextPrompt prompt) { PromptReturned?.Invoke(); return Task.FromResult(Prompt); }
        public Task SaveFieldAsync(GameEditorSavedFieldRequest request) => Task.CompletedTask;
        public void ReportStatus(string message) => LastStatus = message;
        public void ShowError(string title, string message) => Errors++;
        public Task<AdapterFieldValue> WriteFieldAsync(string key, string value)
        { Writes++; Key = key; Value = value; Revision++; Written?.Invoke(value);
            return Failure is null ? Task.FromResult(new AdapterFieldValue(key, value, "confirmed")) : Task.FromException<AdapterFieldValue>(Failure); }
        public async Task ReadSnapshotAsync<T>(Func<T> read, Action<T> apply)
        {
            var start = Revision;
            if (Busy) throw new GameEditorSnapshotChangedException();
            T result;
            try { result = await Task.Run(read); }
            catch (Exception) when (start != Revision || Busy) { throw new GameEditorSnapshotChangedException(); }
            if (start != Revision || Busy) throw new GameEditorSnapshotChangedException();
            apply(result); Applies++;
        }
    }
    private sealed class ReadOnlyHost : IGameEditorHostServices, IGameEditorSnapshotOperations
    {
        public Task<string?> PromptValueAsync(GameEditorTextPrompt prompt) => Task.FromResult<string?>("20");
        public Task SaveFieldAsync(GameEditorSavedFieldRequest request) => Task.CompletedTask;
        public void ReportStatus(string message) { }
        public void ShowError(string title, string message) { }
        public async Task ReadSnapshotAsync<T>(Func<T> read, Action<T> apply) => apply(await Task.Run(read));
    }
    private sealed class NoSnapshotHost : IGameEditorHostServices
    {
        public Task<string?> PromptValueAsync(GameEditorTextPrompt prompt) => Task.FromResult<string?>(null);
        public Task SaveFieldAsync(GameEditorSavedFieldRequest request) => Task.CompletedTask;
        public void ReportStatus(string message) { }
        public void ShowError(string title, string message) { }
    }
    private sealed class FakeAdapter : IInventoryGameAdapter, ICharacterAttributesGameAdapter, IEntityEditorsGameAdapter
    {
        internal int DirectWrites;
        internal long Value = 10;
        internal int Reads;
        internal Action? BeforeRead;
        public string Id => "game.page-test";
        public string DisplayName => "simulation";
        public string Description => "isolated";
        public IReadOnlyList<GameEditorDescriptor> Editors => [];
        public bool Supports(GameProcessContext process, GameBuildIdentity build) => true;
        public AdapterFieldValue ReadField(GameProcessContext process, string key) => new(key, "10", "read");
        public AdapterFieldValue WriteField(GameProcessContext process, string key, string value)
        { DirectWrites++; throw new InvalidOperationException("Direct writes are forbidden in this test."); }
        public IReadOnlyList<AdapterInventoryItem> ReadInventory(GameProcessContext process)
        { var value = Value; Interlocked.Increment(ref Reads); BeforeRead?.Invoke(); return [new(ModuleFieldKey.Create("test.inventory", "item", "count"), "item", value)]; }
        public bool SupportsCharacterAttributes(GameProcessContext process) => true;
        public IReadOnlyList<AdapterCharacterItem> ReadCharacters(GameProcessContext process)
        { var value = (int)Value; Interlocked.Increment(ref Reads); BeforeRead?.Invoke(); return [new("item", "item", 1, [new("count", "count", value, value, 0)])]; }
        public AdapterCharacterItem WriteCharacterAttribute(GameProcessContext process, string character, string field, int value) => throw new InvalidOperationException("Direct write.");
        public bool SupportsEntityEditor(GameProcessContext process, string editor) => true;
        public IReadOnlyList<AdapterEditorEntity> ReadEditorEntities(GameProcessContext process, string editor)
        { var value = Value; Interlocked.Increment(ref Reads); BeforeRead?.Invoke(); return
            [new("item", "item", "词缀碎片", [new(editor == MaterialsEditorId ? "quantity" : "count", "count", value, 0, 100)])]; }
        public AdapterEditorEntity WriteEditorField(GameProcessContext process, string editor, string entity, string field, long value) => throw new InvalidOperationException("Direct write.");
    }
}
