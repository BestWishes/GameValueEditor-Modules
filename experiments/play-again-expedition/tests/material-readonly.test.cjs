'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const { inspectProtectionScroll } = require('../src/material-readonly.cjs');

function fixture() {
  const state = { version: 38, gold: 90, materials: { protectionScrolls: 2, stones: 17 } };
  const saved = structuredClone(state);
  const values = new Map();
  const ui = { entered: true, gameMode: 'expedition' };
  const engine = { state, mode: 'guardian', serialize() { throw Error('FORBIDDEN_SERIALIZE'); } };
  const game = { engine, getUi: () => ui, platform: { read: () => values.get('zseb-expedition-v1'), save() { throw Error('FORBIDDEN_SAVE'); } } };
  const storage = { get length() { return values.size; }, key: index => [...values.keys()][index], getItem: key => values.get(key) ?? null,
    setItem() { throw Error('FORBIDDEN_STORAGE_WRITE'); }, removeItem() { throw Error('FORBIDDEN_STORAGE_WRITE'); } };
  const context = vm.createContext({ window: { expedition: game }, localStorage: storage });
  const syncSaved = () => { values.set('zseb-expedition-v1', JSON.stringify(saved)); values.set('zseb-expedition-v1-backup', JSON.stringify(saved)); };
  syncSaved();
  const read = (expected = 2) => vm.runInContext(`'use strict'; (${inspectProtectionScroll.toString()})(${JSON.stringify(expected)})`, context);
  return { state, saved, values, ui, engine, game, storage, context, syncSaved, read };
}
test('read-only result matches the unique main key, ignoring backup; no mutations', () => {
  const f = fixture();
  Object.freeze(f.state.materials); Object.freeze(f.state); Object.freeze(f.engine);
  Object.freeze(f.game.platform); Object.freeze(f.game); Object.freeze(f.storage); Object.freeze(f.ui);
  const beforeState = JSON.stringify(f.state), beforeStorage = [...f.values];
  const beforeGlobals = vm.runInContext('Object.getOwnPropertyNames(globalThis)', f.context);
  const result = f.read();
  assert.equal(result.runtimeQuantity, 2); assert.equal(result.storedQuantity, 2);
  assert.equal(result.activeSaveKey, 'zseb-expedition-v1'); assert.equal(result.mode, 'expedition');
  assert.equal(result.journeyMode, 'guardian'); assert.equal(result.readOnly, true);
  assert.equal(JSON.stringify(f.state), beforeState); assert.deepEqual([...f.values], beforeStorage);
  assert.deepEqual(vm.runInContext('Object.getOwnPropertyNames(globalThis)', f.context), beforeGlobals);
});
test('ambiguous equal main slots and backup-only storage are rejected', () => {
  const f = fixture(); f.values.set('zseb-expedition-v1-slot-1', f.values.get('zseb-expedition-v1'));
  assert.throws(() => f.read(), /SAVE_KEY_AMBIGUOUS/);
  f.values.delete('zseb-expedition-v1-slot-1'); f.values.delete('zseb-expedition-v1');
  f.game.platform.read = () => f.values.get('zseb-expedition-v1-backup');
  assert.throws(() => f.read(), /SAVE_KEY_AMBIGUOUS/);
});
test('title, unsupported mode, storage block, format and missing material fail closed', () => {
  for (const [change, expected] of [
    [f => { f.ui.entered = false; }, /ENTER_EXISTING_SAVE/],
    [f => { f.ui.gameMode = 'unknown'; }, /MODE_NOT_SUPPORTED/],
    [f => { f.game.platform.storageBlocked = true; }, /SAVE_CONTRACT_CHANGED/],
    [f => { f.saved.version = 39; f.syncSaved(); }, /SAVE_FORMAT_CHANGED/],
    [f => { delete f.state.materials.protectionScrolls; }, /MATERIAL_CONTRACT_CHANGED/],
  ]) { const f = fixture(); change(f); assert.throws(() => f.read(), expected); }
});
test('mismatched displayed/runtime/stored counts cannot pass', () => {
  const f = fixture(); assert.throws(() => f.read(3), /QUANTITY_MISMATCH/);
  f.saved.materials.protectionScrolls = 3; f.syncSaved();
  assert.throws(() => f.read(), /QUANTITY_MISMATCH/);
});
test('changing the selected save during the read fails closed', () => {
  const f = fixture(); let reads = 0;
  f.game.platform.read = () => ++reads === 1 ? f.values.get('zseb-expedition-v1') : 'changed';
  assert.throws(() => f.read(), /SAVE_CHANGED_DURING_READ/);
});
for (const invalid of [-1, 1.5, 1_000_000_001, '2', null, true]) {
  test(`invalid displayed or runtime quantity ${JSON.stringify(invalid)}`, () => {
    const f = fixture(); assert.throws(() => f.read(invalid), /INVALID_DISPLAYED_QUANTITY/);
    f.state.materials.protectionScrolls = invalid; assert.throws(() => f.read(), /INVALID_RUNTIME_QUANTITY/);
  });
}
