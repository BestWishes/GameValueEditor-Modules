using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameValueEditor.ModuleSdk;
using GameValueEditor.Modules.PlayAgainExpedition;
using GameValueEditor.Services;
using GameValueEditor.Services.Adapters;
using GameValueEditor.ViewModels;

internal static class Program
{
    private static int _checks;
    private static readonly string Output = "D:/MyOtherProjects/GameValueEditor-Modules/artifacts/play-again-expedition/module-tests";
    private static void Check(bool value, string message) { _checks++; if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action, string message)
    { try { action(); } catch (Exception) { _checks++; return; } throw new InvalidOperationException(message); }
    [STAThread]
    private static int Main(string[] args)
    {
        Directory.CreateDirectory(Output);
        var app = new GameValueEditor.App(); app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Exception? failure = null;
        Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
        {
            try
            {
                if (args.Length == 2 && args[0] == "--small-update-live-readonly") await SmallUpdateLive(int.Parse(args[1]));
                else if (args.Length == 2 && args[0] == "--read-live-approved") await LiveReadonly(int.Parse(args[1]));
                else if (args.Length == 2 && args[0] == "--materials-live-readonly") await MaterialsLiveReadonly(int.Parse(args[1]));
                else if (args.Length == 2 && args[0] == "--launcher-speed-live-approved") await LauncherSpeedLive(int.Parse(args[1]));
                else if (args.Length == 2 && args[0] == "--direct-live-approved") await DirectLive(int.Parse(args[1]), false);
                else if (args.Length == 2 && args[0] == "--direct-drops-trial-approved") await DirectLive(int.Parse(args[1]), true);
                else if (args.Length == 1 && args[0] == "--ui-only") { Models(); ArchiveContracts(); MaterialMessages(); Gates(); await BridgeQueue(); await Pages(); await UiContracts(); await MaterialAvailability(); Packages(); }
                else { Models(); ArchiveContracts(); MaterialMessages(); Gates(); await BridgeQueue(); DirectContracts(); await Transport(); await Pages(); await UiContracts(); await MaterialAvailability(); Packages(); NativeReadonly(); }
            }
            catch (Exception error) { failure = error; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        Dispatcher.Run();
        if (failure is not null) { Console.Error.WriteLine(failure); return 1; }
        Console.WriteLine(args.SequenceEqual(new[] { "--ui-only" })
            ? $"Expedition UI-only: {_checks} checks passed; fake clients only, no TCP listener, original game connection, save or screen control."
            : args.FirstOrDefault() == "--launcher-speed-live-approved"
            ? $"Expedition speed integration: {_checks} checks passed; temporary 2x restored, no game launch, save or screen control."
            : $"Expedition module: {_checks} checks passed; no game launch, save or screen control. Reversible session writes occur only in the explicitly selected drops-trial mode."); return 0;
    }
    private static void ArchiveContracts()
    {
        Check(OriginalGameSession.IsGameProcessName("playagainexpedition") && OriginalGameSession.IsGameProcessName("ZSEBEXPEDITION"), "Stable names must be case insensitive");
        Check(!OriginalGameSession.IsGameProcessName("OtherGame") && !OriginalGameSession.IsGameProcessName("ZsebExpeditionHelper"), "Unrelated names accepted");
        var path = Path.Combine(Path.GetTempPath(), "gve-asar-metadata-" + Guid.NewGuid().ToString("N") + ".asar");
        void Write(string version, long? size = null, string offset = "0", bool unpacked = false)
        {
            var package = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { version }));
            var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { files = new Dictionary<string, object>
                { ["package.json"] = new { size = size ?? package.Length, offset, unpacked } } }));
            using var stream = File.Create(path); using var writer = new BinaryWriter(stream);
            writer.Write(4u); writer.Write((uint)(8 + json.Length));
            writer.Write((uint)(4 + json.Length)); writer.Write((uint)json.Length); writer.Write(json); writer.Write(package);
        }
        try
        {
            Write("0.116.73"); Check(ArchiveMetadata.ReadVersion(path) == "0.116.73", "Current declared version not read");
            Write("0.117.0"); Check(ArchiveMetadata.ReadVersion(path) == "0.117.0", "Future declared version incorrectly blocked");
            Write("0.117.0", 65537); Reject(() => ArchiveMetadata.ReadVersion(path), "Oversized metadata accepted");
            Write("0.117.0", offset: "-1"); Reject(() => ArchiveMetadata.ReadVersion(path), "Negative archive offset accepted");
            Write("0.117.0", offset: "9999999"); Reject(() => ArchiveMetadata.ReadVersion(path), "Out-of-file archive offset accepted");
            Write("0.117.0", unpacked: true); Reject(() => ArchiveMetadata.ReadVersion(path), "External package declaration accepted");
            Write(new string('v', 129)); Reject(() => ArchiveMetadata.ReadVersion(path), "Unbounded version text accepted");
            File.WriteAllBytes(path, [4, 0, 0, 0, 255, 255, 255, 127]);
            Reject(() => ArchiveMetadata.ReadVersion(path), "Unbounded archive header accepted");
            var gemProfile = WheelProfile.Default with { Wheel = WheelProfile.Default.Wheel with { TypeWeights = new() { ["gem"] = 0 } } };
            Check(WheelProfile.Parse(gemProfile.ToJson()).Wheel.TypeWeights!["gem"] == 0, "Updated gem type weight lost");
        }
        finally { File.Delete(path); }
    }
    private static async Task LauncherSpeedLive(int sourceId)
    {
        var processService = new ProcessService(); var processes = processService.GetProcesses();
        var source = processes.Single(item => item.ProcessId == sourceId);
        Check(OriginalGameSession.IsGameProcessName(source.ProcessName), "Live speed trial requires the verified original launcher");
        var group = processService.ResolveLogicalGame(source, processes);
        Check(group.DataProcess.Role == GameValueEditor.Models.GameProcessRole.Renderer && group.RootProcess != source && !group.Members.Contains(source), "Launcher did not route to real renderer");
        var context = new GameProcessContext(source.ProcessId, source.ProcessName, source.ExecutablePath, source.StartTimeUtc);
        var session = OriginalGameSession.Resolve(context, false);
        Check(group.RootProcess.ProcessId == session.ProcessId, "Host and module disagree about the game instance");
        Check(RuntimeBridge.Listener(9229).IsFree && RuntimeBridge.Listener(59321).IsFree, "Do not borrow or close an existing interface during this trial");
        using var original = Process.GetProcessById(session.ProcessId);
        Check(!OriginalGameSession.CommandLine(original).Contains("--inspect"), "Original startup was changed");
        using var bridge = await Task.Run(() => RuntimeBridge.Connect(session));
        var directory = Path.Combine(Output, "speed-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var store = new ProfileStore(directory); var catalog = new GameModuleCatalogService(store.ModulesDirectory);
        using var registry = new GameAdapterRegistry(store.ModulesDirectory);
        var ctor = typeof(MainViewModelServices).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        var services = (MainViewModelServices)ctor.Invoke(new object[] { store, processService, new VersionFingerprintService(),
            new MemoryScanService(Path.Combine(directory, "scan")), registry, new ThemeService(), new ProcessSpeedService(),
            new GameIconService(store.IconsDirectory), catalog, new ApplicationUpdateService(store.UpdatesDirectory) });
        var vm = new MainViewModel(services);
        double baseline = 0, accelerated = 0, normalized = 0;
        try
        {
            var first = await Clock();
            Console.WriteLine(JsonSerializer.Serialize(new { verification = "speed-preflight-readonly",
                entered = first.GetProperty("entered").GetBoolean(), phase = first.GetProperty("phase").GetString(),
                pendingWheel = first.GetProperty("pendingWheel").GetBoolean(), speedChanged = false }));
            // Speed control is valid during gameplay, unlike an inventory write.
            // Do not start/stop combat merely to make a clock test possible.
            Check(first.GetProperty("entered").GetBoolean() && !first.GetProperty("pendingWheel").GetBoolean(), "Live speed trial requires an entered game without pending wheel settlement");
            baseline = await Rate(); Check(baseline is > 0.7 and < 1.3, "Original renderer clock was not at normal speed");
            vm.Attach(source);
            Check(vm.AttachedProcess?.ProcessId == group.DataProcess.ProcessId, "Actual host connection retained the launcher PID");
            vm.SpeedMultiplier = "2"; await vm.AccelerateGameAsync();
            Check(vm.IsSpeedActive && vm.SpeedStatusText.Contains("2 倍加速"), "Actual host speed control did not become active");
            accelerated = await Rate(); Check(accelerated is > 1.6 and < 2.5, "Renderer performance clock did not run at 2x");
            await WaitForSpeedControl();
            await vm.RestoreGameSpeedAsync();
            normalized = await Rate(); Check(normalized is > 0.7 and < 1.3 && !vm.IsSpeedActive, "Renderer clock was not restored to normal speed");
            Check((await Clock()).GetProperty("entered").GetBoolean(), "Game left the current save during the clock trial");
        }
        finally
        {
            try { if (vm.IsSpeedActive) { await WaitForSpeedControl(); await vm.RestoreGameSpeedAsync(); } }
            finally { vm.Shutdown(); bridge.Finish(); }
        }
        Check(bridge.Warning.Length == 0 && RuntimeBridge.Listener(9229).IsFree && RuntimeBridge.Listener(59321).IsFree, "Trial interface cleanup failed");
        Check(original.StartTime.ToUniversalTime().Ticks == session.StartTicks, "Original game instance changed");
        Console.WriteLine(JsonSerializer.Serialize(new { verification = "original-launcher-host-speed", sourceId,
            gameProcessId = session.ProcessId, dataProcessId = group.DataProcess.ProcessId, baselineRate = baseline,
            acceleratedRate = accelerated, normalizedRate = normalized, gameActionCalled = false, nativeSaveCalled = false,
            originalStartupUnchanged = true, restored = true, interfaceClosed = true }));

        async Task<JsonElement> Clock()
        {
            bridge.AssertEndpoint();
            var config = JsonSerializer.Serialize(new { pid = session.ProcessId, exe = session.ExecutablePath });
            var code = "(()=>{const g=window.expedition;if(!g||typeof g.getUi!=='function')throw Error('GAME_NOT_READY');return {clock:performance.now(),entered:g.getUi().entered===true,phase:g.engine.phase,pendingWheel:!!g.engine.state.luckyWheel.pending};})()";
            var expression = "(async()=>{const c=" + config + ";if(process.pid!==c.pid||process.execPath!==c.exe)throw Error('SESSION_CHANGED');" +
                "const w=process.mainModule.require('electron').BrowserWindow.getAllWindows().filter(w=>!w.isDestroyed()&&w.webContents.getURL()==='expedition://game/index.html');" +
                "if(w.length!==1)throw Error('ORIGINAL_WINDOW_AMBIGUOUS');return await w[0].webContents.executeJavaScriptInIsolatedWorld(999,[{code:" + JsonSerializer.Serialize(code) + "}],false);})()";
            var request = JsonSerializer.Serialize(new { id = 405, method = "Runtime.evaluate", @params = new { expression, returnByValue = true, awaitPromise = true, timeout = 4000 } });
            return await ExpeditionClient.Send(bridge.Uri, request, 405);
        }
        async Task<double> Rate()
        {
            var before = await Clock(); var wall = Stopwatch.StartNew(); await Task.Delay(800); var after = await Clock(); wall.Stop();
            return (after.GetProperty("clock").GetDouble() - before.GetProperty("clock").GetDouble()) / wall.Elapsed.TotalMilliseconds;
        }
        async Task WaitForSpeedControl()
        {
            // Exercise the real host's two-second cooldown, never bypass it.
            for (var attempt = 0; attempt < 60 && !vm.CanRestoreSpeed; attempt++) await Task.Delay(50);
            Check(vm.CanRestoreSpeed, "Host speed controls did not leave their cooldown");
        }
    }
    private static async Task MaterialsLiveReadonly(int pid)
    {
        using var process = Process.GetProcessById(pid);
        var context = new GameProcessContext(pid, process.ProcessName, process.MainModule!.FileName, process.StartTime.ToUniversalTime());
        Check(!OriginalGameSession.CommandLine(process).Contains("--inspect"), "Original normal launch required");
        Check(RuntimeBridge.Listener(9229).IsFree && RuntimeBridge.Listener(59321).IsFree, "Test requires no existing inspector; it will not close user-owned interfaces");
        var client = new ExpeditionClient(); var read = await Task.Run(() => client.ReadMaterials(context));
        Check(read.Rows.Count >= 29 && read.Rows.Any(row => row.Id == "protectionScrolls"), "Material rows unavailable");
        Check(read.Receipt.Observation.GetProperty("material").GetProperty("readOnly").GetBoolean() &&
            !read.Receipt.Observation.GetProperty("material").GetProperty("nativeSaveCalled").GetBoolean(), "Read called native save");
        Check(read.Rows.All(row => !row.Status.Contains("模式") && !row.Status.Contains("切回守关")), "Opaque activity exposed to player");
        Check(client.ConnectionWarning.Length == 0 && RuntimeBridge.Listener(9229).IsFree && RuntimeBridge.Listener(59321).IsFree, "Read-only interface cleanup failed");
        var adapter = new ExpeditionGameAdapter(client);
        using var page = adapter.CreateEditorPage(ExpeditionGameAdapter.MaterialsId,
            new(context, new(new string('A', 64), "", "", new string('B', 64)), new ReadOnlyHost(), CancellationToken.None));
        await Call(page, "RefreshAsync"); await Render(page.View, ApplicationTheme.Light, "materials-live-state", 980, 680);
        Check(Field<System.Collections.ObjectModel.ObservableCollection<MaterialRow>>(page, "_items").Count >= 29, "Real data page empty");
        Check(RuntimeBridge.Listener(9229).IsFree && RuntimeBridge.Listener(59321).IsFree && process.StartTime.ToUniversalTime() == context.StartTimeUtc, "Read-only page altered game instance or left interface open");
        Console.WriteLine(JsonSerializer.Serialize(new { verification = "materials-original-readonly", rows = read.Rows.Count,
            writable = read.Rows.Count(row => row.CanWrite), activity = read.Receipt.Observation.GetProperty("material").GetProperty("journeyMode").GetString(),
            nativeSaveCalled = false, gameActionCalled = false, gameInstanceUnchanged = true, interfaceClosed = true }));
    }
    private static async Task SmallUpdateLive(int pid)
    {
        await Task.Run(() =>
        {
            using var selected = Process.GetProcessById(pid);
            var context = new GameProcessContext(pid, selected.ProcessName, selected.MainModule!.FileName, selected.StartTime.ToUniversalTime());
            var adapter = new ExpeditionGameAdapter(); var client = new ExpeditionClient();
            Check(adapter.Supports(context, new(new string('D', 64), "", "", new string('E', 64))), "Historical hash still gates an updated game");
            Check(!adapter.Supports(context with { StartTimeUtc = context.StartTimeUtc.AddMinutes(-1) }, new("", "", "", "")), "Stale game instance accepted");
            var session = OriginalGameSession.Resolve(context, false);
            var version = adapter.ReadGameVersionMetadata(context).Version;
            Check(version == ArchiveMetadata.ReadVersion(session.ArchivePath), "Declared version is not from the current package");
            Check(RuntimeBridge.Listener(9229).IsFree && RuntimeBridge.Listener(59321).IsFree, "Do not borrow user-owned interfaces during this test");
            var materials = client.ReadMaterials(context); var wheel = client.ReadWheel(context); var drops = client.ReadDrops(context);
            Check(materials.Rows.Count >= 29 && materials.Rows.Any(row => row.Id == "protectionScrolls"), "Updated materials unavailable");
            Check(wheel.Rows.Count is 0 or 16 && drops.NativeValues.Count == DropProfile.Options.Length, "Updated probability pages unavailable");
            var probability = wheel.Receipt.Observation.GetProperty("probability");
            Console.WriteLine(JsonSerializer.Serialize(new { version, materialRows = materials.Rows.Count, wheelRows = wheel.Rows.Count,
                discoveryErrors = probability.GetProperty("discoveryErrors"), consumers = probability.GetProperty("report").GetProperty("consumerCapabilities") }));
            Check(probability.GetProperty("discoveryErrors").EnumerateObject().Count() == 0, "Current consumers were not all rediscovered");
            Check(probability.GetProperty("report").GetProperty("consumerCapabilities").GetArrayLength() == 13, "Current consumer map incomplete");
            Check(client.ConnectionWarning.Length == 0 && RuntimeBridge.Listener(9229).IsFree && RuntimeBridge.Listener(59321).IsFree, "Temporary interface cleanup failed");
            // Probe is allowed only while idle. All actions run on isolated current
            // native Engine objects, never on the original engine or save.
            if (probability.GetProperty("phase").GetString() == "idle" && !probability.GetProperty("pendingWheel").GetBoolean() &&
                !wheel.Installed && !drops.Installed)
            {
                using var bridge = RuntimeBridge.Connect(session); bridge.AssertEndpoint();
                var request = System.Text.Json.Nodes.JsonNode.Parse(ExpeditionClient.BuildRequest("wheel-inspect", session, new { }))!;
                var expression = request["params"]!["expression"]!.GetValue<string>();
                expression = expression.Replace("operation = \"inspect\"", "operation = \"probe\"")
                    .Replace("await call(\"inspect\", {})", "await call(\"probe\", {})");
                request["params"]!["expression"] = expression;
                var result = ExpeditionClient.Send(bridge.Uri, request.ToJsonString(), 402).GetAwaiter().GetResult().GetProperty("probability");
                var diagnostic = result.GetProperty("diagnostic");
                Check(diagnostic.GetProperty("nativeBundlePassedCases").GetInt32() >= 77, "Updated formal wheel/battle consumers failed");
                Check(diagnostic.GetProperty("dropsValidation").GetProperty("passedChecks").GetInt32() >= 310, "Updated formal drop consumers failed");
                Check(!diagnostic.GetProperty("originalGameActionCalled").GetBoolean() && !diagnostic.GetProperty("originalSaveCalled").GetBoolean(), "Probe touched original state");
                Console.WriteLine(JsonSerializer.Serialize(new { verification = "current-formal-isolated-consumers",
                    wheelAndBattleChecks = diagnostic.GetProperty("nativeBundlePassedCases").GetInt32(),
                    dropChecks = diagnostic.GetProperty("dropsValidation").GetProperty("passedChecks").GetInt32() }));
                bridge.Finish(); Check(bridge.Warning.Length == 0, "Probe interface cleanup failed");
            }
            OriginalGameSession.Assert(session, false);
            Check(RuntimeBridge.Listener(9229).IsFree && RuntimeBridge.Listener(59321).IsFree, "Live test left an interface open");
            Console.WriteLine(JsonSerializer.Serialize(new { verification = "small-update-original-readonly", version,
                materials = materials.Rows.Count, wheel = wheel.Rows.Count, drops = drops.NativeValues.Count,
                consumers = 13, gameActionCalled = false, nativeSaveCalled = false, interfaceClosed = true }));
        });
    }
    private static async Task LiveReadonly(int processId)
    {
        using var process = Process.GetProcessById(processId);
        var context = new GameProcessContext(process.Id, process.ProcessName, process.MainModule!.FileName, process.StartTime.ToUniversalTime());
        var client = new ExpeditionClient(); var materials = client.ReadMaterials(context); var wheel = client.ReadWheel(context); var drops = client.ReadDrops(context);
        Check(materials.Rows.Count == 29 && materials.Rows.Count(row => row.CanWrite) == 29, "Original materials unavailable");
        Check(wheel.Rows.Count == 16 && wheel.Ready && !wheel.Installed, "Original wheel unavailable");
        Check(drops.Ready && !drops.Installed && drops.Profile.IsEmpty && drops.NativeValues.Count == DropProfile.Options.Length, "Original drops unavailable");
        var adapter = new ExpeditionGameAdapter(client); var pageContext = new GameEditorPageContext(context, new(new string('A', 64), "", "", new string('B', 64)), new ReadOnlyHost(), CancellationToken.None);
        foreach (var editor in adapter.Editors)
        {
            using var page = adapter.CreateEditorPage(editor.Id, pageContext); await Call(page, "RefreshAsync");
            await Render(page.View, ApplicationTheme.Light, "live-" + editor.DisplayName, 980, 680);
        }
        Console.WriteLine($"Original DLL client: materials={materials.Rows.Count}, wheel slots={wheel.Rows.Count}, drops options={drops.NativeValues.Count}; three pages rendered with original read-only data; no game action/save/configuration called.");
    }
    private static void DirectContracts()
    {
        var path = "/" + Guid.NewGuid().ToString("D");
        Check(RuntimeBridge.ValidateUri("ws://127.0.0.1:9229" + path, 9229).Port == 9229, "Valid endpoint rejected");
        foreach (var invalid in new[] { "http://127.0.0.1:9229" + path, "ws://localhost:9229" + path,
            "ws://192.168.1.1:9229" + path, "ws://127.0.0.1:59321" + path, "ws://user@127.0.0.1:9229" + path,
            "ws://127.0.0.1:9229" + path + "?redirect=x", "ws://127.0.0.1:9229" + path + "#x", "ws://127.0.0.1:9229/not-a-target" })
            Reject(() => RuntimeBridge.ValidateUri(invalid, 9229), "Invalid endpoint accepted");
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.Any, IPAddress.IPv6Loopback })
        {
            using var listener = new TcpListener(address, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var state = RuntimeBridge.Listener(port);
            Check(!state.IsFree, "Listener occupancy not detected");
            Check(state.IsOwnedBy(Environment.ProcessId) == address.Equals(IPAddress.Loopback), "Non-loopback or IPv6 target accepted");
            listener.Stop(); Check(RuntimeBridge.Listener(port).IsFree, "Released endpoint still occupied");
        }
        Check(!NativeRuntimeActivation.Available(new(Environment.ProcessId, 0, "not-a-game", "not-an-asar", "")), "Unknown runtime handler accepted");
        Reject(() => RuntimeBridge.Listener(0), "Invalid zero port accepted");
        Reject(() => RuntimeBridge.Listener(65536), "Out-of-range port accepted");
        Check(RuntimeBridge.Key(new(1, 1, "", "", "")) != RuntimeBridge.Key(new(1, 2, "", "", "")), "PID reuse shares transport record");
    }
    private static async Task DirectLive(int pid, bool trial)
    {
        await Task.Run(() =>
        {
            using var process = Process.GetProcessById(pid);
            var context = new GameProcessContext(pid, process.ProcessName, process.MainModule!.FileName, process.StartTime.ToUniversalTime());
            var session = OriginalGameSession.Resolve(context, false);
            Check(!OriginalGameSession.CommandLine(process).Contains("--inspect"), "Live verification requires ordinary launch");
            Check(RuntimeBridge.Listener(9229).IsFree && RuntimeBridge.Listener(59321).IsFree, "Existing inspector must not be stopped by this test");
            var client = new ExpeditionClient(); var adapter = new ExpeditionGameAdapter(client);
            var diagnostics = adapter.GetCompatibilityDiagnostics(context, new(new string('A', 64), "", "", ""));
            Check(RuntimeBridge.Listener(9229).IsFree, "Diagnostics activated the inspector");
            Check(diagnostics[1].Status == GameCompatibilityDiagnosticStatus.Passed, "Direct attach diagnostic unavailable");
            if (!trial)
            {
                using (var blocker = new TcpListener(IPAddress.Loopback, 9229))
                {
                    blocker.Start(); Reject(() => client.ReadWheel(context), "Foreign listener did not reject direct activation");
                    Check(RuntimeBridge.Listener(9229).IsOwnedBy(Environment.ProcessId), "Foreign listener was closed or replaced");
                    blocker.Stop();
                }
                for (var round = 0; round < 10; round++)
                {
                    var wheel = client.ReadWheel(context); var drops = client.ReadDrops(context);
                    Check(wheel.Rows.Count == 16 && !wheel.Installed && wheel.Ready, "Live wheel read failed");
                    Check(drops.Ready && drops.Profile.IsEmpty && !drops.Installed, "Live drops read failed");
                    Check(client.ConnectionWarning.Length == 0 && RuntimeBridge.Listener(9229).IsFree, "Direct interface cleanup failed");
                }
                var materials = client.ReadMaterials(context); Check(materials.Rows.Count >= 29, "Material catalog missing");
            }
            else
            {
                var before = client.ReadDrops(context);
                Check(before.Ready && !before.Installed && before.Profile.IsEmpty && !client.ReadWheel(context).Installed, "Original rules must be idle/uninstalled");
                var native = ConsumerRead(session);
                client.ConfigureDrops(context, before, new(new Dictionary<string, double> { ["equipmentBonusPercent"] = 0.1 }));
                Check(client.ConnectionWarning.Length == 0, "Apply cleanup unconfirmed; inspect before proceeding");
                JsonElement applied;
                try { applied = ConsumerRead(session); }
                finally
                {
                    var current = client.ReadDrops(context);
                    Check(current.Profile.ToJson() == new DropProfile(new Dictionary<string, double> { ["equipmentBonusPercent"] = 0.1 }).ToJson() &&
                        current.Receipt.Session == before.Receipt.Session && current.Receipt.Observation.GetProperty("scopeHash").GetString() == before.Receipt.Observation.GetProperty("scopeHash").GetString() &&
                        current.Receipt.Observation.GetProperty("probability").GetProperty("revision").GetInt64() == before.Receipt.Observation.GetProperty("probability").GetProperty("revision").GetInt64() + 1,
                        "Trial profile ownership changed; no blind restore");
                    // Always restore a confirmed, still-owned test profile, including
                    // on a failed read. Never replay an uncertain configure.
                    client.ResetDrops(context, current);
                }
                var restored = ConsumerRead(session); var final = client.ReadDrops(context);
                Check(!final.Installed && final.Profile.IsEmpty && !client.ReadWheel(context).Installed, "Original namespaces not restored");
                Check(Math.Abs(applied.GetProperty("dropBonus").GetDouble() - native.GetProperty("dropBonus").GetDouble() - 0.001) < 1e-12, "Final getter did not change by 0.1 percentage point");
                Check(restored.GetProperty("dropBonus").GetDouble() == native.GetProperty("dropBonus").GetDouble() && restored.GetProperty("goldBonus").GetDouble() == native.GetProperty("goldBonus").GetDouble(), "Original consumer not restored");
                var saveUnchanged = restored.GetProperty("saveHash").GetString() == native.GetProperty("saveHash").GetString();
                var changedPaths = SaveChanges(native, restored);
                Console.WriteLine(JsonSerializer.Serialize(new { trial = "drops-final-consumer", before = native.GetProperty("dropBonus").GetDouble(),
                    applied = applied.GetProperty("dropBonus").GetDouble(), restored = restored.GetProperty("dropBonus").GetDouble(), saveUnchanged,
                    saveChangedPaths = changedPaths, nativeSaveCalledByModule = false, gameActionCalledByModule = false }));
            }
            OriginalGameSession.Assert(session, false);
            Check(RuntimeBridge.Listener(9229).IsFree && RuntimeBridge.Listener(59321).IsFree, "Inspector left open");
            Console.WriteLine("Formal DLL direct attach verified on the same normally launched game instance; no launch, restart or startup setting change.");
        });
    }
    private static JsonElement ConsumerRead(GameSession session)
    {
        using var bridge = RuntimeBridge.Connect(session); bridge.AssertEndpoint();
        var config = JsonSerializer.Serialize(new { pid = session.ProcessId, exe = session.ExecutablePath });
        var expression = "(async()=>{const c=" + config + ";if(process.pid!==c.pid||process.execPath!==c.exe)throw Error('SESSION_CHANGED');" +
            "const w=process.mainModule.require('electron').BrowserWindow.getAllWindows().filter(w=>!w.isDestroyed()&&w.webContents.getURL()==='expedition://game/index.html');" +
            "if(w.length!==1)throw Error('ORIGINAL_WINDOW_AMBIGUOUS');const r=await w[0].webContents.executeJavaScriptInIsolatedWorld(999,[{code:\"(()=>{const g=window.expedition;return {dropBonus:g.engine.dropBonus(),goldBonus:g.engine.goldBonus(),save:g.platform.read()};})()\"}],false);" +
            "r.saveHash=process.mainModule.require('node:crypto').createHash('sha256').update(r.save).digest('hex');" +
            // Keep bodies only in this test process's memory, never log or export them.
            "if(r.save.length>90000)throw Error('TEST_SAVE_TOO_LARGE');return r;})()";
        var request = JsonSerializer.Serialize(new { id = 404, method = "Runtime.evaluate", @params = new { expression, returnByValue = true, awaitPromise = true, timeout = 4000 } });
        var read = ExpeditionClient.Send(bridge.Uri, request, 404).GetAwaiter().GetResult();
        bridge.Finish(); Check(bridge.Warning.Length == 0, "Consumer read interface cleanup failed"); return read;
    }
    private static string[] SaveChanges(JsonElement before, JsonElement after)
    {
        using var first = JsonDocument.Parse(before.GetProperty("save").GetString()!);
        using var second = JsonDocument.Parse(after.GetProperty("save").GetString()!);
        var paths = new List<string>();
        void Compare(JsonElement a, JsonElement b, string path)
        {
            if (paths.Count >= 30 || a.GetRawText() == b.GetRawText()) return;
            if (a.ValueKind == JsonValueKind.Object && b.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in a.EnumerateObject().Select(item => item.Name).Union(b.EnumerateObject().Select(item => item.Name)))
                {
                    var next = path.Length == 0 ? key : path + "." + key;
                    if (!a.TryGetProperty(key, out var x) || !b.TryGetProperty(key, out var y)) paths.Add(next);
                    else Compare(x, y, next);
                }
            }
            else paths.Add(path);
        }
        Compare(first.RootElement, second.RootElement, ""); return paths.ToArray();
    }
    private static void Models()
    {
        var original = WheelProfile.Default.ToJson();
        Check(WheelProfile.Parse(original).ToJson() == original, "Default profile round trip");
        using (var settings = JsonDocument.Parse("{\"wheelBonus\":{\"doublePercent\":20,\"marqueePercent\":0}}"))
        {
            var profile = WheelProfile.FromSessionSettings(settings.RootElement);
            Check(profile.Wheel == WheelProfile.Default.Wheel && profile.WheelBonus.DoublePercent == 20, "Partial session should preserve native defaults");
        }
        foreach (var settings in new[] { "{\"drops\":{}}", "{\"wheel\":{}}", "{\"wheelBonus\":null}", "{\"wheelBonus\":{},\"wheelBonus\":{\"doublePercent\":0,\"marqueePercent\":0}}" })
        {
            using var document = JsonDocument.Parse(settings);
            Reject(() => WheelProfile.FromSessionSettings(document.RootElement), "Unconfirmed namespace accepted");
        }
        using (var before = JsonDocument.Parse("{\"namespaces\":{\"drops\":{\"enabled\":true,\"settings\":{\"drops\":{\"guardianTicketPercent\":25}}}}}"))
        using (var unchanged = JsonDocument.Parse("{\"namespaces\":{\"drops\":{\"enabled\":true,\"settings\":{\"drops\":{\"guardianTicketPercent\":25}},\"hits\":{\"guardianTicket\":1}}}}"))
        using (var changed = JsonDocument.Parse("{\"namespaces\":{\"drops\":{\"enabled\":false,\"settings\":{}}}}"))
        {
            Check(ExpeditionClient.OtherProbabilitySettingsUnchanged(before.RootElement, unchanged.RootElement), "Wheel receipt must tolerate natural drop hits");
            Check(!ExpeditionClient.OtherProbabilitySettingsUnchanged(before.RootElement, changed.RootElement), "Wheel receipt must catch erased drops settings");
        }
        var protection = new WheelProfile(new(new(0, 0, 0, 100, 0)), new(0, 0));
        Check(WheelProfile.Parse(protection.ToJson()).Wheel.GroupPercent.Protection == 100, "100% protection");
        foreach (var json in new[] { "{}", original.Replace("92.5", "92.6"), original.Replace("92.5", "-1"), original.Replace("\"marqueePercent\":1", "\"marqueePercent\":101"),
            original.Replace("\"ordinary\":92.5,", ""), original[..^1] + ",\"drops\":{}}", original.Replace("\"ordinary\":92.5", "\"ordinary\":92.5,\"ordinary\":92.5"),
            original.Replace("\"wheelBonus\"", "\"script\"") }) Reject(() => WheelProfile.Parse(json), "Invalid profile accepted: " + json);
        Reject(() => new WheelProfile(new(new(double.NaN, 0, 0, 100, 0)), new(0, 0)).ToJson(), "NaN accepted");
        Reject(() => new WheelProfile(new(new(0, 0, 0, 100, 0), new() { ["unknown"] = 1 }), new(0, 0)).ToJson(), "Unknown weight accepted");
        var client = new FakeClient(); var adapter = new ExpeditionGameAdapter(client);
        Reject(() => adapter.WriteField(Context().Process, ExpeditionGameAdapter.WheelKey, "arbitrary code"), "Code accepted");
        Reject(() => adapter.WriteField(Context().Process, ModuleFieldKey.Create(ExpeditionGameAdapter.MaterialsId, "protectionScrolls", "quantity"), "1.1"), "Fractional count accepted");
        Check(client.Reads == 0 && client.Writes == 0, "Invalid input reached game client");
        Check(adapter.Editors.Select(item => item.DisplayName).SequenceEqual(new[] { "材料", "大转盘", "掉落" }), "Editor labels changed");
        Check(adapter.GetFieldPolicy(ExpeditionGameAdapter.DropsId, "session", "configuration") is { SessionOnly: true, CanLock: false }, "Drops policy");
        var dropProfile = DropProfile.Parse("{\"drops\":{\"goldBonusPercent\":100,\"mine\":{\"qualityPercent\":{\"blue\":0,\"purple\":0,\"immortal\":0,\"mythic\":100}}}}");
        Check(DropProfile.Parse(dropProfile.ToJson()).ToJson() == dropProfile.ToJson(), "Drop round trip");
        foreach (var invalid in new[] { "{\"drops\":{}}", "{\"wheel\":{}}", "{\"drops\":{\"unknown\":1}}", "{\"drops\":{\"goldBonusPercent\":1001}}", "{\"drops\":{\"goldBonusPercent\":0,\"goldBonusPercent\":1}}", "{\"drops\":{\"mine\":{\"qualityPercent\":{\"blue\":100}}}}", "{\"drops\":{\"cow\":{\"mythicPercent\":{\"w:10009\":1}}}}" })
            Reject(() => DropProfile.Parse(invalid), "Invalid drops accepted");
        Reject(() => adapter.WriteField(Context().Process, ExpeditionGameAdapter.DropsKey, "{}"), "Empty drops reached client");
        Check(client.Reads == 0 && client.Writes == 0, "Invalid drops accessed client");
        Check(!OriginalGameSession.InterfaceHelp.Contains("追加") && !OriginalGameSession.InterfaceHelp.Contains("--inspect"), "Manual parameter configuration remains");
        Check(!OriginalGameSession.InterfaceHelp.Contains("准备连接") && !ExpeditionClient.ExplainGuard("PROBABILITY_IMPLEMENTATION_CHANGED_RESTART_REQUIRED").Contains("准备连接 / 重启"), "Old preparation UI guidance remains");
        Check(typeof(ExpeditionGameAdapter).Assembly.GetType("GameValueEditor.Modules.PlayAgainExpedition.OriginalGameLauncher") is null &&
            typeof(ExpeditionGameAdapter).Assembly.GetType("GameValueEditor.Modules.PlayAgainExpedition.ConnectionPreparationWindow") is null, "Old launcher code still shipped");
        Check(adapter.GetFieldPolicy(ExpeditionGameAdapter.WheelId, "session", "configuration") is { SessionOnly: true, CanLock: false }, "Wheel policy");
        Check(adapter.GetFieldPolicy(ExpeditionGameAdapter.MaterialsId, "protectionScrolls", "quantity") is { SessionOnly: false, CanLock: false }, "Materials policy");
        var request = ExpeditionClient.BuildRequest("wheel-inspect", new(1, 1, "D:/__GVE_INPUT_JSON__/\"游戏.exe", "D:/app.asar", "C:/用户"), new { });
        using var doc = JsonDocument.Parse(request);
        Check(doc.RootElement.GetProperty("params").GetProperty("expression").GetString()!.Contains("__GVE_INPUT_JSON__"), "Substitution corrupted JSON-string markers");
        using var board = JsonDocument.Parse("{\"probability\":{\"report\":{\"wheel\":[{\"group\":\"protection\",\"kind\":\"material\",\"quality\":5}]}}}");
        var available = new WheelSnapshot(new(new(1, 1, "", "", ""), board.RootElement.Clone()), protection, false, true, "", []);
        ExpeditionClient.ValidateAvailableGroups(available, protection); _checks++;
        Reject(() => ExpeditionClient.ValidateAvailableGroups(available, protection with { Wheel = protection.Wheel with { TypeWeights = new() { ["material"] = 0 } } }), "Unavailable group accepted");
    }
    private static async Task Transport()
    {
        foreach (var scenario in new[] { "success", "success-sync-ui", "success-close-abort", "guard", "wrong-id", "oversize" })
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                using var network = client.GetStream();
                var header = new List<byte>(); var single = new byte[1];
                while (!Encoding.ASCII.GetString(header.ToArray()).EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (await network.ReadAsync(single) != 1 || header.Count > 8192) throw new InvalidOperationException("Fake server handshake failed");
                    header.Add(single[0]);
                }
                var key = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n").Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
                var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                await network.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n"));
                using var socket = WebSocket.CreateFromStream(network, true, null, TimeSpan.FromSeconds(20));
                var buffer = new byte[4096];
                var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                using var sent = JsonDocument.Parse(buffer.AsMemory(0, received.Count));
                Check(sent.RootElement.GetProperty("id").GetInt32() == 401, "Wrong protocol send");
                var response = scenario switch
                {
                    "success" or "success-sync-ui" or "success-close-abort" => "{\"id\":401,\"result\":{\"result\":{\"value\":{\"ok\":true}}}}",
                    "guard" => "{\"id\":401,\"result\":{\"exceptionDetails\":{\"exception\":{\"description\":\"Error: GAME_NOT_ENTERED\\nprivate-path\"}}}}",
                    "wrong-id" => "{\"id\":999,\"result\":{}}",
                    _ => new string('x', 131073)
                };
                await socket.SendAsync(Encoding.UTF8.GetBytes(response), WebSocketMessageType.Text, true, CancellationToken.None);
                if (scenario == "success-close-abort") { socket.Abort(); return; }
                do { received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None); } while (received.MessageType != WebSocketMessageType.Close);
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "fake complete", CancellationToken.None);
            });
            var method = typeof(ExpeditionClient).GetMethod("Send", BindingFlags.NonPublic | BindingFlags.Static)!;
            var sending = (Task<JsonElement>)method.Invoke(null, [new Uri($"ws://127.0.0.1:{port}/fake"), "{\"id\":401,\"method\":\"Runtime.evaluate\",\"params\":{\"expression\":\"0\"}}", 401])!;
            if (scenario is "success" or "success-sync-ui" or "success-close-abort") Check((scenario == "success-sync-ui" ? sending.GetAwaiter().GetResult() : await sending).GetProperty("ok").GetBoolean(), "Transport result missing");
            else
            {
                var rejected = false;
                try { await sending; }
                catch (InvalidOperationException error)
                {
                    rejected = true;
                    Check(scenario != "guard" || error.Message.Contains("进入远征存档") && !error.Message.Contains("private-path"), "Guard was not localized/redacted");
                }
                Check(rejected, "Invalid transport result accepted");
            }
            await server.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
    private static async Task BridgeQueue()
    {
        var root = Path.Combine(Output, "bridge-" + Guid.NewGuid().ToString("N"));
        var first = RuntimeBridge.AcquireLease(root);
        Task<FileStream>? queued = null;
        try
        {
            queued = Task.Run(() => RuntimeBridge.AcquireLease(root));
            await Task.Delay(150);
            Check(!queued.IsCompleted, "Overlapping refresh did not wait for previous cleanup");
            first.Dispose();
            using var next = await queued.WaitAsync(TimeSpan.FromSeconds(5));
            Check(next.CanWrite, "Queued refresh did not continue after previous cleanup");
        }
        finally
        {
            first.Dispose();
            if (queued is not null) { using var remaining = await queued.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        // A non-sharing I/O error is not a busy request and must fail immediately.
        Directory.CreateDirectory(Path.Combine(root, "runtime-bridge.lock.invalid"));
        var invalidRoot = Path.Combine(root, "runtime-bridge.lock.invalid");
        Directory.CreateDirectory(Path.Combine(invalidRoot, "runtime-bridge.lock"));
        var elapsed = Stopwatch.StartNew();
        Reject(() => RuntimeBridge.AcquireLease(invalidRoot), "Invalid lease target accepted");
        Check(elapsed.Elapsed < TimeSpan.FromSeconds(2), "Permanent I/O error was retried as sharing contention");
    }
    private static void Gates()
    {
        var root = Path.Combine(Output, "gates-" + Guid.NewGuid().ToString("N"));
        Reject(() => MutationGate.Acquire(root, "../escape"), "Session key escaped ledger root");
        using (var first = MutationGate.Acquire(root, "session"))
        {
            Reject(() => MutationGate.Acquire(root, "session"), "Concurrent write accepted");
            first.Claim();
        }
        Reject(() => MutationGate.Acquire(root, "session"), "Pending operation was replayed");
        using (var second = MutationGate.Acquire(root, "new-session")) { second.Claim(); second.Confirm(); }
        using (var third = MutationGate.Acquire(root, "new-session")) { third.Claim(); third.Uncertain(); }
        Reject(() => MutationGate.Acquire(root, "new-session"), "Uncertain operation was retried");
    }
    private static void MaterialMessages()
    {
        foreach (var (code, expected) in new[] {
            ("MATERIAL_BATTLE_ACTIVE", "战斗尚未结束"), ("MATERIAL_BATTLE_PAUSED", "战斗已暂停"),
            ("MATERIAL_OPERATION_PENDING", "正在处理当前操作"), ("MATERIAL_WHEEL_PENDING", "大转盘"),
            ("MATERIAL_SAVE_BLOCKED", "保存受阻"), ("MATERIAL_SAVE_PENDING", "正常保存"),
            ("MATERIAL_NOT_DEFINED", "数据结构"), ("MATERIAL_CONTRACT_CHANGED", "数据结构"),
            ("MATERIAL_READ_ONLY", "只读"), ("MATERIAL_RANGE_INVALID", "数量超出"),
            ("ACTIVE_SAVE_CHANGED", "重新刷新"), ("JOURNEY_MODE_NOT_SUPPORTED", "新版模块") })
        {
            var text = ExpeditionClient.ExplainGuard(code);
            Check(text.Contains(expected) && !text.Contains("模式") && !text.Contains("切回"), "Material guard is not actionable: " + code);
            if (code is "MATERIAL_OPERATION_PENDING" or "MATERIAL_SAVE_BLOCKED" or "MATERIAL_SAVE_PENDING")
                Check(!text.Contains("战斗"), "Non-battle condition says user is fighting");
        }
        Check(!OriginalGameSession.InterfaceHelp.Contains("模式"), "Help requires hidden activity selection");
    }
    private static async Task MaterialAvailability()
    {
        foreach (var status in new[] { "当前战斗尚未结束，请先停止战斗再修改材料", "战斗已暂停但尚未结束，请先退出当前战斗再修改材料",
            "游戏正在处理当前操作，结束后可修改材料", "请先完成大转盘的动画与奖励结算", "游戏保存受阻，当前不能修改材料",
            "实时数量和保存数量不同，请正常保存后重新读取" })
        {
            var client = new FakeClient { MaterialBlockedStatus = status }; var adapter = new ExpeditionGameAdapter(client); var host = new Host { Adapter = adapter };
            using var page = adapter.CreateEditorPage(ExpeditionGameAdapter.MaterialsId, Context(host)); await Call(page, "RefreshAsync");
            var rows = Field<System.Collections.ObjectModel.ObservableCollection<MaterialRow>>(page, "_items");
            Check(rows.Count == 29 && rows.All(row => !row.CanWrite && row.Status == status), "Busy page hides material rows or changes reason");
            Field<DataGrid>(page, "_grid").SelectedItem = rows.First();
            try { await Call(page, "EditSelectedAsync"); throw new InvalidOperationException("Blocked page allowed edit"); }
            catch (InvalidOperationException error) when (error.Message == status) { _checks++; }
            Check(host.Writes == 0 && client.Writes == 0, "Blocked material page dispatched a write");
        }
    }
    private static GameEditorPageContext Context(IGameEditorHostServices? host = null, CancellationToken lifetime = default) =>
        new(new(1, "fixture", "", DateTime.UnixEpoch), new("", "", "", ""), host ?? new Host(), lifetime);
    private static Task Call(IGameEditorPage page, string name)
    {
        try { return (Task)page.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null)!; }
        catch (TargetInvocationException error) when (error.InnerException is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static T Field<T>(object page, string name) => (T)page.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
    private static async Task Pages()
    {
        foreach (var editor in new[] { ExpeditionGameAdapter.MaterialsId, ExpeditionGameAdapter.WheelId, ExpeditionGameAdapter.DropsId })
        foreach (var scenario in new[] { "normal", "cancel", "expired", "missing-write", "missing-snapshot", "write-error", "invalid", "stale" })
        {
            using var lifetime = new CancellationTokenSource();
            var client = new FakeClient(); var adapter = new ExpeditionGameAdapter(client);
            var host = new Host { Adapter = adapter, Prompt = scenario == "cancel" ? null : "20" };
            if (scenario == "expired") host.PromptFinished = lifetime.Cancel;
            if (scenario == "write-error") host.FailWrite = true;
            if (scenario == "stale") host.Stale = true;
            using var page = adapter.CreateEditorPage(editor, Context(scenario == "missing-write" ? new ReadOnlyHost() : scenario == "missing-snapshot" ? new OldHost() : host, lifetime.Token));
            if (scenario is "missing-snapshot" or "stale")
            {
                try { await Call(page, "RefreshAsync"); throw new InvalidOperationException("Expected snapshot rejection"); }
                catch (GameEditorSnapshotChangedException) when (scenario == "stale") { _checks++; }
                catch (InvalidOperationException error) when (scenario == "missing-snapshot" && error.Message.Contains("安全刷新")) { _checks++; }
                Check(client.Reads == 0 && client.Writes == 0, "Bad snapshot entered game"); continue;
            }
            await Call(page, "RefreshAsync");
            if (editor == ExpeditionGameAdapter.MaterialsId)
                Field<DataGrid>(page, "_grid").SelectedItem = Field<System.Collections.ObjectModel.ObservableCollection<MaterialRow>>(page, "_items").Single(row => row.Id == "protectionScrolls");
            else if (editor == ExpeditionGameAdapter.WheelId)
            {
                var boxes = Field<TextBox[]>(page, "_groups");
                for (var index = 0; index < 5; index++) boxes[index].Text = index == 3 ? "100" : "0";
                if (scenario == "invalid") boxes[0].Text = "10";
                if (scenario is "cancel" or "expired") lifetime.Cancel();
            }
            else
            {
                var row = Field<System.Collections.ObjectModel.ObservableCollection<DropEditRow>>(page, "_items").First();
                row.Enabled = true; row.Value = scenario == "invalid" ? "NaN" : "20";
                if (scenario is "cancel" or "expired") lifetime.Cancel();
            }
            if (scenario == "invalid" && editor == ExpeditionGameAdapter.MaterialsId) host.Prompt = "1.5";
            var method = editor == ExpeditionGameAdapter.MaterialsId ? "EditSelectedAsync" : "ApplyAsync";
            var completed = false;
            try { await Call(page, method); completed = true; }
            catch (OperationCanceledException) when (scenario is "expired" or "cancel") { _checks++; }
            catch (InvalidOperationException) when (scenario is "missing-write" or "write-error" or "invalid") { _checks++; }
            Check(completed == (scenario == "normal" || scenario == "cancel" && editor == ExpeditionGameAdapter.MaterialsId), "Write completion does not match scenario");
            Check(host.Writes == (scenario is "normal" or "write-error" ? 1 : 0), "Page bypassed or duplicated bridge");
            Check(client.Writes == (scenario == "normal" ? 1 : 0), "Cancelled/invalid page wrote game");
            if (scenario == "normal" && editor != ExpeditionGameAdapter.MaterialsId)
            {
                await Call(page, "RestoreAsync");
                Check(host.LastValue == "restore" && host.LastKey == (editor == ExpeditionGameAdapter.WheelId ? ExpeditionGameAdapter.WheelKey : ExpeditionGameAdapter.DropsKey) && client.Writes == 2, "Restore bypassed same semantic queue");
            }
        }
        foreach (var theme in Enum.GetValues<ApplicationTheme>())
        foreach (var editor in new[] { ExpeditionGameAdapter.MaterialsId, ExpeditionGameAdapter.WheelId, ExpeditionGameAdapter.DropsId })
        {
            new ThemeService().Apply(theme);
            var client = new FakeClient(); var adapter = new ExpeditionGameAdapter(client); var host = new Host { Adapter = adapter };
            using var page = adapter.CreateEditorPage(editor, Context(host)); await Call(page, "RefreshAsync");
            await Render(page.View, theme, editor.EndsWith("materials") ? "materials" : editor.EndsWith("drops") ? "drops" : "wheel", 980, 680);
            Check(ReferenceEquals(((Control)page.View).Foreground, Application.Current.FindResource(ModuleVisualResources.TextBrush)), "Page does not inherit active host theme");
            Check(client.Writes == 0 && host.Writes == 0, "Render/theme change wrote game");
        }
        new ThemeService().Apply(ApplicationTheme.Light);
    }
    private static async Task Render(FrameworkElement page, ApplicationTheme theme, string name, int width, int height)
    {
        var frame = new Border { Background = (Brush)Application.Current.FindResource(ModuleVisualResources.PanelBrush), Child = page,
            Width = width, Height = height };
        System.Windows.Documents.TextElement.SetFontFamily(frame, new FontFamily("Segoe UI, Microsoft YaHei UI"));
        frame.Measure(new(width, height)); frame.Arrange(new(0, 0, width, height)); frame.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        frame.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(frame);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(Output, $"{name}-{theme}.png")); encoder.Save(stream);
        frame.Child = null;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var item in Descendants<T>(VisualTreeHelper.GetChild(root, index))) yield return item;
    }
    private static async Task Layout(Border frame)
    {
        frame.Measure(new(frame.Width, frame.Height)); frame.Arrange(new(0, 0, frame.Width, frame.Height));
        frame.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ContextIdle); frame.UpdateLayout();
    }
    private static async Task UiContracts()
    {
        foreach (var editor in new[] { ExpeditionGameAdapter.MaterialsId, ExpeditionGameAdapter.WheelId, ExpeditionGameAdapter.DropsId })
        {
            var client = new FakeClient(); var adapter = new ExpeditionGameAdapter(client); var host = new Host { Adapter = adapter };
            using var page = adapter.CreateEditorPage(editor, Context(host)); await Call(page, "RefreshAsync");
            var root = (Grid)((UserControl)page.View).Content;
            Check(root.RowDefinitions.Count == 4 && root.Children.Cast<UIElement>().All(child => Grid.GetRow(child) < 4), "Removed footer left a layout row");
            var frame = new Border { Child = page.View, Width = 980, Height = 680 };
            try
            {
                await Layout(frame);
                Check(!Descendants<TextBlock>(root).Any(text => text.Text == OriginalGameSession.InterfaceHelp || text.Text.Contains("从 Steam 或原入口正常启动")), "Connection footer still shown");
                if (editor == ExpeditionGameAdapter.WheelId)
                {
                    Check(!Descendants<DataGrid>(root).Any(), "Per-slot wheel table still shown");
                    var toolbar = root.Children.OfType<StackPanel>().Single(panel => Grid.GetRow(panel) == 1);
                    var buttons = toolbar.Children.OfType<Button>().ToArray();
                    Check(buttons.Select(button => button.Content).SequenceEqual(new[] { "刷新", "应用规则", "恢复原规则" }), "Wheel toolbar labels/order");
                    Check(buttons.All(button => button.Margin == ModuleVisualResources.InlineControlSpacing && button.MinWidth == 96), "Wheel spacing differs");
                    var heading = Descendants<TextBlock>(root).Single(text => text.Text.StartsWith("奖励概率（"));
                    Check(buttons.All(button => button.TranslatePoint(new(0, button.ActualHeight), root).Y < heading.TranslatePoint(new(), root).Y), "Wheel toolbar not above heading");
                    var boxes = Field<TextBox[]>(page, "_groups").Concat(Field<TextBox[]>(page, "_bonus")).ToArray();
                    Check(boxes.Length == 7 && boxes.All(box => box.ActualWidth > 0), "Upper wheel probability inputs missing");
                    Check(Descendants<Expander>(root).Single().Header.ToString()!.Contains("类内类型"), "Existing optional category weights removed");
                    var scroll = root.Children.OfType<ScrollViewer>().Single();
                    Check(double.IsPositiveInfinity(scroll.MaxHeight) && Grid.GetRow(scroll) == 2, "Wheel settings still height-limited");
                    frame.Width = 460; frame.Height = 430; await Layout(frame);
                    Check(buttons.Last().TranslatePoint(new(buttons.Last().ActualWidth, 0), root).X <= root.ActualWidth, "Compact toolbar clipped");
                    Descendants<Expander>(root).Single().IsExpanded = true; await Layout(frame);
                    Check(scroll.ScrollableHeight > 0, "Expanded compact wheel settings cannot scroll");
                }
                Check(host.Writes == 0 && client.Writes == 0, "UI layout wrote data");
            }
            finally { frame.Child = null; }
        }
        foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
        foreach (var editor in new[] { ExpeditionGameAdapter.WheelId, ExpeditionGameAdapter.DropsId })
        {
            new ThemeService().Apply(theme);
            var client = new FakeClient(); var adapter = new ExpeditionGameAdapter(client); var host = new Host { Adapter = adapter };
            using var page = adapter.CreateEditorPage(editor, Context(host)); await Call(page, "RefreshAsync");
            await Render(page.View, theme, editor == ExpeditionGameAdapter.WheelId ? "wheel-compact" : "drops-compact", 460, 430);
        }
        new ThemeService().Apply(ApplicationTheme.Light);
        Check(DropsPage.CalculateWheelOffset(100, 1000, 300, -120, 3) == 148, "System line count ignored");
        Check(DropsPage.CalculateWheelOffset(100, 1000, 300, -30, 3) == 112, "Fine wheel delta lost");
        Check(DropsPage.CalculateWheelOffset(100, 1000, 300, -120, -1) == 400, "System page scrolling ignored");
        Check(DropsPage.CalculateWheelOffset(100, 1000, 300, -120, 0) == 100, "Disabled system wheel still scrolls");
        Check(DropsPage.CalculateWheelOffset(100, 1000, 300, 0, 3) == 100, "Zero delta scrolls");
        Check(DropsPage.CalculateWheelOffset(1, 1000, 300, 120, 3) == 0, "Wheel crosses top");
        Check(DropsPage.CalculateWheelOffset(699, 1000, 300, -120, 3) == 700, "Wheel crosses bottom");
        Check(DropsPage.CalculateWheelOffset(0, 200, 300, -120, 3) == 0, "Short content scrolls");
        using var lifetime = new CancellationTokenSource();
        var dropsClient = new FakeClient(); var dropsAdapter = new ExpeditionGameAdapter(dropsClient); var dropsHost = new Host { Adapter = dropsAdapter };
        using var dropsPage = dropsAdapter.CreateEditorPage(ExpeditionGameAdapter.DropsId, Context(dropsHost, lifetime.Token));
        await Call(dropsPage, "RefreshAsync");
        var rows = Field<System.Collections.ObjectModel.ObservableCollection<DropEditRow>>(dropsPage, "_items"); rows[0].Enabled = true;
        var before = rows.Select(row => (row.Enabled, row.Value, row.Original)).ToArray();
        var dropsFrame = new Border { Child = dropsPage.View, Width = 980, Height = 680 };
        try
        {
            await Layout(dropsFrame);
            var scroll = Field<ScrollViewer>(dropsPage, "_scroll");
            Check(!scroll.CanContentScroll && scroll.ScrollableHeight > 0, "Drops outer scroll unavailable");
            var grid = Descendants<DataGrid>(scroll).First();
            var sources = new UIElement[] { grid, Descendants<DataGridRow>(grid).First(), Descendants<TextBox>(grid).First(), Descendants<CheckBox>(grid).First() };
            async Task<bool> Wheel(UIElement source, int delta)
            {
                var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
                source.RaiseEvent(args); await Layout(dropsFrame); return args.Handled;
            }
            foreach (var source in sources)
            {
                scroll.ScrollToTop(); await Layout(dropsFrame);
                var expected = DropsPage.CalculateWheelOffset(scroll.VerticalOffset, scroll.ExtentHeight, scroll.ViewportHeight, -120, SystemParameters.WheelScrollLines);
                Check(await Wheel(source, -120), "Nested input/grid swallowed wheel: " + source.GetType().Name);
                Check(Math.Abs(scroll.VerticalOffset - expected) < .01, "Nested wheel failed to scroll outer region");
                await Wheel(source, 120); Check(scroll.VerticalOffset == 0, "Wheel cannot return to top");
            }
            Check(await Wheel(sources[2], 120) && scroll.VerticalOffset == 0, "Top boundary wheel not clamped");
            scroll.ScrollToEnd(); await Layout(dropsFrame);
            Check(scroll.VerticalOffset == scroll.ScrollableHeight, "Scrollbar/programmatic bottom broken");
            Check(await Wheel(sources[2], -120) && scroll.VerticalOffset == scroll.ScrollableHeight, "Bottom boundary wheel not clamped");
            scroll.ScrollToTop(); await Layout(dropsFrame);
            var fineExpected = DropsPage.CalculateWheelOffset(0, scroll.ExtentHeight, scroll.ViewportHeight, -30, SystemParameters.WheelScrollLines);
            Check(await Wheel(sources[2], -30) && Math.Abs(scroll.VerticalOffset - fineExpected) < .01, "Fine routed wheel failed");
            foreach (var expander in Descendants<Expander>(scroll).ToArray()) expander.IsExpanded = true;
            // The same outer viewport must manage expanded optional rows too.
            dropsFrame.Width = 460; dropsFrame.Height = 430; await Layout(dropsFrame);
            var maximum = scroll.ScrollableHeight; Check(maximum > 0, "Compact/expanded drops cannot scroll");
            scroll.ScrollToEnd(); await Layout(dropsFrame); Check(scroll.VerticalOffset == maximum, "Expanded last rows inaccessible");
            scroll.ScrollToTop(); await Layout(dropsFrame);
            dropsPage.View.IsEnabled = false; Check(!await Wheel(sources[2], -120) && scroll.VerticalOffset == 0, "Disabled page handled wheel");
            dropsPage.View.IsEnabled = true; lifetime.Cancel(); Check(!await Wheel(sources[2], -120) && scroll.VerticalOffset == 0, "Expired page handled wheel");
            Check(rows.Select(row => (row.Enabled, row.Value, row.Original)).SequenceEqual(before), "Wheel changed edited values");
            Check(dropsClient.Writes == 0 && dropsHost.Writes == 0, "Wheel dispatched a write");
        }
        finally { dropsFrame.Child = null; }
    }
    private static void Packages()
    {
        var root = Path.Combine(Output, "loader-" + Guid.NewGuid().ToString("N"));
        var version = typeof(ExpeditionGameAdapter).Assembly.GetName().Version!;
        var moduleVersion = $"{version.Major}.{version.Minor}.{version.Build}";
        var package = Path.Combine(root, "packages", "game.play-again-expedition", moduleVersion); Directory.CreateDirectory(package);
        File.Copy(typeof(ExpeditionGameAdapter).Assembly.Location, Path.Combine(package, "GameValueEditor.Modules.PlayAgainExpedition.dll"));
        File.Copy("D:/MyOtherProjects/GameValueEditor-Modules/games/play-again-expedition/module.json", Path.Combine(package, "module.json"));
        File.WriteAllText(Path.Combine(root, "installed.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            modules = new[] { new { id = "game.play-again-expedition", version = moduleVersion, installedUtc = "2026-10-08T00:00:00Z" } }
        }));
        using var registry = new GameAdapterRegistry(root);
        Check(registry.LoadErrors.Count == 0, "Actual host loader rejected module: " + string.Join(";", registry.LoadErrors));
        var adapter = registry.FindById("game.play-again-expedition")!;
        Check(adapter is ICoordinatedGameEditorPageProvider && adapter.Editors.Count == 3, "Host missed module contract");
        foreach (var editor in adapter.Editors)
        {
            using var page = ((IGameEditorPageFactoryProvider)adapter).CreateEditorPage(editor.Id, Context());
            Check(page.View.GetType() == typeof(UserControl) && page.GetType().Assembly.GetName().Name == "GameValueEditor.Modules.PlayAgainExpedition", "Page not owned by module");
        }
        using var unlocked = new FileStream(Path.Combine(package, "GameValueEditor.Modules.PlayAgainExpedition.dll"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Check(unlocked.Length > 0, "Installed source DLL locked");
    }
    private static void NativeReadonly()
    {
        var processes = Process.GetProcessesByName("ZsebExpedition");
        try
        {
            var main = processes.SingleOrDefault(process => !OriginalGameSession.CommandLine(process).Contains("--type="));
            if (main is null) { Console.WriteLine("Original game not running: live readonly identification skipped."); return; }
            var context = new GameProcessContext(main.Id, main.ProcessName, main.MainModule!.FileName, main.StartTime.ToUniversalTime());
            var adapter = new ExpeditionGameAdapter();
            Check(adapter.Supports(context, new(new string('A', 64), "", "", "")), "Exact native process group/ASAR did not resolve");
            Check(adapter.Supports(context, new(new string('C', 64), "", "", "")), "A small update was rejected by a historical hash");
            Check(adapter.ReadGameVersionMetadata(context).Version == ArchiveMetadata.ReadVersion(OriginalGameSession.Resolve(context, false).ArchivePath), "Declared version is stale");
            Check(!adapter.Supports(context with { StartTimeUtc = DateTime.UtcNow.AddDays(-1) }, new("", "", "", "")), "Old instance accepted");
            var diagnostics = adapter.GetCompatibilityDiagnostics(context, new(new string('A', 64), "", "", ""));
            Check(diagnostics.All(item => !item.Message.Contains(context.ExecutablePath) && !item.Message.Contains(main.Id.ToString())), "Sensitive diagnostic");
            Console.WriteLine("Original game identification/diagnostics readonly check passed; no renderer script sent.");
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}

internal sealed class FakeClient : IExpeditionClient
{
    public int Reads, Writes;
    public string? MaterialBlockedStatus;
    private WheelProfile _profile = WheelProfile.Default;
    private bool _installed;
    private long _quantity = 3;
    public bool Supports(GameProcessContext process, GameBuildIdentity build) => true;
    public bool CanAttachDirectly(GameProcessContext process) => false;
    public string ConnectionWarning => "";
    public MaterialsSnapshot ReadMaterials(GameProcessContext process)
    {
        Reads++;
        return new(new(new(1, 1, "", "", ""), default), Enumerable.Range(0, 29).Select(index => new MaterialRow(index == 0 ? "protectionScrolls" : "material" + index,
            index == 0 ? "装备保护卷" : "材料 " + index, index % 2 == 0 ? "强化" : "票券", index == 0 ? _quantity : index * 100, index == 0 ? _quantity : index * 100,
            0, 1000000000, MaterialBlockedStatus is null, MaterialBlockedStatus ?? "可修改")).ToArray());
    }
    public MaterialRow WriteMaterial(GameProcessContext process, MaterialsSnapshot read, string id, long target)
    { Writes++; _quantity = target; return read.Rows.Single(row => row.Id == id) with { Value = target, StoredValue = target }; }
    public WheelSnapshot ReadWheel(GameProcessContext process)
    {
        Reads++; return new(new(new(1, 1, "", "", ""), default), _profile, _installed, true, _installed ? "已应用会话概率；重启恢复原规则。" : "当前使用游戏原概率。",
            Enumerable.Range(1, 16).Select(index => new WheelRow(index, index == 5 ? "装备保护卷" : "紫色时装", index == 5 ? "装备保护卷" : "普通", index == 5 ? "1%" : "13.2143%", _installed ? index == 5 ? "100%" : "0%" : index == 5 ? "1%" : "13.2143%")).ToArray());
    }
    public void ConfigureWheel(GameProcessContext process, WheelSnapshot read, WheelProfile profile) { Writes++; _profile = profile; _installed = true; }
    public void ResetWheel(GameProcessContext process, WheelSnapshot read) { Writes++; _profile = WheelProfile.Default; _installed = false; }
    private DropProfile _drops = DropProfile.Empty;
    public DropsSnapshot ReadDrops(GameProcessContext process)
    { Reads++; return new(new(new(1, 1, "", "", ""), default), _drops, !_drops.IsEmpty, true, "当前使用游戏原规则；仅勾选要修改的项目。", DropProfile.Options.ToDictionary(option => option.Path, option => DropProfile.Format(option.Default) + "%")); }
    public void ConfigureDrops(GameProcessContext process, DropsSnapshot read, DropProfile profile) { Writes++; _drops = profile; }
    public void ResetDrops(GameProcessContext process, DropsSnapshot read) { Writes++; _drops = DropProfile.Empty; }
}
internal class OldHost : IGameEditorHostServices
{
    public virtual Task<string?> PromptValueAsync(GameEditorTextPrompt prompt) => Task.FromResult<string?>("20");
    public Task SaveFieldAsync(GameEditorSavedFieldRequest request) => Task.CompletedTask;
    public void ReportStatus(string message) { }
    public void ShowError(string title, string message) { }
}
internal class ReadOnlyHost : OldHost, IGameEditorSnapshotOperations
{
    public bool Stale;
    public async Task ReadSnapshotAsync<T>(Func<T> read, Action<T> apply)
    { if (Stale) throw new GameEditorSnapshotChangedException(); var value = await Task.Run(read); apply(value); }
}
internal sealed class Host : ReadOnlyHost, IGameEditorFieldOperations
{
    public ExpeditionGameAdapter? Adapter;
    public string? Prompt = "20", LastKey, LastValue;
    public Action? PromptFinished;
    public bool FailWrite;
    public int Writes;
    public override Task<string?> PromptValueAsync(GameEditorTextPrompt prompt) { PromptFinished?.Invoke(); return Task.FromResult(Prompt); }
    public Task<AdapterFieldValue> WriteFieldAsync(string fieldKey, string displayValue)
    {
        Writes++; LastKey = fieldKey; LastValue = displayValue;
        if (FailWrite) throw new InvalidOperationException("simulated write error");
        return Task.FromResult(Adapter!.WriteField(new(1, "fixture", "", DateTime.UnixEpoch), fieldKey, displayValue));
    }
}
