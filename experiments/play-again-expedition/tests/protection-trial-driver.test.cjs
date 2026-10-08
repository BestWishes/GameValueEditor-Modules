'use strict';
const fs = require('node:fs');
const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, 'fixtures/startup-protection-trial.ps1.txt'), 'utf8');

test('a fixed stage request has an exclusive on-disk claim before any WS connection', () => {
  assert.match(source, /ValidateSet\('Apply', 'Restore'\)/);
  assert.match(source, /Stage already claimed/);
  const claim = source.indexOf('[IO.File]::Open($dispatchPath, [IO.FileMode]::CreateNew');
  assert.ok(claim > 0 && claim < source.indexOf('$socket.ConnectAsync'));
  assert.ok(source.indexOf('$claim.Flush($true)') < source.indexOf('$socket.SendAsync'));
});
test('request validation returns before claim and cannot send or mutate', () => {
  const dry = source.indexOf('if ($ValidateOnly)');
  assert.ok(dry > 0 && dry < source.indexOf('[IO.File]::Open($dispatchPath'));
  assert.match(source.slice(dry, source.indexOf('# Generated trial metadata')), /return/);
});
test('loopback ownership, session identity, exact build, profile and world are mandatory', () => {
  for (const marker of ["'127.0.0.1'", 'OwningProcess', 'CreationDate', 'Get-FileHash', 'contextContract',
    "getPath('userData')", "getAppPath()", "expedition://game/index.html", 'executeJavaScriptInIsolatedWorld(999']) assert.ok(source.includes(marker));
  assert.doesNotMatch(source, /Debugger\.enable|_debugProcess\(|inspector\.close\(|Stop-Process|forcefullyCrash|executeJavaScriptInIsolatedWorld\(0/);
});
test('restore requires confirmed apply and compares original save anchor before commit', () => {
  assert.match(source, /apply\.result\.single-dispatch-v2\.json/); assert.match(source, /No confirmed apply result/);
  assert.ok(source.indexOf('expectedAnchorHash !== anchorHash') < source.indexOf('const atomicPacket = await'));
  assert.match(source, /onlySerializedMaterialChanged/); // result returned by the fixed backend, not an arbitrary setter
});
test('inspection only returns before atomic commit and never creates claim or result files', () => {
  const inspection = source.indexOf('if ($inspectionLiteral)');
  assert.ok(inspection > 0 && inspection < source.indexOf('const atomicPacket = await'));
  assert.match(source.slice(inspection, source.indexOf("const prepared = await call('prepare', input);", inspection)), /nativeSaveCalled: false/);
  assert.match(source, /if \(-not \$InspectPreparation\) \{\s+\$claim =/);
  assert.ok(source.indexOf('if ($InspectPreparation) { $resultJson; return }') < source.indexOf('[IO.File]::Open($resultPath'));
});
test('revised trial preserves old claim and executes fresh prepare plus commit atomically', () => {
  assert.match(source, /single-dispatch-v2/); assert.match(source, /executeProtectionTrialAtomically/);
  assert.doesNotMatch(source, /await call\('commit'|Remove-Item|\.Delete\(/);
  assert.match(source, /hash\(atomicPacket\.result\.beforeSave\)/);
});
test('recording keeps only hashes and metadata, never complete save snapshots', () => {
  assert.match(source, /delete material\.afterSave/);
  assert.match(source, /beforeHash, afterHash, anchorHash/);
  assert.match(source, /NewBackupCreated = \$false/);
  assert.doesNotMatch(source, /Copy-Item|\.before-save\.json|WriteAllText\(.*beforeSave/);
  assert.equal((source.match(/\.SendAsync\(/g) || []).length, 1);
});
