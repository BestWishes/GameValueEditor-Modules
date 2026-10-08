'use strict';
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const assert = require('node:assert/strict');
const { execFileSync } = require('node:child_process');
const driverPath = path.join(__dirname, '../tools/Invoke-MaterialQuantity.ps1');
const source = fs.readFileSync(driverPath, 'utf8');
test('offline catalog and ValidateOnly execute without games, network or claims', () => {
  const catalog = JSON.parse(execFileSync('pwsh', ['-NoProfile', '-File', driverPath, '-Operation', 'Catalog'], { encoding: 'utf8', timeout: 10000 }));
  assert.equal(catalog.length, 29);
  const dry = JSON.parse(execFileSync('pwsh', ['-NoProfile', '-File', driverPath, '-Operation', 'List', '-ValidateOnly'], { encoding: 'utf8', timeout: 10000 }));
  assert.equal(dry.SendsRequest, false); assert.equal(dry.CreatesClaim, false);
  assert.equal(dry.Request.method, 'Runtime.evaluate');
});
test('session guards require current launch instance, full build, original launcher and loopback owner', () => {
  for (const marker of ['CreationDate.ToUniversalTime().Ticks', 'ExecutableSha256', 'PackageSha256', 'Get-FileHash',
    '$launcher.ExecutablePath', "'127.0.0.1'", 'OwningProcess', 'StartTicks', 'ScopeHash', 'ReadAt', 'ReceiptPath']) assert.ok(source.includes(marker));
  assert.doesNotMatch(source, /_debugProcess\(|inspector\.close\(|Debugger\.enable|Stop-Process|Start-Process|Copy-Item|Remove-Item/);
});
test('per-session file lease, once-only request and durable pending precede the only WS send', () => {
  assert.match(source, /\[IO.FileShare\]::None/); assert.match(source, /\[IO.FileMode\]::CreateNew/);
  assert.ok(source.indexOf('$claim.Flush($true)') < source.indexOf('$socket.SendAsync'));
  assert.ok(source.indexOf("Phase = 'pending'") < source.indexOf('$socket.ConnectAsync'));
  assert.equal((source.match(/\.SendAsync\(/g) || []).length, 1);
  assert.match(source, /if \(\$last.Phase -ne 'confirmed'\)/); assert.match(source, /Phase = 'uncertain'/);
});
test('only full confirmed result unblocks further writes, readonly cannot clear an uncertainty gate', () => {
  for (const marker of ["status -eq 'storage-verified'", 'onlySerializedMaterialChanged -eq $true', 'flushRequested -eq $true',
    'materialId -ceq $MaterialId', 'value -eq $TargetValue']) assert.ok(source.includes(marker));
  assert.equal((source.match(/Phase = 'confirmed'/g) || []).length, 1);
  assert.match(source, /if \(\$Operation -eq 'Set'\) \{[\s\S]*Append-Journal @\{ Phase = 'confirmed'/);
  assert.match(source, /finally \{ if \(\$lease\) \{ \$lease.Dispose\(\) \} \}/);
});
test('real target is explicit, amounts are never blanket-set or silently defaulted', () => {
  assert.match(source, /PSBoundParameters.ContainsKey\('TargetValue'\)/);
  assert.match(source, /Where-Object id -CEQ \$MaterialId/);
  assert.doesNotMatch(source, /WriteAllText.*beforeSave|WriteAllBytes.*save|\.before-save\.json/);
});
test('read-only failures report a sanitized guard code without stack or paths', () => {
  assert.match(source, /材料只读检查未完成（\$guardCode）/);
  assert.match(source, /RUNTIME_EXECUTION_UNCONFIRMED/);
  assert.doesNotMatch(source, /throw \$description/);
});
test('guard errors still attempt normal client disconnect, and success binds the exact receipt', () => {
  assert.match(source, /finally \{\s*# Graceful client disconnect/);
  assert.match(source, /previousValue -eq \$receiptRow\[0\]\.value/);
  assert.match(source, /scopeHash -ceq \$receipt.ScopeHash/);
  assert.match(source, /journeyMode -ceq \$receipt.JourneyMode/);
});
