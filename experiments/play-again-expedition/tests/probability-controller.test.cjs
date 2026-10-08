'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const vm = require('node:vm');
const { buildProbabilityRequest } = require('../src/probability-controller.cjs');
const { discoverProbabilitySites } = require('../src/probability-sites.cjs');
const config = { processId: 17, executablePath: 'C:/original/game.exe', archivePath: 'C:/original/resources/app.asar', profilePath: 'C:/original/profile' };
function fixture() {
  const state = { version: 38, rewardClockId: 'private-clock', inventory: [{ uid: 7 }], materials: {}, luckyWheel: null };
  const storage = new Map([['zseb-expedition-v1', JSON.stringify(state)]]);
  let actions = 0, saves = 0;
  const engine = { state, mode: 'guardian', phase: 'idle', rng: () => 0.5,
    serialize() { return JSON.stringify(this.state); }, spinLuckyWheel() { actions++; return this.rng(); }, victory() { actions++; return this.rng(); },
    dropBonus() { return 0.02; }, immortalDropChance() { return 0.07; } };
  const game = { engine, getUi: () => ({ entered: true, gameMode: 'expedition' }),
    platform: { read: () => storage.get('zseb-expedition-v1'), save() { saves++; throw Error('unexpected save'); } } };
  const window = { expedition: game, performance: { timeOrigin: 100 } };
  const renderer = vm.createContext({ window, localStorage: { get length() { return storage.size; },
    key: i => [...storage.keys()][i], getItem: key => storage.get(key) ?? null } });
  const preferences = { contextIsolation: true, nodeIntegration: false };
  const contents = { getURL: () => 'expedition://game/index.html', getLastWebPreferences: () => preferences,
    async executeJavaScriptInIsolatedWorld(world, scripts, gesture) {
      assert.equal(world, 999); assert.equal(gesture, false); return vm.runInContext(scripts[0].code, renderer);
    } };
  const electron = { app: { getPath: () => config.profilePath, getAppPath: () => config.archivePath },
    BrowserWindow: { getAllWindows: () => [{ isDestroyed: () => false, webContents: contents }] } };
  const process = { pid: 17, execPath: config.executablePath, arch: 'x64', versions: { electron: '44.2.0', node: '24.20.0' },
    mainModule: { require: name => name === 'electron' ? electron : name === 'node:crypto' ? crypto : assert.fail('unexpected import') } };
  const main = vm.createContext({ process });
  // This fixture has a hand-written engine, not the native Engine constructor.
  // Stub discovery only here; native current-build consumers are tested separately.
  const run = (op, input = {}, namespace) => vm.runInContext(buildProbabilityRequest(config, op, input, namespace).params.expression
    .replace(JSON.stringify(discoverProbabilitySites.toString()).slice(1, -1), 'function(){ return null; }'), main);
  const input = read => ({ profile: { drops: { guardianTicketPercent: 100 } }, scopeHash: read.scopeHash, revision: read.probability.revision });
  return { run, input, state, engine, game, window, renderer, contents, preferences, electron, process, storage,
    get actions() { return actions; }, get saves() { return saves; } };
}
test('only fixed probability operations and JSON fields can be compiled', () => {
  const request = buildProbabilityRequest(config, 'inspect'); new vm.Script(request.params.expression);
  assert.equal(request.id, 402); assert.equal(request.method, 'Runtime.evaluate');
  assert.throws(() => buildProbabilityRequest(config, 'spin'), /UNKNOWN/);
  assert.throws(() => buildProbabilityRequest(config, 'inspect', { expression: 'code' }), /UNKNOWN/);
  assert.throws(() => buildProbabilityRequest(config, 'configure', { scopeHash: 'a'.repeat(64), revision: 0, profile: { drops: { gold: 100 } } }), /UNKNOWN/);
  assert.throws(() => buildProbabilityRequest({ ...config, processId: 0 }, 'inspect'), /CONTEXT/);
  new vm.Script(buildProbabilityRequest({ ...config, archivePath: 'C:/x";throw Error("no");' }, 'inspect').params.expression);
});
test('inspect caches only module identity, no hooks/actions/save and no private identity leaves renderer', async () => {
  const f = fixture(), original = f.engine.victory, read = await f.run('inspect');
  assert.equal(f.engine.victory, original); assert.equal(f.actions, 0); assert.equal(f.saves, 0);
  assert.equal(read.probability.report.installed, false); assert.equal(read.probability.readOnly, true);
  assert.match(read.scopeHash, /^[a-f0-9]{64}$/); assert.doesNotMatch(JSON.stringify(read), /private-clock|"inventory"|"anchor"/);
  assert.equal((await f.run('inspect')).scopeHash, read.scopeHash);
});
test('configure replaces settings and changes only per-engine methods; reset restores native descriptors', async () => {
  const f = fixture(), snapshot = JSON.stringify(f.state), original = f.engine.victory, read = await f.run('inspect');
  const result = await f.run('configure', f.input(read));
  assert.equal(result.probability.report.installed, true); assert.equal(result.probability.revision, 1);
  assert.equal(f.actions, 0); assert.equal(f.saves, 0); assert.equal(JSON.stringify(f.state), snapshot);
  const after = await f.run('inspect');
  const reset = await f.run('reset', { scopeHash: after.scopeHash, revision: 1 });
  assert.equal(reset.probability.outcome.restored, true); assert.equal(f.engine.victory, original); assert.equal(reset.probability.revision, 2);
  assert.equal((await f.run('inspect')).probability.revision, 2);
});
test('stale revisions and receipts are rejected without retry or game action', async () => {
  const f = fixture(), read = await f.run('inspect'); await f.run('configure', f.input(read));
  await assert.rejects(f.run('configure', f.input(read)), /READ_RECEIPT_SCOPE_CHANGED/);
  await assert.rejects(f.run('reset', { scopeHash: 'b'.repeat(64), revision: 1 }), /READ_RECEIPT_SCOPE_CHANGED/);
  assert.equal(f.actions, 0); assert.equal(f.saves, 0);
});

test('fixed wheel/drops namespaces coexist and share stale receipt protection', async () => {
  const f = fixture(), read = await f.run('inspect');
  await f.run('configure', f.input(read), 'drops');
  await assert.rejects(f.run('configure', { ...f.input(read), profile: { wheelBonus: { doublePercent: 25, marqueePercent: 0 } } }, 'wheel'), /READ_RECEIPT_SCOPE_CHANGED/);
  const fresh = await f.run('inspect');
  const wheel = await f.run('configure', { scopeHash: fresh.scopeHash, revision: fresh.probability.revision,
    profile: { wheelBonus: { doublePercent: 25, marqueePercent: 0 } } }, 'wheel');
  assert.equal(wheel.probability.report.namespaces.drops.enabled, true);
  const reset = await f.run('reset', { scopeHash: wheel.scopeHash, revision: wheel.probability.revision }, 'wheel');
  assert.equal(reset.probability.report.namespaces.wheel.enabled, false);
  assert.equal(reset.probability.report.namespaces.drops.enabled, true);
  assert.equal(reset.probability.report.installed, true);
  const last = await f.run('reset', { scopeHash: reset.scopeHash, revision: reset.probability.revision }, 'drops');
  assert.equal(last.probability.report.installed, false);
  assert.equal(f.actions, 0); assert.equal(f.saves, 0);
});

test('namespace is compiled, never accepted as arbitrary external argument', () => {
  const input = { scopeHash: 'a'.repeat(64), revision: 0, profile: { drops: { guardianTicketPercent: 100 } } };
  assert.throws(() => buildProbabilityRequest(config, 'configure', input, 'wheel'), /NAMESPACE_MISMATCH/);
  assert.throws(() => buildProbabilityRequest(config, 'reset', { scopeHash: input.scopeHash, revision: 0 }, 'other'), /NAMESPACE/);
  assert.throws(() => buildProbabilityRequest(config, 'configure', { ...input, namespace: 'drops' }), /ARGUMENT/);
  assert.match(buildProbabilityRequest(config, 'configure', input, 'drops').params.expression, /const namespace = "drops"/);
});
test('legitimate new loot does not invalidate the probability session', async () => {
  const f = fixture(), read = await f.run('inspect'); await f.run('configure', f.input(read));
  f.state.inventory.unshift({ uid: 8 }); f.state.materials.stones = 5;
  f.storage.set('zseb-expedition-v1', JSON.stringify(f.state));
  const next = await f.run('inspect'); assert.equal(next.scopeHash, read.scopeHash); assert.equal(next.probability.report.ownerCurrent, true);
});
test('engine replacement within same renderer invalidates old receipt even for same save', async () => {
  const f = fixture(), read = await f.run('inspect'); f.game.engine = { ...f.engine, state: JSON.parse(JSON.stringify(f.state)) };
  await assert.rejects(f.run('configure', f.input(read)), /READ_RECEIPT_SCOPE_CHANGED/);
  assert.equal(f.actions, 0); assert.equal(f.saves, 0);
});
for (const [label, change] of [
  ['process', f => f.process.pid++], ['runtime', f => { delete f.process.versions.electron; }],
  ['profile', f => { f.electron.app.getPath = () => 'other'; }], ['archive', f => { f.electron.app.getAppPath = () => 'other'; }],
  ['isolation', f => { f.preferences.contextIsolation = false; }], ['game mode', f => { f.game.getUi = () => ({ entered: true, gameMode: 'threeKingdoms' }); }],
  ['main menu', f => { f.game.getUi = () => ({ entered: false }); }], ['duplicate slots', f => { f.storage.set('zseb-expedition-v1-slot-1', f.storage.get('zseb-expedition-v1')); }],
  ['clock', f => { f.state.rewardClockId = 'different'; }], ['battle', f => { f.engine.phase = 'fighting'; }],
  ['pending', f => { f.state.luckyWheel = { pending: {} }; }], ['method', f => { f.engine.victory = () => 1; }],
  ['rng', f => { f.engine.rng = () => 0.9; }], ['renderer reload', f => { f.window.performance.timeOrigin++; }],
]) test('guard blocks configure after ' + label + ' change', async () => {
  const f = fixture(), read = await f.run('inspect'); change(f);
  await assert.rejects(f.run('configure', f.input(read))); assert.equal(f.actions, 0); assert.equal(f.saves, 0);
});
test('third-party hook conflicts are reported and not overwritten on reset', async () => {
  const f = fixture(), read = await f.run('inspect'); await f.run('configure', f.input(read));
  const other = () => 123; f.engine.victory = other;
  const next = await f.run('inspect'), result = await f.run('reset', { scopeHash: next.scopeHash, revision: next.probability.revision });
  assert.equal(result.probability.outcome.restored, false); assert.equal(f.engine.victory, other);
});
test('new controller source can upgrade only a previously uninstalled metadata cache', async () => {
  const f = fixture(), old = await f.run('inspect'), key = Symbol.for('GameValueEditor.PlayAgainExpedition.ProbabilitySession.v1');
  f.window[key].implementationVersion = 1;
  const next = await f.run('inspect'); assert.notEqual(next.scopeHash, old.scopeHash); assert.equal(next.probability.report.installed, false);
  await assert.rejects(f.run('configure', f.input(old)), /READ_RECEIPT_SCOPE_CHANGED/);
  await f.run('configure', f.input(next)); f.window[key].implementationVersion = 1;
  await assert.rejects(f.run('inspect'), /IMPLEMENTATION_CHANGED_RESTART_REQUIRED/);
});
test('Probe is a fixed copy-only operation and refuses an unknown constructor', async () => {
  const f = fixture(), before = JSON.stringify(f.state), read = await f.run('inspect');
  await assert.rejects(f.run('probe'), /CONSTRUCTOR_NOT_VERIFIED/);
  assert.equal(JSON.stringify(f.state), before); assert.equal(f.actions, 0); assert.equal(f.saves, 0);
  assert.equal((await f.run('inspect')).probability.report.installed, false);
  await f.run('configure', f.input(read)); await assert.rejects(f.run('probe'), /PROBE_NOT_SAFE/);
});
test('copy-only probe rejects arbitrary test source/profile inputs', () => {
  assert.throws(() => buildProbabilityRequest(config, 'probe', { expression: 'code' }), /UNKNOWN/);
  assert.throws(() => buildProbabilityRequest(config, 'probe', { profile: { drops: { guardianTicketPercent: 100 } } }), /UNKNOWN/);
});
