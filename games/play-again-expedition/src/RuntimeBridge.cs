using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameValueEditor.Modules.PlayAgainExpedition;

internal sealed record ListenerState(int Count, int LoopbackOwner)
{
    internal bool IsFree => Count == 0;
    internal bool IsOwnedBy(int pid) => Count == 1 && LoopbackOwner == pid;
}

internal sealed class RuntimeBridge : IDisposable
{
    internal const int DefaultPort = 9229, LegacyPort = 59321;
    internal static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameValueEditor", "ModuleSessions", "play-again-expedition");
    internal static string Key(GameSession session) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{session.ProcessId}/{session.StartTicks}")));
    private readonly FileStream _lease;
    private readonly GameSession _session;
    private readonly string _statePath;
    private bool _owned, _finished, _activationAttempted;
    internal Uri Uri { get; private set; } = null!;
    internal string Warning { get; private set; } = "";

    internal static FileStream AcquireLease(string root)
    {
        MutationGate.EnsureNoLinks(root); Directory.CreateDirectory(root); MutationGate.EnsureNoLinks(root);
        var file = Path.Combine(root, "runtime-bridge.lock"); MutationGate.EnsureNoLinks(file);
        var wait = Stopwatch.StartNew();
        while (true)
        {
            MutationGate.EnsureNoLinks(file);
            try { return new(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 && wait.Elapsed < TimeSpan.FromSeconds(30))
            { Thread.Sleep(25); }
        }
    }
    private RuntimeBridge(GameSession session)
    {
        _session = session; _lease = AcquireLease(Root);
        _statePath = Path.Combine(Root, "transport-" + Key(session) + ".json");
        try
        {
            MutationGate.EnsureNoLinks(_statePath);
            if (File.Exists(_statePath))
            {
                if (new FileInfo(_statePath).Length > 8192) throw new InvalidOperationException("接入记录无效，已停止操作。");
                using var state = JsonDocument.Parse(File.ReadAllBytes(_statePath));
                if (state.RootElement.GetProperty("processId").GetInt32() != session.ProcessId || state.RootElement.GetProperty("startTicks").GetInt64() != session.StartTicks)
                    throw new InvalidOperationException("接入记录不属于当前游戏实例，已停止操作。");
                var phase = state.RootElement.GetProperty("phase").GetString();
                if (phase != "closed")
                {
                    if ((phase is "closing" or "cleanup-failed") && Listener(DefaultPort).IsFree) Persist("closed");
                    else throw new InvalidOperationException("上次接入或临时接口清理尚未确认，已停止操作，不会重复修改；请正常重启游戏后连接。");
                }
            }
            OriginalGameSession.Assert(session, false);
            var legacy = Listener(LegacyPort); var standard = Listener(DefaultPort);
            var existing = new[] { (LegacyPort, legacy), (DefaultPort, standard) }.Where(item => item.Item2.IsOwnedBy(session.ProcessId)).ToArray();
            if (existing.Length > 1) throw new InvalidOperationException("游戏有多个本地接口，无法确认唯一目标，已停止操作。");
            var port = DefaultPort;
            if (existing.Length == 1) port = existing[0].Item1;
            else
            {
                if (!standard.IsFree) throw new InvalidOperationException("直接接入端口 9229 被其它程序占用；模块不会连接或关闭其它程序，本次未修改游戏。");
                // Persist before invoking: a host crash or timeout may otherwise replay
                // an activation whose callback is still queued in the game main thread.
                Persist("activating"); _activationAttempted = true;
                NativeRuntimeActivation.Activate(session);
                for (var attempt = 0; attempt < 100 && Listener(port).IsFree; attempt++) Thread.Sleep(50);
                RequireOwner(session, port); _owned = true; Persist("active");
            }
            RequireOwner(session, port);
            Uri = Discover(port).GetAwaiter().GetResult();
            AssertEndpoint();
        }
        catch
        {
            // Never run cleanup code on an undiscovered or unverified endpoint.
            if (_owned && Uri is not null) Finish();
            else if (_activationAttempted) TryPersist("activation-unconfirmed");
            _lease.Dispose(); throw;
        }
    }
    internal static RuntimeBridge Connect(GameSession session) => new(session);
    internal void AssertEndpoint()
    {
        OriginalGameSession.Assert(_session, false); RequireOwner(_session, Uri.Port);
    }
    private static void RequireOwner(GameSession session, int port)
    {
        if (!Listener(port).IsOwnedBy(session.ProcessId)) throw new InvalidOperationException("本地接口监听者或地址改变，已停止操作，不会连接其它进程。");
    }
    internal static Uri ValidateUri(string value, int port)
    {
        if (!System.Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "ws" || uri.Host != "127.0.0.1" || uri.Port != port ||
            uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "" || !Regex.IsMatch(uri.AbsolutePath, "^/[a-f0-9-]{36}$") ||
            !Guid.TryParseExact(uri.AbsolutePath[1..], "D", out _)) throw new InvalidOperationException("游戏接口目标无效，已停止操作。");
        return uri;
    }
    private static async Task<Uri> Discover(int port)
    {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 1048576 };
        using var document = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list").ConfigureAwait(false));
        var targets = document.RootElement;
        if (targets.ValueKind != JsonValueKind.Array || targets.GetArrayLength() != 1 || targets[0].GetProperty("type").GetString() != "node" ||
            targets[0].GetProperty("title").GetString() != "electron/js2c/browser_init") throw new InvalidOperationException("本地接口不是唯一的原游戏主进程目标。");
        return ValidateUri(targets[0].GetProperty("webSocketDebuggerUrl").GetString()!, port);
    }
    internal void Finish()
    {
        if (_finished) return; _finished = true;
        if (!_owned) return; // An interface opened outside this request is borrowed.
        try
        {
            AssertEndpoint(); Persist("closing");
            var config = JsonSerializer.Serialize(new { pid = _session.ProcessId, exe = _session.ExecutablePath, archive = _session.ArchivePath, profile = _session.ProfilePath, url = Uri.AbsoluteUri });
            var expression = "(()=>{const c=" + config + ";const p=process.mainModule.require('node:path');const i=process.mainModule.require('node:inspector');" +
                "if(process.pid!==c.pid||process.execPath!==c.exe||p.join(p.dirname(process.execPath),'resources','app.asar')!==c.archive||" +
                "process.mainModule.require('electron').app.getPath('userData')!==c.profile||i.url()!==c.url)throw Error('INTERFACE_IDENTITY_CHANGED');" +
                "setTimeout(()=>i.close(),500);return {cleanupScheduled:true,pid:process.pid};})()";
            var request = JsonSerializer.Serialize(new { id = 403, method = "Runtime.evaluate", @params = new { expression, returnByValue = true, timeout = 1000 } });
            var result = ExpeditionClient.Send(Uri, request, 403).GetAwaiter().GetResult();
            if (!result.GetProperty("cleanupScheduled").GetBoolean() || result.GetProperty("pid").GetInt32() != _session.ProcessId)
                throw new InvalidOperationException("清理回执未确认。");
            for (var attempt = 0; attempt < 100 && !Listener(DefaultPort).IsFree; attempt++) Thread.Sleep(50);
            OriginalGameSession.Assert(_session, false);
            if (!Listener(DefaultPort).IsFree) throw new InvalidOperationException("临时接口未关闭。");
            Persist("closed");
        }
        catch
        {
            Warning = "操作结果已返回，但临时接口清理未确认，不会重复执行；请先检查接入状态。";
            TryPersist("cleanup-failed");
        }
    }
    private void Persist(string phase)
    {
        using var stream = new FileStream(_statePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        stream.Write(JsonSerializer.SerializeToUtf8Bytes(new { phase, processId = _session.ProcessId, startTicks = _session.StartTicks, observedAt = DateTimeOffset.UtcNow })); stream.Flush(true);
    }
    private void TryPersist(string phase) { try { Persist(phase); } catch { } }
    public void Dispose() { Finish(); _lease.Dispose(); }

    // Count every listener on both families. Only exactly one IPv4 loopback row
    // owned by the target is eligible; wildcard/IPv6/foreign rows fail closed.
    internal static ListenerState Listener(int port)
    {
        if (port < 1 || port > 65535) throw new InvalidOperationException("本地接口端口无效。");
        var count = 0; var owner = 0;
        foreach (var family in new[] { 2, 23 })
        {
            var size = 0; var status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
            if (status != 122 || size < 4 || size > 1048576) throw new InvalidOperationException("无法可靠检查本地接口占用。");
            var table = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(table, ref size, false, family, 3, 0) != 0) throw new InvalidOperationException("无法检查本地接口监听者。");
                var rows = Marshal.ReadInt32(table); var stride = family == 2 ? 24 : 56;
                if (rows < 0 || rows > (size - 4) / stride) throw new InvalidOperationException("本地接口信息无效。");
                for (var index = 0; index < rows; index++)
                {
                    var offset = 4 + index * stride;
                    var rawPort = (uint)Marshal.ReadInt32(table, offset + (family == 2 ? 8 : 20));
                    if ((((rawPort & 255) << 8) | ((rawPort >> 8) & 255)) != port) continue;
                    count++;
                    if (family == 2 && (uint)Marshal.ReadInt32(table, offset + 4) == 0x0100007f)
                        owner = Marshal.ReadInt32(table, offset + 20);
                }
            }
            finally { Marshal.FreeHGlobal(table); }
        }
        return new(count, owner);
    }
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
}
