using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace ProtectionScrollTest;

internal static class Program
{
    [STAThread]
    public static void Main() => new Application().Run(new TestWindow());
}

internal sealed record Connection(int Protocol, int Pid, string ExePath, string Session, int Port, string Token, string GameVersion, string OriginalAsarSha256);
internal sealed record Snapshot(string Instance, long Value, string Mode, string Slot);
internal sealed record TestRecord(long Original, long Target, string Mode, string Slot, string Session, string Phase);

internal sealed class TestWindow : Window
{
    private const long Maximum = 1_000_000_000;
    private const string OriginalHash = "5E5394C50B67A71CF0965617F850A6DA91484D5BE2D5FEA5A058E1F8D7C68ECB";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
    private readonly HttpClient http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };
    private readonly Button read = new() { Content = "读取数量", MinWidth = 106, Margin = new Thickness(0, 0, 12, 0) };
    private readonly Button plus = new() { Content = "测试 +1", MinWidth = 106, Margin = new Thickness(0, 0, 12, 0), IsEnabled = false };
    private readonly Button restore = new() { Content = "恢复原数量", MinWidth = 120, IsEnabled = false };
    private readonly TextBlock count = new() { Text = "装备保护卷：尚未读取", FontSize = 24, Margin = new Thickness(0, 18, 0, 18) };
    private readonly TextBlock status = new() { Text = "由你启动测试游戏、进入原有存档，然后点击“读取数量”。工具不会自动写入。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 0) };
    private TestRecord? record;
    private Snapshot? snapshot;
    private Connection? connection;
    private bool busy, recordInvalid;
    private string RecordPath => Path.Combine(AppContext.BaseDirectory, "test-record.json");

    public TestWindow()
    {
        Title = "装备保护卷 · 本地实时测试";
        Width = 590; Height = 360; MinWidth = 540; MinHeight = 330;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "仅测试副本，正常游戏存档不覆盖。请停在安全界面，不战斗、不换档。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(count);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(read); buttons.Children.Add(plus); buttons.Children.Add(restore);
        foreach (var button in new[] { read, plus, restore }) button.MinHeight = 34;
        panel.Children.Add(buttons); panel.Children.Add(status);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        read.Click += async (_, _) => await Guard(ReadAsync);
        plus.Click += async (_, _) => await Guard(PlusAsync);
        restore.Click += async (_, _) => await Guard(RestoreAsync);
        Closing += (_, e) => { if (busy) { e.Cancel = true; status.Text = "请求进行中，请等待结果；不要重复发送写入。"; } };
        Closed += (_, _) => http.Dispose();
        if (File.Exists(RecordPath))
        {
            try
            {
                if (new FileInfo(RecordPath).Length > 4096) throw new InvalidDataException();
                record = JsonSerializer.Deserialize<TestRecord>(File.ReadAllText(RecordPath), JsonOptions);
                if (record is null || record.Original < 0 || record.Original >= Maximum || record.Target != record.Original + 1 || !Guid.TryParse(record.Session, out _) || !new[] { "Pending", "Applied", "Restoring", "Unknown" }.Contains(record.Phase)) throw new InvalidDataException();
                status.Text = "发现上次测试记录。先读取当前数量，确认仍是同一存档，再决定是否恢复。";
            }
            catch { recordInvalid = true; status.Text = "测试记录损坏：只允许读取，不允许写入。请告知开发者，不要删除记录后继续试。"; }
        }
    }

    private async Task Guard(Func<Task> action)
    {
        if (busy) return;
        busy = true; SetButtons();
        try { await action(); }
        catch (Exception error) { snapshot = null; connection = null; status.Text = Explain(error); }
        finally { busy = false; SetButtons(); }
    }

    private void SetButtons()
    {
        read.IsEnabled = !busy;
        plus.IsEnabled = !busy && !recordInvalid && snapshot is not null && record is null && snapshot.Value < Maximum;
        restore.IsEnabled = !busy && !recordInvalid && snapshot is not null && connection is not null && record is not null
            && record.Mode == snapshot.Mode && record.Slot == snapshot.Slot && snapshot.Value == record.Target
            && (record.Phase == "Applied" || connection.Session != record.Session);
    }

    private Connection ReadConnection()
    {
        var descriptor = Path.Combine(root, "profile-test", "gve-protection-scroll-test", "connection.json");
        if (!File.Exists(descriptor)) throw new InvalidOperationException("TEST_NOT_STARTED");
        if (new FileInfo(descriptor).Length > 4096) throw new InvalidOperationException("CONNECTION_INVALID");
        var result = JsonSerializer.Deserialize<Connection>(File.ReadAllText(descriptor), JsonOptions);
        var expectedExe = Path.Combine(root, "runtime", "ZsebExpedition.exe");
        if (result is null || result.Protocol != 1 || result.GameVersion != "0.116.68" || result.OriginalAsarSha256 != OriginalHash || result.Port is < 1 or > 65535 || !Guid.TryParse(result.Session, out _) || result.Token is null || result.Token.Length != 64 || result.Token.Any(c => !Uri.IsHexDigit(c)) || !string.Equals(Path.GetFullPath(result.ExePath), expectedExe, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("CONNECTION_INVALID");
        using var process = Process.GetProcessById(result.Pid);
        if (process.HasExited || !string.Equals(process.MainModule?.FileName, expectedExe, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("TEST_NOT_STARTED");
        return result;
    }

    private async Task<JsonElement> RequestAsync(Connection current, object? payload)
    {
        using var request = new HttpRequestMessage(payload is null ? HttpMethod.Get : HttpMethod.Post, $"http://127.0.0.1:{current.Port}/{(payload is null ? "read" : "write")}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", current.Token);
        if (payload is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead);
        var text = await response.Content.ReadAsStringAsync();
        if (text.Length > 4096) throw new InvalidOperationException("CONNECTION_INVALID");
        using var parsed = JsonDocument.Parse(text);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(parsed.RootElement.TryGetProperty("error", out var code) ? code.GetString() : "BRIDGE_OPERATION_FAILED");
        return parsed.RootElement.Clone();
    }

    private static Snapshot ParseSnapshot(Connection current, JsonElement json)
    {
        var instance = json.GetProperty("instance").GetString();
        var value = json.GetProperty("value").GetInt64();
        if (instance is null || !instance.StartsWith(current.Session + ":", StringComparison.Ordinal) || value is < 0 or > Maximum || json.GetProperty("maximum").GetInt64() != Maximum) throw new InvalidOperationException("CONTRACT_CHANGED");
        return new Snapshot(instance, value, json.GetProperty("mode").GetRawText(), json.GetProperty("slot").GetRawText());
    }

    private async Task ReadAsync()
    {
        connection = ReadConnection();
        snapshot = ParseSnapshot(connection, await RequestAsync(connection, null));
        count.Text = $"装备保护卷：{snapshot.Value:N0}";
        if (recordInvalid) { status.Text = "测试记录损坏，本次仅只读。请告知开发者，不要继续写入。"; return; }
        if (record is null) status.Text = "已只读获取实时数量。先在游戏里对照，确认正确后再点击“测试 +1”。";
        else if (record.Mode != snapshot.Mode || record.Slot != snapshot.Slot) status.Text = "当前模式或存档槽位与测试记录不符。请由你切回原存档，不允许写入。";
        else if (snapshot.Value == record.Original && (record.Phase == "Applied" || connection.Session != record.Session))
        {
            ClearRecord(); status.Text = "读到原数量。请在游戏界面确认；持久化是否成功仍要由你正常重启后核对。";
        }
        else if (record.Phase != "Applied" && connection.Session == record.Session) status.Text = "上次写入结果不确定。不要重试；请由你正常退出并重启测试游戏，再读取确认。";
        else if (snapshot.Value == record.Target) status.Text = $"读到测试数量 {record.Target}（原数量 {record.Original}）。由你确认画面和重启结果；之后可手动恢复。";
        else status.Text = "数量与测试前和 +1 后均不一致，可能发生了游戏内消耗或收益。工具拒绝覆盖，请告知当前情况。";
    }

    private void SaveRecord()
    {
        var staged = RecordPath + ".tmp";
        File.WriteAllText(staged, JsonSerializer.Serialize(record), new UTF8Encoding(false));
        File.Move(staged, RecordPath, true);
    }

    private void ClearRecord()
    {
        File.Delete(RecordPath); record = null;
    }

    private async Task<(Connection Connection, Snapshot Snapshot)> FreshAsync()
    {
        var current = ReadConnection();
        var fresh = ParseSnapshot(current, await RequestAsync(current, null));
        if (snapshot is null || fresh.Instance != snapshot.Instance || fresh.Value != snapshot.Value) throw new InvalidOperationException("VALUE_CHANGED");
        return (current, fresh);
    }

    private async Task WriteAsync(Connection current, Snapshot fresh, long target)
    {
        var result = await RequestAsync(current, new { expectedInstance = fresh.Instance, expectedValue = fresh.Value, targetValue = target, requestId = Guid.NewGuid().ToString() });
        var changed = ParseSnapshot(current, result);
        if (changed.Value != target || changed.Instance != fresh.Instance || !result.TryGetProperty("storageVerified", out var verified) || verified.ValueKind != JsonValueKind.True || !result.TryGetProperty("onlyMaterialChanged", out var only) || only.ValueKind != JsonValueKind.True) throw new InvalidOperationException("PERSISTENCE_UNCONFIRMED");
        connection = current; snapshot = changed; count.Text = $"装备保护卷：{changed.Value:N0}";
    }

    private async Task PlusAsync()
    {
        if (recordInvalid || record is not null) return;
        var (current, fresh) = await FreshAsync();
        if (fresh.Value >= Maximum) throw new InvalidOperationException("INVALID_TARGET_VALUE");
        record = new TestRecord(fresh.Value, fresh.Value + 1, fresh.Mode, fresh.Slot, current.Session, "Pending");
        SaveRecord(); // Record original before any possibly effective write.
        try
        {
            await WriteAsync(current, fresh, record.Target);
            record = record with { Phase = "Applied" }; SaveRecord();
            status.Text = "+1 已通过接口和本地存储核验。请由你看游戏数量；再正常退出、用测试入口重启，读取确认仍为 +1。";
        }
        catch { record = record with { Phase = "Unknown" }; SaveRecord(); throw; }
    }

    private async Task RestoreAsync()
    {
        if (recordInvalid || record is null) return;
        var (current, fresh) = await FreshAsync();
        if (fresh.Mode != record.Mode || fresh.Slot != record.Slot || fresh.Value != record.Target || (record.Phase != "Applied" && current.Session == record.Session)) throw new InvalidOperationException("SAVE_INSTANCE_CHANGED");
        if (MessageBox.Show(this, $"请确认仍是测试时的同一存档，并且没有消耗或获取保护卷。\n\n将装备保护卷从 {fresh.Value} 恢复为 {record.Original}。", "手动恢复确认", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        record = record with { Phase = "Restoring", Session = current.Session }; SaveRecord();
        try
        {
            await WriteAsync(current, fresh, record.Original);
            ClearRecord();
            status.Text = "原数量已通过接口和本地存储核验。请由你确认游戏画面，再正常重启核对；不会把测试存档覆盖回正常游戏。";
        }
        catch { record = record with { Phase = "Unknown" }; SaveRecord(); throw; }
    }

    private static string Explain(Exception error) => error is TaskCanceledException || error.Message.Contains("TIMEOUT", StringComparison.Ordinal)
        ? "请求超时，结果不确定，不会自动重试。请勿反复点击；由你重启测试游戏后再读取，确认实际数量。"
        : error.Message switch
        {
            "TEST_NOT_STARTED" => "测试游戏未启动或已退出。请由你使用“启动测试游戏.cmd”，再进入存档。",
            "GAME_NOT_READY" or "ENTER_EXISTING_SAVE" => "请由你在测试游戏中进入现有存档，等待加载完成，再读取。",
            "VALUE_CHANGED" or "SNAPSHOT_CHANGED" => "游戏状态或数量发生变化，本次没有继续覆盖。请停在安全界面，重新读取后核对。",
            "SAVE_INSTANCE_CHANGED" => "游戏重启、换档或实例发生变化。请重新读取，确认是测试时的同一存档。",
            "BRIDGE_DISABLED" => "本次测试接口已禁止写入。不要重试；请由你正常重启测试游戏，再读取实际数量。",
            "SAVE_FAILED_LIVE_RESTORED" => "原生保存失败，运行时尝试恢复旧值，接口已禁写。请停止测试并告知我。",
            "PERSISTENCE_UNCONFIRMED" or "SAVE_RESULT_UNCERTAIN" => "保存结果未能完整确认。请停止写入，不要重试；由你核对游戏数量并正常重启读取。",
            "INVALID_TARGET_VALUE" => "数量超出游戏允许范围，不会修改。",
            "CONNECTION_INVALID" or "CONTRACT_CHANGED" or "SAVE_CONTRACT_CHANGED" => "游戏构建、接口或存档格式不符合本轮测试契约，拒绝写入。",
            _ => "无法完成本次操作，不会自动重试。请告诉我你点了哪个按钮及当前界面；不要删除备份或测试记录。"
        };
}
