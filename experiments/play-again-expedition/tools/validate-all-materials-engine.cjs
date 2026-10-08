'use strict';
// Exact-build pure Engine only, headless and with fake storage. No live attach,
// original profile, main/preload, network, game UI, backup or disk save writes.
const fs = require('node:fs');
const originalFs = require('original-fs');
const path = require('node:path');
const vm = require('node:vm');
const crypto = require('node:crypto');
const assert = require('node:assert/strict');
const { createMaterialsRuntime } = require('../src/all-materials-runtime.cjs');
const [archive, copiedSave] = process.argv.slice(2);
assert.equal(process.env.ELECTRON_RUN_AS_NODE, '1');
assert.equal(process.versions.electron, '44.2.0'); assert.equal(process.versions.node, '24.20.0'); assert.equal(process.arch, 'x64');
assert.equal(crypto.createHash('sha256').update(originalFs.readFileSync(archive)).digest('hex'), '5a4f4e414bd06fc248cfE2ad30e7d9ee3b110968db888fdb733cd57f78a6b294'.toLowerCase());
assert.equal(path.resolve(copiedSave).toLowerCase(), path.resolve(__dirname,
  '../../../artifacts/play-again-expedition/20261007-213142-offline-recovery/restore-protection-scrolls-2.json').toLowerCase());
Object.defineProperty(process, 'type', { value: 'browser', configurable: true });
const { Engine } = require(path.join(archive, 'runtime/src/engine.js'));
const fixtureRaw = fs.readFileSync(copiedSave, 'utf8');
const fixtureHash = crypto.createHash('sha256').update(fixtureRaw).digest('hex');
const engine = new Engine(fixtureRaw, () => 0.5);
assert.equal(engine.mode, 'guardian'); assert.equal(engine.phase, 'idle');
const slot = 'zseb-expedition-v1', storage = new Map([[slot, engine.serialize()], [slot + '-slot-1', '{"other":"preserved"}']]);
let saves = 0;
const game = { engine, getUi: () => ({ entered: true, gameMode: 'expedition' }), platform: {
  read: () => storage.get(slot), save: raw => { saves++; storage.set(slot, raw); return true; } } };
const context = vm.createContext({ window: { expedition: game }, localStorage: {
  get length() { return storage.size; }, key: i => [...storage.keys()][i], getItem: key => storage.get(key) ?? null } });
const run = vm.runInContext(`(${createMaterialsRuntime.toString()})()`, context);
const definitions = run('catalog'); assert.equal(definitions.length, 29);
const leaf = (root, id) => id === 'recruitTickets' ? [root, 'tickets'] : id === 'abyssTickets' ? [root.abyss, 'tickets'] : [root.materials, id];
const reports = [];
const activities = ['guardian', 'journey', 'abyss', 'cow', 'mine', 'flame', 'training', 'terror'];
for (const activity of activities) {
  const materialOwner = engine.state.materials;
  if (activity === 'journey') engine.select(engine.state.selected);
  else engine.mode = activity;
  assert.equal(engine.mode, activity); assert.equal(engine.phase, 'idle');
  assert.equal(engine.state.materials, materialOwner);
  const baseline = engine.serialize(); storage.set(slot, baseline);
for (const item of definitions) {
  const source = leaf(engine.state, item.id), original = source[0][source[1]];
  assert.equal(Number.isSafeInteger(original), true, item.id);
  for (const targetValue of [0, 1, 17, item.maximum]) {
    const read = run('list'), row = read.rows.find(row => row.id === item.id);
    assert.equal(row.canWrite, true, item.id + ': ' + row.status);
    const result = run('set', { materialId: item.id, expectedValue: row.value, targetValue,
      slot, journeyMode: read.journeyMode, anchor: read.anchor });
    assert.equal(result.status, targetValue === row.value ? 'unchanged' : 'storage-verified', item.id);
    const saved = JSON.parse(storage.get(slot)), reload = new Engine(storage.get(slot), () => 0.5);
    assert.equal(leaf(reload.state, item.id)[0][leaf(reload.state, item.id)[1]], targetValue, item.id + ' reload');
    const savedLeaf = leaf(saved, item.id); savedLeaf[0][savedLeaf[1]] = original;
    assert.equal(JSON.stringify(saved), JSON.stringify(JSON.parse(baseline)), item.id + ' unrelated save data');
    const restoreRead = run('list');
    const restored = run('set', { materialId: item.id, expectedValue: targetValue, targetValue: original,
      slot, journeyMode: restoreRead.journeyMode, anchor: restoreRead.anchor });
    assert.equal(restored.status, original === targetValue ? 'unchanged' : 'storage-verified', item.id + ' restore');
    const restoredEngine = new Engine(storage.get(slot), () => 0.5);
    assert.equal(leaf(restoredEngine.state, item.id)[0][leaf(restoredEngine.state, item.id)[1]], original);
    assert.equal(engine.serialize(), baseline); assert.equal(storage.get(slot), baseline);
    assert.equal(storage.get(slot + '-slot-1'), '{"other":"preserved"}');
  }
  reports.push({ activity, id: item.id, testedTargets: [0, 1, 17, item.maximum], roundtripAndRestore: true });
}
}
assert.equal(crypto.createHash('sha256').update(fs.readFileSync(copiedSave)).digest('hex'), fixtureHash);
console.log(JSON.stringify({ exactBuildPureEngineAllMaterials: 'PASS', materialCount: definitions.length,
  activityContexts: activities, targetCases: reports.length * 4, saveCalls: saves,
  nativeStageSelectionValidated: true, allCasesRoundtripAndRestored: reports.every(row => row.roundtripAndRestore), allOtherSerializedDataPreserved: true,
  fakeStorageOnly: true, liveGameTouched: false, originalProfileOpened: false, newBackupCreated: false }, null, 2));
