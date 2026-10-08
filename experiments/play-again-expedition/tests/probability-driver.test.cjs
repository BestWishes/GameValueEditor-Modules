'use strict';
const fs = require('node:fs'), path = require('node:path'), os = require('node:os');
const test = require('node:test'), assert = require('node:assert/strict');
const { execFileSync } = require('node:child_process');
const driver = path.join(__dirname, '../tools/Invoke-Probability.ps1'), source = fs.readFileSync(driver, 'utf8');
const run = args => JSON.parse(execFileSync('pwsh', ['-NoProfile', '-File', driver, ...args], { encoding: 'utf8', timeout: 10000 }));
test('Inspect ValidateOnly compiles without process/network access or claim', () => {
  const dry = run(['-ValidateOnly']); assert.equal(dry.SendsRequest, false); assert.equal(dry.CreatesClaim, false); assert.equal(dry.Request.id, 402);
});
test('Configure and Reset ValidateOnly compile only a fresh exact-build receipt', () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'gve-probability-test-')), receipt = path.join(directory, 'receipt.json');
  try {
    fs.writeFileSync(receipt, JSON.stringify({ Schema: 'probability-1', ReadAt: new Date().toISOString(), Revision: 0, ScopeHash: 'a'.repeat(64),
      ExecutableSha256: 'A79F66A6F236B237E25C00DD9F8BA763F9BAD13E16CD3269DE2348EA7FFEF396',
      PackageSha256: '5A4F4E414BD06FC248CFE2AD30E7D9EE3B110968DB888FDB733CD57F78A6B294' }));
    for (const op of ['Configure', 'Reset']) {
      const args = ['-Operation', op, '-ReadReceiptPath', receipt, '-ValidateOnly'];
      if (op === 'Configure') args.push('-ProfileJson', '{"drops":{"guardianTicketPercent":25}}');
      const dry = run(args); assert.equal(dry.SendsRequest, false); assert.equal(dry.CreatesClaim, false);
    }
  } finally { fs.rmSync(directory, { recursive: true }); }
});
test('driver does not control game, export/restore saves or forcibly open/close debug service', () => {
  assert.doesNotMatch(source, /_debugProcess\(|inspector\.close\(|Debugger\.enable|Stop-Process|Start-Process|Copy-Item|Remove-Item|WriteAllText/);
  for (const guard of ['CreationDate.ToUniversalTime().Ticks', 'Get-FileHash', '$launcher.ExecutablePath', 'OwningProcess', "'127.0.0.1'", 'StartTicks', 'ReadAt', 'Revision']) assert.ok(source.includes(guard));
});
test('only one send; durable exclusive no-replay and pending gate precede connection', () => {
  assert.equal((source.match(/\.SendAsync\(/g) || []).length, 1);
  assert.match(source, /\[IO.FileShare\]::None/); assert.match(source, /\[IO.FileMode\]::CreateNew/);
  assert.ok(source.indexOf('$claim.Flush($true)') < source.indexOf('$socket.ConnectAsync'));
  assert.ok(source.indexOf("Phase = 'pending'") < source.indexOf('$socket.ConnectAsync'));
  assert.match(source, /\$last.Phase -ne 'confirmed'/); assert.match(source, /Phase = 'uncertain'/);
  assert.match(source, /if \(\$mutating\) \{[\s\S]*Phase = 'confirmed'/);
});
test('JSON validation precedes process/network; management success requires exact receipt and no save/action', () => {
  assert.ok(source.indexOf('$validationRequest = Build-Request') < source.indexOf('Get-CimInstance'));
  for (const marker of ['nativeSaveCalled -ne $false', 'gameActionCalled -ne $false', 'scopeHash -cne $receipt.ScopeHash',
    'outcome.restored -ne $true', 'Normalize-Data $actual', "'RUNTIME_EXECUTION_UNCONFIRMED'", '$socket.CloseAsync']) assert.ok(source.includes(marker));
});
