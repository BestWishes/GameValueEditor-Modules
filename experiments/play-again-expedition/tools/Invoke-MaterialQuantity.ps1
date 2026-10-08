[CmdletBinding()]
param(
    [ValidateSet('Catalog', 'List', 'Set')][string]$Operation = 'List',
    [string]$MaterialId,
    [ValidateRange(0, 1000000000)][long]$TargetValue,
    [string]$ReadReceiptPath,
    [ValidatePattern('^[a-f0-9-]{36}$')][string]$RequestId = ([guid]::NewGuid().ToString()),
    [ValidateRange(1024, 65535)][int]$Port = 59321,
    [string]$OriginalLauncherPath = 'G:/Game/Steam/steamapps/common/PlayAgainExpedition/PlayAgainExpedition.exe',
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
# Fixed local original-game operations; never launch/control/kill the game,
# patch its files, forcibly enable an inspector, close its inspector service,
# open LevelDB, export a complete save, retry a write or create new backups.
$nodeExecutable = 'C:/Users/19654/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node.exe'
$builder = Join-Path $PSScriptRoot 'build-materials-request.cjs'
$ledgerRoot = 'D:/MyOtherProjects/GameValueEditor-Modules/artifacts/play-again-expedition/all-materials-control'
$profilePath = 'C:\Users\19654\AppData\Roaming\ZsebExpedition'
$exeHash = 'A79F66A6F236B237E25C00DD9F8BA763F9BAD13E16CD3269DE2348EA7FFEF396'
$asarHash = '5A4F4E414BD06FC248CFE2AD30E7D9EE3B110968DB888FDB733CD57F78A6B294'
function Get-Catalog {
    $output = & $nodeExecutable $builder 'catalog'
    if ($LASTEXITCODE -ne 0) { throw '材料定义读取失败。' }
    return @($output | ConvertFrom-Json)
}
$catalog = Get-Catalog
if ($Operation -eq 'Catalog') { $catalog | ConvertTo-Json -Depth 4; return }
if ($Operation -eq 'Set') {
    if (-not $PSBoundParameters.ContainsKey('TargetValue') -or -not $MaterialId -or -not $ReadReceiptPath) { throw '设置数量需要 MaterialId、TargetValue 和当前会话的 ReadReceiptPath。' }
    $definition = @($catalog | Where-Object id -CEQ $MaterialId)
    if ($definition.Count -ne 1 -or $TargetValue -gt $definition[0].maximum) { throw '未知材料或数量超出游戏范围。' }
    if ((Get-Item -LiteralPath $ReadReceiptPath).Length -gt 131072) { throw '读取回执过大。' }
    $receipt = Get-Content -LiteralPath $ReadReceiptPath -Encoding utf8 -Raw | ConvertFrom-Json -DateKind String
    $receiptRow = @($receipt.Rows | Where-Object id -CEQ $MaterialId)
    if ($receipt.Schema -ne 1 -or $receiptRow.Count -ne 1 -or $receiptRow[0].canWrite -ne $true -or
        $receipt.ExecutableSha256 -cne $exeHash -or $receipt.PackageSha256 -cne $asarHash -or
        $receipt.ScopeHash -cnotmatch '^[a-f0-9]{64}$') { throw '读取回执无效或该材料不可写，请重新读取。' }
    $age = [datetimeoffset]::UtcNow - [datetimeoffset]::Parse($receipt.ReadAt)
    if ($age.TotalSeconds -lt 0 -or $age.TotalSeconds -gt 300) { throw '读取回执已过期，请重新读取。' }
}
function Build-Request($controllerConfig, $operationArgs) {
    $configJson = $controllerConfig | ConvertTo-Json -Compress
    $argsJson = $operationArgs | ConvertTo-Json -Compress
    $request = & $nodeExecutable $builder $Operation.ToLowerInvariant() $configJson $argsJson
    if ($LASTEXITCODE -ne 0 -or -not $request) { throw '固定材料请求组装失败；没有发送。' }
    return ($request -join "`n")
}
if ($ValidateOnly) {
    $operationArgs = if ($Operation -eq 'List') { @{} } else { @{ materialId = $MaterialId; expectedValue = $receiptRow[0].value;
        targetValue = $TargetValue; slot = $receipt.Slot; journeyMode = $receipt.JourneyMode; scopeHash = $receipt.ScopeHash } }
    $request = Build-Request @{ processId = 1; executablePath = 'validation-only'; archivePath = 'validation-only'; profilePath = $profilePath } $operationArgs
    [pscustomobject]@{ Request = ($request | ConvertFrom-Json -Depth 8); SendsRequest = $false; CreatesClaim = $false } | ConvertTo-Json -Depth 10
    return
}
$mainCandidates = @(Get-CimInstance Win32_Process -Filter "Name='ZsebExpedition.exe'" | Where-Object {
    $_.CommandLine -notmatch '(?:^|\s)--type=' -and
    $_.CommandLine -match ('(?:^|\s)' + [regex]::Escape("--inspect=127.0.0.1:$Port") + '(?:\s|$)')
})
if ($mainCandidates.Count -ne 1) { throw "未找到唯一的原游戏实时接口。请由你正常保存退出，在原启动选项追加 --inspect=127.0.0.1:$Port 后正常重开；不会强行开启或替你操作。" }
$mainGame = $mainCandidates[0]
$launcher = Get-CimInstance Win32_Process -Filter "ProcessId=$($mainGame.ParentProcessId)"
if ($launcher.ExecutablePath -ne [IO.Path]::GetFullPath($OriginalLauncherPath)) { throw '原游戏启动器不匹配，停止连接。' }
$mainExecutable = $mainGame.ExecutablePath
$archive = Join-Path ([IO.Path]::GetDirectoryName($mainExecutable)) 'resources/app.asar'
$startTicks = $mainGame.CreationDate.ToUniversalTime().Ticks
function Assert-MaterialSession {
    $live = Get-CimInstance Win32_Process -Filter "ProcessId=$($mainGame.ProcessId)"
    if ($live.ExecutablePath -ne $mainExecutable -or $live.CreationDate.ToUniversalTime().Ticks -ne $startTicks) { throw '游戏启动实例发生变化。' }
    if ((Get-FileHash -LiteralPath $mainExecutable -Algorithm SHA256).Hash -cne $exeHash -or
        (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -cne $asarHash) { throw '游戏构建未验证，停止读写。' }
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction Stop)
    if ($listeners.Count -ne 1 -or $listeners[0].LocalAddress -ne '127.0.0.1' -or $listeners[0].OwningProcess -ne $mainGame.ProcessId) { throw '实时接口不是原游戏的唯一回环监听。' }
}
Assert-MaterialSession
if ($Operation -eq 'Set' -and ($receipt.ProcessId -ne $mainGame.ProcessId -or [long]$receipt.StartTicks -ne $startTicks)) { throw '回执属于另一次游戏启动，请重新读取。' }
$sessionKey = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("$($mainGame.ProcessId)/$startTicks"))).ToLowerInvariant()
$journalPath = Join-Path $ledgerRoot ($sessionKey + '.journal.jsonl')
$lease = $null
function Append-Journal($entry) {
    $stream = [IO.File]::Open($journalPath, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $bytes = [Text.Encoding]::UTF8.GetBytes(($entry | ConvertTo-Json -Depth 5 -Compress) + "`n"); $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
}
$dispatchClaimed = $false
try {
    if ($Operation -eq 'Set') {
        $null = [IO.Directory]::CreateDirectory($ledgerRoot)
        $lease = [IO.File]::Open((Join-Path $ledgerRoot ($sessionKey + '.lock')), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        if (Test-Path -LiteralPath $journalPath) {
            if ((Get-Item -LiteralPath $journalPath).Length -gt 2097152) { throw '当前会话记录达到安全上限，请正常重启后重新读取。' }
            $last = Get-Content -LiteralPath $journalPath -Encoding utf8 | Select-Object -Last 1 | ConvertFrom-Json
            if ($last.Phase -ne 'confirmed') { throw '本会话有结果不确定的写入；仅允许读取核验，不得继续写入或自动重试。' }
        }
    }
    $handler = [Net.Http.HttpClientHandler]::new(); $handler.UseProxy = $false; $handler.AllowAutoRedirect = $false
    $http = [Net.Http.HttpClient]::new($handler); $http.Timeout = [TimeSpan]::FromSeconds(5); $http.MaxResponseContentBufferSize = 1048576
    try {
        $targets = @($http.GetStringAsync("http://127.0.0.1:$Port/json/list").GetAwaiter().GetResult() | ConvertFrom-Json)
        if ($targets.Count -ne 1 -or $targets[0].type -ne 'node' -or $targets[0].title -ne 'electron/js2c/browser_init') { throw '实时接口目标不匹配。' }
        $socketUri = [uri]$targets[0].webSocketDebuggerUrl
        if ($socketUri.Scheme -ne 'ws' -or $socketUri.Host -ne '127.0.0.1' -or $socketUri.Port -ne $Port -or $socketUri.UserInfo -or $socketUri.Query -or $socketUri.Fragment -or $socketUri.AbsolutePath -notmatch '^/[0-9a-f-]{36}$') { throw '实时接口地址无效。' }
    } finally { $http.Dispose(); $handler.Dispose() }
    $operationArgs = if ($Operation -eq 'List') { @{} } else { @{ materialId = $MaterialId; expectedValue = $receiptRow[0].value;
        targetValue = $TargetValue; slot = $receipt.Slot; journeyMode = $receipt.JourneyMode; scopeHash = $receipt.ScopeHash } }
    $request = Build-Request @{ processId = [int]$mainGame.ProcessId; executablePath = $mainExecutable; archivePath = $archive; profilePath = $profilePath } $operationArgs
    Assert-MaterialSession
    if ($Operation -eq 'Set') {
        # Persist exclusive no-replay claim and pending gate BEFORE connection/send.
        $claimPath = Join-Path $ledgerRoot ('request-' + $RequestId + '.json')
        $claim = [IO.File]::Open($claimPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        try { $claimBytes = [Text.Encoding]::UTF8.GetBytes((@{ RequestId = $RequestId; Session = $sessionKey; MaterialId = $MaterialId;
            ExpectedValue = $receiptRow[0].value; TargetValue = $TargetValue } | ConvertTo-Json -Compress)); $claim.Write($claimBytes, 0, $claimBytes.Length); $claim.Flush($true) }
        finally { $claim.Dispose() }
        Append-Journal @{ Phase = 'pending'; RequestId = $RequestId; ClaimedAt = [datetimeoffset]::UtcNow.ToString('o') }
        $dispatchClaimed = $true
    }
    $socket = [Net.WebSockets.ClientWebSocket]::new(); $socket.Options.Proxy = $null
    $cancellation = [Threading.CancellationTokenSource]::new(15000)
    $closeError = $null
    try {
        $null = $socket.ConnectAsync($socketUri, $cancellation.Token).GetAwaiter().GetResult()
        $requestBytes = [Text.Encoding]::UTF8.GetBytes($request)
        $null = $socket.SendAsync([ArraySegment[byte]]::new($requestBytes), [Net.WebSockets.WebSocketMessageType]::Text, $true, $cancellation.Token).GetAwaiter().GetResult()
        $response = $null
        for ($messageNumber = 0; $messageNumber -lt 32; $messageNumber++) {
            $stream = [IO.MemoryStream]::new()
            try {
                do {
                    $chunk = [byte[]]::new(4096)
                    $received = $socket.ReceiveAsync([ArraySegment[byte]]::new($chunk), $cancellation.Token).GetAwaiter().GetResult()
                    if ($received.MessageType -ne [Net.WebSockets.WebSocketMessageType]::Text) { throw '实时接口消息类型无效；不要重试写入。' }
                    $stream.Write($chunk, 0, $received.Count)
                    if ($stream.Length -gt 131072) { throw '实时接口响应过大；不要重试写入。' }
                } while (-not $received.EndOfMessage)
                $message = [Text.Encoding]::UTF8.GetString($stream.ToArray()) | ConvertFrom-Json -Depth 20
            } finally { $stream.Dispose() }
            if ($message.id -eq 401) { $response = $message; break }
            if ($message.id) { throw '实时接口响应 ID 不匹配。' }
        }
        if (-not $response -or $response.error) { throw '接口协议未得到确定结果；没有自动重试。' }
        if ($response.result.exceptionDetails) {
            # Keep known guard codes, never publish main-process stack/path data.
            $description = [string]$response.result.exceptionDetails.exception.description
            $guardCode = if ($description -match '^Error:\s*([A-Z][A-Z_]{0,80})(?:[\r\n:]|$)') { $Matches[1] } else { 'RUNTIME_EXECUTION_UNCONFIRMED' }
            if ($Operation -eq 'List') { throw "材料只读检查未完成（$guardCode）；没有修改数量或调用保存。" }
            throw "写入未得到确定结果（$guardCode）；不要重试，请先只读检查。"
        }
        $observation = $response.result.result.value
    } finally {
        # Graceful client disconnect on guard failures as well as success.
        # This never calls the inspector service's shutdown API.
        if ($socket.State -eq [Net.WebSockets.WebSocketState]::Open) {
            $closeTimeout = [Threading.CancellationTokenSource]::new(3000)
            try { $null = $socket.CloseAsync([Net.WebSockets.WebSocketCloseStatus]::NormalClosure, 'material operation finished', $closeTimeout.Token).GetAwaiter().GetResult() }
            catch { $closeError = $_ }
            finally { $closeTimeout.Dispose() }
        }
        $socket.Dispose(); $cancellation.Dispose()
    }
    if ($closeError) { throw '客户端断开未正常完成；不要重试写入。' }
    Assert-MaterialSession
    if ($Operation -eq 'Set') {
        $confirmed = $observation.material.status -eq 'unchanged' -and $observation.material.nativeSaveCalled -eq $false
        $confirmed = $confirmed -or ($observation.material.status -eq 'storage-verified' -and $observation.material.nativeSaveCalled -eq $true -and
            $observation.material.storageVerified -eq $true -and $observation.material.onlySerializedMaterialChanged -eq $true -and $observation.flushRequested -eq $true)
        $confirmed = $confirmed -and $observation.material.materialId -ceq $MaterialId -and $observation.material.value -eq $TargetValue -and
            $observation.material.previousValue -eq $receiptRow[0].value -and $observation.material.slot -ceq $receipt.Slot -and
            $observation.material.journeyMode -ceq $receipt.JourneyMode -and $observation.scopeHash -ceq $receipt.ScopeHash
        if (-not $confirmed) { throw '本次写入未完整确认；已停止本会话写入，不自动恢复或重试。' }
        Append-Journal @{ Phase = 'confirmed'; RequestId = $RequestId; Result = $observation; ObservedAt = [datetimeoffset]::UtcNow.ToString('o') }
    } else {
        if ($observation.material.readOnly -ne $true -or $observation.material.nativeSaveCalled -ne $false -or
            $observation.material.saveFormat -ne 38 -or $observation.scopeHash -cnotmatch '^[a-f0-9]{64}$') { throw '读取结果不完整。' }
        $null = [IO.Directory]::CreateDirectory($ledgerRoot)
        if (-not $ReadReceiptPath) { $ReadReceiptPath = Join-Path $ledgerRoot ('read-' + [guid]::NewGuid().ToString() + '.json') }
        $record = @{ Schema = 1; ReadAt = [datetimeoffset]::UtcNow.ToString('o'); ProcessId = [int]$mainGame.ProcessId; StartTicks = $startTicks;
            ExecutableSha256 = $exeHash; PackageSha256 = $asarHash; ScopeHash = $observation.scopeHash;
            Slot = $observation.material.slot; JourneyMode = $observation.material.journeyMode; Rows = $observation.material.rows }
        $file = [IO.File]::Open($ReadReceiptPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        try { $recordBytes = [Text.Encoding]::UTF8.GetBytes(($record | ConvertTo-Json -Depth 6)); $file.Write($recordBytes, 0, $recordBytes.Length); $file.Flush($true) }
        finally { $file.Dispose() }
    }
    [pscustomobject]@{ Observation = $observation; ReadReceiptPath = $ReadReceiptPath; OriginalGameStillRunning = $true;
        NewBackupCreated = $false; AutomaticRetry = $false; InspectorServerClosed = $false } | ConvertTo-Json -Depth 8
} catch {
    if ($dispatchClaimed) {
        # Keep pending/uncertain gate even when the error happened before send.
        Append-Journal @{ Phase = 'uncertain'; RequestId = $RequestId; ObservedAt = [datetimeoffset]::UtcNow.ToString('o') }
    }
    throw
} finally { if ($lease) { $lease.Dispose() } }
