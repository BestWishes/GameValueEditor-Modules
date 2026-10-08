'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const { materialTransaction } = require('../src/material-transaction.cjs');
function fixture(quantity = 20) {
  const state = { version: 38, gold: 123, inventory: [{ uid: 4, atk: 9 }], party: [1, 2], materials: { protectionScrolls: quantity, sacredProtectionScrolls: 7, stones: 30 } };
  const values = new Map([['zseb-expedition-v1', JSON.stringify(state)]]);
  const selected = { key: 'zseb-expedition-v1' };
  const storage = { get length() { return values.size; }, key: index => [...values.keys()][index], getItem: key => values.get(key) ?? null };
  let saves = 0;
  const engine = { state, mode: 'expedition', serialize() { return JSON.stringify(this.state); } };
  const ui = { entered: true, gameMode: 'expedition', slotActive: 0 };
  const game = { engine, getUi: () => ui, platform: { read() { return values.get(selected.key); }, save(raw) { saves++; values.set(selected.key, raw); return true; } } };
  const context = vm.createContext({ window: { expedition: game }, localStorage: storage });
  const run = (method, args) => vm.runInContext(`(${materialTransaction.toString()})(${JSON.stringify(method)},${JSON.stringify(args ?? null)},'test-session')`, context);
  const input = targetValue => ({ expectedInstance: run('read').instance, expectedValue: engine.state.materials.protectionScrolls, targetValue });
  return { state, engine, ui, game, values, selected, run, input, get saves() { return saves; } };
}
test('read does not serialize or save', () => {
  const f = fixture();
  f.engine.serialize = () => { throw Error('must not serialize'); };
  assert.equal(f.run('read').value, 20);
  assert.equal(f.saves, 0);
});
test('prepare then commit changes exactly one material and verifies origin storage', () => {
  const f = fixture();
  const before = JSON.stringify(f.state);
  const args = f.input(21);
  const prepared = f.run('prepare', args);
  assert.equal(f.saves, 0);
  const result = f.run('commit', { ...args, beforeSave: prepared.beforeSave });
  assert.equal(result.value, 21);
  assert.equal(result.storageVerified, true);
  assert.equal(f.saves, 1);
  f.state.materials.protectionScrolls = 20;
  assert.equal(JSON.stringify(f.state), before);
});
test('restore follows the same validated save path', () => {
  const f = fixture(21);
  const args = f.input(20);
  const prepared = f.run('prepare', args);
  assert.equal(f.run('commit', { ...args, beforeSave: prepared.beforeSave }).value, 20);
  assert.equal(JSON.parse(f.values.get('zseb-expedition-v1')).materials.protectionScrolls, 20);
});
test('same-value requests neither serialize nor save', () => {
  const f = fixture();
  const args = f.input(20);
  f.engine.serialize = () => { throw Error('must not serialize'); };
  assert.equal(f.run('prepare', args).unchanged, true);
  assert.equal(f.saves, 0);
});
test('unknown operation and blocked storage are refused even for same value', () => {
  const f = fixture();
  assert.throws(() => f.run('arbitrary', f.input(20)), /UNKNOWN_OPERATION/);
  f.game.platform.storageBlocked = true;
  assert.throws(() => f.run('prepare', f.input(20)), /SAVE_CONTRACT_CHANGED/);
  assert.equal(f.saves, 0);
});
for (const value of [-1, 1.5, 1000000001, '1', null, true]) test(`invalid target ${JSON.stringify(value)} is refused`, () => {
  const f = fixture();
  assert.throws(() => f.run('prepare', f.input(value)), /INVALID_TARGET_VALUE/);
  assert.equal(f.state.materials.protectionScrolls, 20);
  assert.equal(f.saves, 0);
});
for (const value of [0, 1000000000]) test(`legal boundary ${value}`, () => {
  const f = fixture();
  const args = f.input(value);
  const prepared = f.run('prepare', args);
  assert.equal(f.run('commit', { ...args, beforeSave: prepared.beforeSave }).value, value);
});
test('a replaced state or changed slot invalidates the old request', () => {
  for (const change of [f => { f.engine.state = structuredClone(f.state); }, f => { f.values.set('zseb-expedition-v1-slot-2', JSON.stringify({ ...f.state, gold: 999 })); f.selected.key = 'zseb-expedition-v1-slot-2'; }]) {
    const f = fixture();
    const args = f.input(21);
    change(f);
    assert.throws(() => f.run('prepare', args), /SAVE_INSTANCE_CHANGED/);
    assert.equal(f.saves, 0);
  }
});
test('game mode is not the engine journey mode; storage key binds the slot', () => {
  const f = fixture();
  f.engine.mode = 'guardian';
  const result = f.run('read');
  assert.equal(result.mode, 'expedition');
  assert.equal(result.slot, 'zseb-expedition-v1');
});
test('ambiguous equal save slots and untested modes refuse writing', () => {
  const f = fixture();
  f.values.set('zseb-expedition-v1-slot-1', f.values.get('zseb-expedition-v1'));
  assert.throws(() => f.run('read'), /SAVE_KEY_AMBIGUOUS/);
  f.values.delete('zseb-expedition-v1-slot-1');
  f.ui.gameMode = 'unknown';
  assert.throws(() => f.run('read'), /MODE_NOT_SUPPORTED/);
  assert.equal(f.saves, 0);
});
test('another save slot is not allowed to be overwritten by the save API', () => {
  const f = fixture();
  f.values.set('zseb-expedition-v1-slot-1', JSON.stringify({ ...f.state, gold: 999 }));
  const originalSave = f.game.platform.save;
  f.game.platform.save = raw => { originalSave(raw); f.values.set('zseb-expedition-v1-slot-1', raw); return true; };
  const args = f.input(21);
  const prepared = f.run('prepare', args);
  assert.throws(() => f.run('commit', { ...args, beforeSave: prepared.beforeSave }), /PERSISTENCE_UNCONFIRMED/);
});
test('changed value and changed whole snapshot are refused', () => {
  const f = fixture();
  const args = f.input(21);
  const prepared = f.run('prepare', args);
  f.state.gold++;
  assert.throws(() => f.run('commit', { ...args, beforeSave: prepared.beforeSave }), /SNAPSHOT_CHANGED/);
  f.state.materials.protectionScrolls++;
  assert.throws(() => f.run('prepare', args), /VALUE_CHANGED/);
  assert.equal(f.saves, 0);
});
test('wrong format and absent save are refused', () => {
  const f = fixture();
  f.state.version = 39;
  assert.throws(() => f.run('prepare', f.input(21)), /SAVE_CONTRACT_CHANGED/);
  f.ui.entered = false;
  assert.throws(() => f.run('read'), /ENTER_EXISTING_SAVE/);
});
test('save failure restores the live original', () => {
  const f = fixture();
  f.game.platform.save = () => false;
  const args = f.input(21);
  const prepared = f.run('prepare', args);
  assert.throws(() => f.run('commit', { ...args, beforeSave: prepared.beforeSave }), /SAVE_FAILED_LIVE_RESTORED/);
  assert.equal(f.state.materials.protectionScrolls, 20);
});
test('unrelated serialization side effect is detected before save', () => {
  const f = fixture();
  const args = f.input(21);
  const prepared = f.run('prepare', args);
  f.engine.serialize = function() { const copy = structuredClone(this.state); if (copy.materials.protectionScrolls === 21) copy.inventory = []; return JSON.stringify(copy); };
  assert.throws(() => f.run('commit', { ...args, beforeSave: prepared.beforeSave }), /UNRELATED_FIELDS_CHANGED/);
  assert.equal(f.state.materials.protectionScrolls, 20);
  assert.equal(f.saves, 0);
});
test('object-only readback cannot pass persistence verification', () => {
  const f = fixture();
  f.game.platform.save = () => true;
  const args = f.input(21);
  const prepared = f.run('prepare', args);
  assert.throws(() => f.run('commit', { ...args, beforeSave: prepared.beforeSave }), /PERSISTENCE_UNCONFIRMED/);
});
