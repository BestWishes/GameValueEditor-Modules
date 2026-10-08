[CmdletBinding()]
param(
    [ValidateSet('Inspect', 'Probe', 'Configure', 'Reset')][string]$Operation = 'Inspect',
    [string]$ProfileJson,
    [string]$ReadReceiptPath,
    [ValidatePattern('^[a-f0-9-]{36}$')][string]$RequestId = ([guid]::NewGuid().ToString()),
    [ValidateRange(1024, 65535)][int]$Port = 59321,
    [string]$OriginalLauncherPath = 'G:/Game/Steam/steamapps/common/PlayAgainExpedition/PlayAgainExpedition.exe',
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
# Original explicit inspector only. Never launch/exit/control the game, patch
# ASAR, close the inspector service, consume tickets, invoke combat or save.
$nodeExecutable = 'C:/Users/19654/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node.exe'
$builder = Join-Path $PSScriptRoot 'build-probability-request.cjs'
$ledgerRoot = 'D:/MyOtherProjects/GameValueEditor-Modules/artifacts/play-again-expedition/probability-control'
$profilePath = 'C:\Users\19654\AppData\Roaming\ZsebExpedition'
$exeHash = 'A79F66A6F236B237E25C00DD9F8BA763F9BAD13E16CD3269DE2348EA7FFEF396'
$asarHash = '5A4F4E414BD06FC248CFE2AD30E7D9EE3B110968DB888FDB733CD57F78A6B294'
$mutating = $Operation -notin @('Inspect', 'Probe')
if ($ProfileJson -and $Operation -ne 'Configure') { throw '只有 Configure 可以传概率配置。' }
if ($Operation -eq 'Configure' -and (-not $ProfileJson -or $ProfileJson.Length -gt 8192)) { throw '需要长度不超过 8192 的固定概率 JSON。' }
$profile = if ($Operation -eq 'Configure') { $ProfileJson | ConvertFrom-Json -AsHashtable } else { $null }
if ($mutating) {
    if (-not $ReadReceiptPath -or (Get-Item -LiteralPath $ReadReceiptPath).Length -gt 131072) { throw '需要当前会话 Inspect 的读取回执。' }
    $receipt = Get-Content -LiteralPath $ReadReceiptPath -Raw -Encoding utf8 | ConvertFrom-Json -DateKind String
    if ($receipt.Schema -ne 'probability-1' -or $receipt.ExecutableSha256 -cne $exeHash -or $receipt.PackageSha256 -cne $asarHash -or
        $receipt.ScopeHash -cnotmatch '^[a-f0-9]{64}$' -or ($receipt.Revision -isnot [long] -and $receipt.Revision -isnot [int]) -or $receipt.Revision -lt 0) { throw '概率回执无效，请重新 Inspect。' }
    $age = [datetimeoffset]::UtcNow - [datetimeoffset]::Parse($receipt.ReadAt)
    if ($age.TotalSeconds -lt 0 -or $age.TotalSeconds -gt 300) { throw '读取回执已过期，请重新 Inspect。' }
}
function Build-Request($controllerConfig) {
    $operationArgs = if ($mutating) { @{ scopeHash = $receipt.ScopeHash; revision = $receipt.Revision } } else { @{} }
    if ($Operation -eq 'Configure') { $operationArgs.profile = $profile }
    $request = & $nodeExecutable $builder $Operation.ToLowerInvariant() ($controllerConfig | ConvertTo-Json -Compress) ($operationArgs | ConvertTo-Json -Depth 8 -Compress)
    if ($LASTEXITCODE -ne 0 -or -not $request) { throw '固定概率请求组装失败，没有发送。' }
    return ($request -join "`n")
}
# Validate the fixed JSON request before any process/network inspection.
$validationRequest = Build-Request @{ processId = 1; executablePath = 'validation-only'; archivePath = 'validation-only'; profilePath = $profilePath }
if ($ValidateOnly) {
    $request = $validationRequest
    [pscustomobject]@{ Request = ($request | ConvertFrom-Json -Depth 8); SendsRequest = $false; CreatesClaim = $false } | ConvertTo-Json -Depth 10
    return
}
$mainCandidates = @(Get-CimInstance Win32_Process -Filter "Name='ZsebExpedition.exe'" | Where-Object {
    $_.CommandLine -notmatch '(?:^|\s)--type=' -and $_.CommandLine -match ('(?:^|\s)' + [regex]::Escape("--inspect=127.0.0.1:$Port") + '(?:\s|$)')
})
if ($mainCandidates.Count -ne 1) { throw "原游戏没有唯一的显式实时接口。需由你正常保存退出，追加 --inspect=127.0.0.1:$Port 后重开，不会替你操作或强行开启。" }
$mainGame = $mainCandidates[0]
$launcher = Get-CimInstance Win32_Process -Filter "ProcessId=$($mainGame.ParentProcessId)"
if ($launcher.ExecutablePath -ne [IO.Path]::GetFullPath($OriginalLauncherPath)) { throw '原游戏启动器不匹配。' }
$mainExecutable = $mainGame.ExecutablePath
$archive = Join-Path ([IO.Path]::GetDirectoryName($mainExecutable)) 'resources/app.asar'
$startTicks = $mainGame.CreationDate.ToUniversalTime().Ticks
function Assert-ProbabilitySession {
    $live = Get-CimInstance Win32_Process -Filter "ProcessId=$($mainGame.ProcessId)"
    if ($live.ExecutablePath -ne $mainExecutable -or $live.CreationDate.ToUniversalTime().Ticks -ne $startTicks) { throw '游戏启动实例变化。' }
    if ((Get-FileHash -LiteralPath $mainExecutable -Algorithm SHA256).Hash -cne $exeHash -or
        (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -cne $asarHash) { throw '游戏构建未验证。' }
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction Stop)
    if ($listeners.Count -ne 1 -or $listeners[0].LocalAddress -ne '127.0.0.1' -or $listeners[0].OwningProcess -ne $mainGame.ProcessId) { throw '接口不是原游戏的唯一回环监听。' }
}
Assert-ProbabilitySession
if ($mutating -and ($receipt.ProcessId -ne $mainGame.ProcessId -or [long]$receipt.StartTicks -ne $startTicks)) { throw '回执属于另一次游戏启动。' }
$sessionKey = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("$($mainGame.ProcessId)/$startTicks"))).ToLowerInvariant()
$journalPath = Join-Path $ledgerRoot ($sessionKey + '.journal.jsonl')
$lease = $null; $dispatchClaimed = $false
function Append-Journal($entry) {
    $stream = [IO.File]::Open($journalPath, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $bytes = [Text.Encoding]::UTF8.GetBytes(($entry | ConvertTo-Json -Depth 10 -Compress) + "`n"); $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
}
try {
    if ($mutating) {
        $null = [IO.Directory]::CreateDirectory($ledgerRoot)
        $lease = [IO.File]::Open((Join-Path $ledgerRoot ($sessionKey + '.lock')), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        if (Test-Path -LiteralPath $journalPath) {
            if ((Get-Item -LiteralPath $journalPath).Length -gt 2097152) { throw '当前会话记录达到上限，请正常重启后重新读取。' }
            $last = Get-Content -LiteralPath $journalPath -Encoding utf8 | Select-Object -Last 1 | ConvertFrom-Json
            if ($last.Phase -ne 'confirmed') { throw '本会话存在不确定操作，只允许 Inspect；正常重启会恢复原概率，不自动重试或继续设置。' }
        }
    }
    $handler = [Net.Http.HttpClientHandler]::new(); $handler.UseProxy = $false; $handler.AllowAutoRedirect = $false
    $http = [Net.Http.HttpClient]::new($handler); $http.Timeout = [TimeSpan]::FromSeconds(5); $http.MaxResponseContentBufferSize = 1048576
    try {
        $targets = @($http.GetStringAsync("http://127.0.0.1:$Port/json/list").GetAwaiter().GetResult() | ConvertFrom-Json)
        if ($targets.Count -ne 1 -or $targets[0].type -ne 'node' -or $targets[0].title -ne 'electron/js2c/browser_init') { throw '接口目标不匹配。' }
        $socketUri = [uri]$targets[0].webSocketDebuggerUrl
        if ($socketUri.Scheme -ne 'ws' -or $socketUri.Host -ne '127.0.0.1' -or $socketUri.Port -ne $Port -or $socketUri.UserInfo -or
            $socketUri.Query -or $socketUri.Fragment -or $socketUri.AbsolutePath -notmatch '^/[0-9a-f-]{36}$') { throw '接口地址无效。' }
    } finally { $http.Dispose(); $handler.Dispose() }
    $request = Build-Request @{ processId = [int]$mainGame.ProcessId; executablePath = $mainExecutable; archivePath = $archive; profilePath = $profilePath }
    Assert-ProbabilitySession
    if ($mutating) {
        $claim = [IO.File]::Open((Join-Path $ledgerRoot ('request-' + $RequestId + '.json')), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        try { $bytes = [Text.Encoding]::UTF8.GetBytes((@{ RequestId = $RequestId; Session = $sessionKey; Operation = $Operation; ScopeHash = $receipt.ScopeHash; Revision = $receipt.Revision } | ConvertTo-Json -Compress)); $claim.Write($bytes, 0, $bytes.Length); $claim.Flush($true) }
        finally { $claim.Dispose() }
        Append-Journal @{ Phase = 'pending'; RequestId = $RequestId; ClaimedAt = [datetimeoffset]::UtcNow.ToString('o') }; $dispatchClaimed = $true
    }
    $socket = [Net.WebSockets.ClientWebSocket]::new(); $socket.Options.Proxy = $null
    $cancellation = [Threading.CancellationTokenSource]::new(15000); $closeError = $null
    try {
        $null = $socket.ConnectAsync($socketUri, $cancellation.Token).GetAwaiter().GetResult()
        $bytes = [Text.Encoding]::UTF8.GetBytes($request)
        $null = $socket.SendAsync([ArraySegment[byte]]::new($bytes), [Net.WebSockets.WebSocketMessageType]::Text, $true, $cancellation.Token).GetAwaiter().GetResult()
        $response = $null
        for ($index = 0; $index -lt 32; $index++) {
            $stream = [IO.MemoryStream]::new()
            try {
                do {
                    $chunk = [byte[]]::new(4096)
                    $received = $socket.ReceiveAsync([ArraySegment[byte]]::new($chunk), $cancellation.Token).GetAwaiter().GetResult()
                    if ($received.MessageType -ne [Net.WebSockets.WebSocketMessageType]::Text) { throw '接口消息类型无效，不要重试。' }
                    $stream.Write($chunk, 0, $received.Count)
                    if ($stream.Length -gt 131072) { throw '接口响应过大，不要重试。' }
                } while (-not $received.EndOfMessage)
                $message = [Text.Encoding]::UTF8.GetString($stream.ToArray()) | ConvertFrom-Json -Depth 24
            } finally { $stream.Dispose() }
            if ($message.id -eq 402) { $response = $message; break }
            if ($message.id) { throw '接口响应 ID 不匹配。' }
        }
        if (-not $response -or $response.error) { throw '接口没有确定结果，没有自动重试。' }
        if ($response.result.exceptionDetails) {
            $description = [string]$response.result.exceptionDetails.exception.description
            $guardCode = if ($description -match '^Error:\s*([A-Z][A-Z_]{0,80})(?:[\r\n:]|$)') { $Matches[1] } else { 'RUNTIME_EXECUTION_UNCONFIRMED' }
            throw "概率操作未确认（$guardCode）；不自动重试，请先 Inspect。"
        }
        $observation = $response.result.result.value
    } finally {
        if ($socket.State -eq [Net.WebSockets.WebSocketState]::Open) {
            $closeTimeout = [Threading.CancellationTokenSource]::new(3000)
            try { $null = $socket.CloseAsync([Net.WebSockets.WebSocketCloseStatus]::NormalClosure, 'probability operation finished', $closeTimeout.Token).GetAwaiter().GetResult() }
            catch { $closeError = $_ }
            finally { $closeTimeout.Dispose() }
        }
        $socket.Dispose(); $cancellation.Dispose()
    }
    if ($closeError) { throw '客户端断开未正常完成，不要重试设置。' }
    Assert-ProbabilitySession
    $result = $observation.probability
    if ($result.sessionOnly -ne $true -or $result.nativeSaveCalled -ne $false -or $result.gameActionCalled -ne $false -or
        $observation.scopeHash -cnotmatch '^[a-f0-9]{64}$' -or $result.report.ownerCurrent -ne $true) { throw '概率结果不完整。' }
    if ($mutating) {
        if ($observation.scopeHash -cne $receipt.ScopeHash -or $result.revision -ne ($receipt.Revision + 1) -or $result.readOnly -ne $false) { throw '概率回执未确认。' }
        if ($Operation -eq 'Configure' -and ($result.report.installed -ne $true -or $result.report.hooksOwned -ne $true -or $result.report.rngOriginal -ne $true -or $result.report.blocked)) { throw '概率设置未确认。' }
        if ($Operation -eq 'Configure') {
            # Compare normalized JSON object structure, not property serialization order.
            $expected = $profile | ConvertTo-Json -Depth 8 -Compress | ConvertFrom-Json -AsHashtable
            $actual = $result.report.settings | ConvertTo-Json -Depth 8 -Compress | ConvertFrom-Json -AsHashtable
            function Normalize-Data($data) {
                if ($data -is [Collections.IDictionary]) {
                    $ordered = [ordered]@{}; foreach ($key in ($data.Keys | Sort-Object -CaseSensitive)) { $ordered[$key] = Normalize-Data $data[$key] }; return $ordered
                }
                if ($data -is [int] -or $data -is [long] -or $data -is [double] -or $data -is [decimal]) { return [double]$data }
                return $data
            }
            if (((Normalize-Data $expected) | ConvertTo-Json -Depth 8 -Compress) -cne ((Normalize-Data $actual) | ConvertTo-Json -Depth 8 -Compress)) { throw '实际概率参数与目标不一致，不继续设置。' }
        }
        if ($Operation -eq 'Reset' -and ($result.outcome.restored -ne $true -or $result.outcome.rngOriginal -ne $true -or $result.report.installed -ne $false)) { throw '概率恢复存在冲突，不继续操作。' }
        Append-Journal @{ Phase = 'confirmed'; RequestId = $RequestId; Result = $observation; ObservedAt = [datetimeoffset]::UtcNow.ToString('o') }
    } else {
        if ($result.readOnly -ne $true) { throw '读取结果不完整。' }
        $null = [IO.Directory]::CreateDirectory($ledgerRoot)
        if (-not $ReadReceiptPath) { $ReadReceiptPath = Join-Path $ledgerRoot ('read-' + [guid]::NewGuid().ToString() + '.json') }
        $record = @{ Schema = 'probability-1'; ReadAt = [datetimeoffset]::UtcNow.ToString('o'); ProcessId = [int]$mainGame.ProcessId; StartTicks = $startTicks;
            ExecutableSha256 = $exeHash; PackageSha256 = $asarHash; ScopeHash = $observation.scopeHash; Revision = $result.revision; Report = $result.report; Diagnostic = $result.diagnostic }
        $file = [IO.File]::Open($ReadReceiptPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        try { $bytes = [Text.Encoding]::UTF8.GetBytes(($record | ConvertTo-Json -Depth 12)); $file.Write($bytes, 0, $bytes.Length); $file.Flush($true) }
        finally { $file.Dispose() }
    }
    [pscustomobject]@{ Observation = $observation; ReadReceiptPath = $ReadReceiptPath; OriginalGameStillRunning = $true;
        NewBackupCreated = $false; AutomaticRetry = $false; InspectorServerClosed = $false } | ConvertTo-Json -Depth 14
} catch {
    if ($dispatchClaimed) { Append-Journal @{ Phase = 'uncertain'; RequestId = $RequestId; ObservedAt = [datetimeoffset]::UtcNow.ToString('o') } }
    throw
} finally { if ($lease) { $lease.Dispose() } }
