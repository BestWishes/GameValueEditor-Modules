'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const vm = require('node:vm');
const { buildMaterialsRequest } = require('../src/all-materials-controller.cjs');
const { createMaterialsRuntime } = require('../src/all-materials-runtime.cjs');
const config = { processId: 17, executablePath: 'C:\\original\\game.exe', archivePath: 'C:\\original\\resources\\app.asar', profilePath: 'C:\\original\\profile' };
function fixture() {
  const state = { version: 38, rewardClockId: 'private-clock', materials: {}, tickets: 2, abyss: { tickets: 2 },
    inventory: [{ uid: 7 }], owned: [1], characters: { hero1: {} }, offline: { lastActiveAt: 10 }, luckyWheel: { pending: null } };
  for (const item of createMaterialsRuntime()('catalog')) if (!['recruitTickets', 'abyssTickets'].includes(item.id)) state.materials[item.id] = 2;
  const storage = new Map([['zseb-expedition-v1', JSON.stringify(state)]]);
  let saves = 0, flushes = 0, dispatches = 0;
  const game = { engine: { state, mode: 'guardian', phase: 'idle', serialize() { return JSON.stringify(this.state); } },
    getUi: () => ({ entered: true, gameMode: 'expedition' }), platform: { read: () => storage.get('zseb-expedition-v1'),
      save: raw => { saves++; storage.set('zseb-expedition-v1', raw); return true; } } };
  const renderer = vm.createContext({ window: { expedition: game }, localStorage: {
    get length() { return storage.size; }, key: i => [...storage.keys()][i], getItem: key => storage.get(key) ?? null } });
  const preferences = { contextIsolation: true, nodeIntegration: false };
  const contents = { getURL: () => 'expedition://game/index.html', getLastWebPreferences: () => preferences,
    session: { flushStorageData() { flushes++; } },
    async executeJavaScriptInIsolatedWorld(world, scripts, gesture) {
      assert.equal(world, 999); assert.equal(gesture, false); dispatches++;
      // Real game changes naturally across event-loop dispatches.
      state.offline.lastActiveAt++;
      return vm.runInContext(scripts[0].code, renderer);
    } };
  const electron = { app: { getPath: () => config.profilePath, getAppPath: () => config.archivePath },
    BrowserWindow: { getAllWindows: () => [{ isDestroyed: () => false, webContents: contents }] } };
  const process = { pid: config.processId, execPath: config.executablePath, arch: 'x64', versions: { electron: '44.2.0', node: '24.20.0' },
    mainModule: { require: name => { if (name === 'electron') return electron; if (name === 'node:crypto') return crypto; throw Error('unexpected import'); } } };
  const main = vm.createContext({ process });
  const run = (operation, input = {}) => vm.runInContext(buildMaterialsRequest(config, operation, input).params.expression, main);
  const setInput = read => ({ materialId: 'stones', expectedValue: 2, targetValue: 3, slot: read.material.slot,
    journeyMode: read.material.journeyMode, scopeHash: read.scopeHash });
  return { run, setInput, process, state, game, contents, preferences, electron,
    get saves() { return saves; }, get flushes() { return flushes; }, get dispatches() { return dispatches; } };
}
test('controller only compiles fixed syntax; validates bounds, operations and fields before connection', () => {
  const request = buildMaterialsRequest(config, 'list'); new vm.Script(request.params.expression);
  assert.equal(request.method, 'Runtime.evaluate'); assert.equal(request.params.awaitPromise, true);
  assert.throws(() => buildMaterialsRequest(config, 'eval'), /UNKNOWN_CONTROLLER_OPERATION/);
  assert.throws(() => buildMaterialsRequest(config, 'list', { expression: 'malicious' }), /UNKNOWN_CONTROLLER_ARGUMENT/);
  assert.throws(() => buildMaterialsRequest({ ...config, processId: 0 }, 'list'), /INVALID_CONTROLLER_CONTEXT/);
  assert.throws(() => buildMaterialsRequest(config, 'set', { materialId: 'gold' }), /INVALID_CONTROLLER_MATERIAL/);
});
test('JSON encoding preserves quotes in context paths without introducing executable source', () => {
  const request = buildMaterialsRequest({ ...config, profilePath: 'C:/x\";throw Error(\"oops\");' }, 'list');
  new vm.Script(request.params.expression);
});
test('list does not save/flush and returns only hashes plus quantity rows, never private identity/save bodies', async () => {
  const f = fixture(), read = await f.run('list');
  assert.equal(read.material.rows.length, 29); assert.equal(f.saves, 0); assert.equal(f.flushes, 0); assert.equal(f.dispatches, 1);
  assert.match(read.scopeHash, /^[a-f0-9]{64}$/);
  assert.doesNotMatch(JSON.stringify(read), /private-clock|beforeSave|storedBefore|afterSave|"anchor"|"inventory"/);
});
test('fresh synchronous set preserves natural cross-dispatch changes; logs keep hashes only', async () => {
  const f = fixture(), read = await f.run('list');
  const before = f.state.offline.lastActiveAt, result = await f.run('set', f.setInput(read));
  assert.equal(result.material.status, 'storage-verified'); assert.equal(f.saves, 1); assert.equal(f.flushes, 1);
  assert.equal(f.state.offline.lastActiveAt, before + 2); assert.equal(f.dispatches, 3);
  assert.match(result.beforeHash, /^[a-f0-9]{64}$/); assert.match(result.afterHash, /^[a-f0-9]{64}$/);
  assert.doesNotMatch(JSON.stringify(result), /beforeSave|afterSave|private-clock|"anchor"|"inventory"/);
});
test('same target does not save or flush even through real controller', async () => {
  const f = fixture(), read = await f.run('list');
  const result = await f.run('set', { ...f.setInput(read), targetValue: 2 });
  assert.equal(result.material.status, 'unchanged'); assert.equal(f.saves, 0); assert.equal(f.flushes, 0);
});
for (const activity of ['guardian', 'journey', 'abyss', 'cow', 'mine', 'flame', 'training', 'terror', 'future-activity']) {
  test(`${activity}: full assembled request supports shared materials and same-value no-op`, async () => {
    const f = fixture(); f.game.engine.mode = activity;
    const read = await f.run('list');
    assert.equal(read.material.journeyMode, activity); assert.ok(read.material.rows.every(row => row.canWrite));
    const noOp = await f.run('set', { ...f.setInput(read), targetValue: 2 });
    assert.equal(noOp.material.status, 'unchanged'); assert.equal(f.saves, 0); assert.equal(f.flushes, 0);
    const result = await f.run('set', f.setInput(read));
    assert.equal(result.material.status, 'storage-verified'); assert.equal(result.material.journeyMode, activity);
    assert.equal(f.saves, 1); assert.equal(f.flushes, 1);
  });
}
test('opaque activity is bounded data, not script or activity allowlist; mismatched receipt never writes', async () => {
  const f = fixture(), read = await f.run('list'), input = f.setInput(read);
  for (const journeyMode of ['', null, undefined, 3, {}, 'x'.repeat(129)])
    assert.throws(() => f.run('set', { ...input, journeyMode }), /INVALID_CONTROLLER_SCOPE/);
  f.game.engine.mode = 'journey';
  await assert.rejects(f.run('set', input), /READ_RECEIPT_SCOPE_CHANGED/); assert.equal(f.saves, 0);
  const fresh = await f.run('list'); assert.equal((await f.run('set', f.setInput(fresh))).material.status, 'storage-verified');
});
test('wrong identity receipt, instance/build/profile/world contract prevent writes', async () => {
  for (const mutate of [
    f => { f.state.rewardClockId = 'other'; f.game.platform.read = () => JSON.stringify(f.state); },
    f => { f.process.pid++; }, f => { delete f.process.versions.electron; },
    f => { f.electron.app.getPath = () => 'other'; },
    f => { f.preferences.contextIsolation = false; },
    f => { f.electron.app.getAppPath = () => 'other'; },
  ]) {
    const f = fixture(), read = await f.run('list'); mutate(f);
    await assert.rejects(f.run('set', f.setInput(read))); assert.equal(f.saves, 0);
  }
});
test('scope hash malformed or wrong is rejected, not patched or retried', async () => {
  const f = fixture(), read = await f.run('list');
  assert.throws(() => f.run('set', { ...f.setInput(read), scopeHash: 'bad' }), /INVALID_CONTROLLER_SCOPE/);
  await assert.rejects(f.run('set', { ...f.setInput(read), scopeHash: 'a'.repeat(64) }), /READ_RECEIPT_SCOPE_CHANGED/);
  assert.equal(f.saves, 0);
});
test('runtime minor updates do not block material read or same-value no-op', async () => {
  const f = fixture(); f.process.versions = { electron: '45.0.1', node: '25.1.2' };
  const read = await f.run('list');
  const result = await f.run('set', { ...f.setInput(read), targetValue: 2 });
  assert.equal(result.material.status, 'unchanged'); assert.equal(f.saves, 0); assert.equal(f.flushes, 0);
});
test('flush error is not save success and uncertain save is never auto-retried or flushed', async () => {
  const f = fixture(), read = await f.run('list'); f.contents.session.flushStorageData = () => { throw Error('secret path'); };
  const result = await f.run('set', f.setInput(read)); assert.equal(result.flushRequested, false); assert.equal(result.flushError, 'FLUSH_REQUEST_FAILED');
  assert.equal(f.saves, 1);
  const g = fixture(), read2 = await g.run('list'); g.game.platform.save = () => false;
  const uncertain = await g.run('set', g.setInput(read2)); assert.equal(uncertain.material.status, 'uncertain-after-save'); assert.equal(g.flushes, 0);
});
