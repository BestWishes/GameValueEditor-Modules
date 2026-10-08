'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const { protectionScrollTrial, executeProtectionTrialAtomically } = require('../src/protection-scroll-trial.cjs');

function fixture(quantity = 2) {
  const state = { version: 38, rewardClockId: 'trial-clock', gold: 80, inventory: [{ uid: 7, atk: 99 }],
    characters: { hero1: { level: 5 } }, owned: [1], materials: { protectionScrolls: quantity, stones: 6 } };
  const values = new Map([['zseb-expedition-v1', JSON.stringify(state)], ['zseb-expedition-v1-backup', JSON.stringify(state)]]);
  const engine = { state, mode: 'guardian', serialize() { return JSON.stringify(this.state); } };
  const ui = { entered: true, gameMode: 'expedition' };
  let saves = 0;
  const game = { engine, getUi: () => ui, platform: { read: () => values.get('zseb-expedition-v1'),
    save(raw) { saves++; values.set('zseb-expedition-v1', raw); return true; } } };
  const storage = { get length() { return values.size; }, key: index => [...values.keys()][index], getItem: key => values.get(key) ?? null };
  const context = vm.createContext({ window: { expedition: game }, localStorage: storage });
  const input = { expectedValue: quantity, targetValue: quantity === 2 ? 3 : 2, slot: 'zseb-expedition-v1', journeyMode: 'guardian' };
  const run = (stage, args = input) => vm.runInContext(`'use strict'; (${protectionScrollTrial.toString()})(${JSON.stringify(stage)},${JSON.stringify(args)})`, context);
  const prepare = () => run('prepare');
  const commit = (prepared = prepare()) => run('commit', { ...input, ...prepared, expectedValue: input.expectedValue });
  const atomic = anchor => vm.runInContext(`'use strict'; (${executeProtectionTrialAtomically.toString()})(${protectionScrollTrial.toString()},${JSON.stringify(input)},${JSON.stringify(anchor)})`, context);
  return { state, values, engine, ui, game, context, input, run, prepare, commit, atomic, get saves() { return saves; } };
}

test('prepare is read-only, has no global cache and does not call save', () => {
  const f = fixture(); const globals = vm.runInContext('Object.getOwnPropertyNames(globalThis)', f.context);
  Object.freeze(f.state); Object.freeze(f.engine); Object.freeze(f.game.platform); Object.freeze(f.game);
  const before = [...f.values]; const result = f.prepare();
  assert.equal(result.value, 2); assert.equal(result.nativeSaveCalled, false); assert.equal(f.saves, 0);
  assert.equal(f.state.materials.protectionScrolls, 2);
  assert.deepEqual([...f.values], before); assert.deepEqual(vm.runInContext('Object.getOwnPropertyNames(globalThis)', f.context), globals);
});
for (const quantity of [2, 3]) test(`single-field ${quantity} -> ${quantity === 2 ? 3 : 2} verifies native storage`, () => {
  const f = fixture(quantity); f.values.set('zseb-expedition-v1-slot-2', '{"other":"save"}');
  const before = JSON.stringify(f.state); const other = f.values.get('zseb-expedition-v1-slot-2');
  const result = f.commit();
  assert.equal(result.status, 'storage-verified'); assert.equal(result.value, f.input.targetValue);
  assert.equal(result.storageVerified, true); assert.equal(f.saves, 1);
  assert.equal(f.values.get('zseb-expedition-v1-slot-2'), other);
  const after = JSON.parse(result.afterSave); after.materials.protectionScrolls = quantity;
  assert.equal(JSON.stringify(after), before);
});
test('unknown stages, arbitrary counts and changed scope are refused', () => {
  for (const [stage, input, error] of [
    ['other', {}, /UNKNOWN_TRIAL_STAGE/], ['prepare', {}, /TRIAL_VALUES_NOT_ALLOWED/],
    ['prepare', { expectedValue: 2, targetValue: 100 }, /TRIAL_VALUES_NOT_ALLOWED/],
    ['prepare', { expectedValue: 2, targetValue: 3, slot: 'wrong', journeyMode: 'guardian' }, /TRIAL_SCOPE_CHANGED/],
  ]) { const f = fixture(); assert.throws(() => f.run(stage, input), error); assert.equal(f.saves, 0); }
});
test('accessor, frozen, absent, unexpected-value materials cannot be written', () => {
  for (const change of [
    f => Object.freeze(f.state.materials),
    f => Object.defineProperty(f.state.materials, 'protectionScrolls', { get: () => 2 }),
    f => { delete f.state.materials.protectionScrolls; },
    f => { f.state.materials.protectionScrolls = 4; },
  ]) { const f = fixture(); change(f); assert.throws(() => f.prepare(), /MATERIAL_NOT_WRITABLE_DATA|TRIAL_VALUE_CHANGED/); assert.equal(f.saves, 0); }
});
test('title, wrong mode, journey, storage block and changed format reject prepare', () => {
  for (const change of [
    f => { f.ui.entered = false; }, f => { f.ui.gameMode = 'other'; }, f => { f.engine.mode = 'other'; },
    f => { f.game.platform.storageBlocked = true; },
    f => { f.values.set('zseb-expedition-v1', JSON.stringify({ ...f.state, version: 39 })); },
  ]) { const f = fixture(); change(f); assert.throws(() => f.prepare(), /TRIAL_/); assert.equal(f.saves, 0); }
});
test('ambiguous origin slots and backup-only saves refuse preparing', () => {
  const f = fixture(); f.values.set('zseb-expedition-v1-slot-1', f.values.get('zseb-expedition-v1'));
  assert.throws(() => f.prepare(), /TRIAL_SAVE_KEY_CHANGED/);
  f.values.delete('zseb-expedition-v1-slot-1'); f.values.delete('zseb-expedition-v1');
  f.game.platform.read = () => f.values.get('zseb-expedition-v1-backup');
  assert.throws(() => f.prepare(), /TRIAL_SAVE_KEY_CHANGED/); assert.equal(f.saves, 0);
});
test('changed live snapshot, storage, anchor and other slots reject before assignment', () => {
  for (const change of [
    f => { f.state.gold++; },
    f => { f.values.set('zseb-expedition-v1', JSON.stringify({ ...f.state, gold: 555 })); },
    f => { f.state.rewardClockId = 'other'; },
    f => { f.values.set('zseb-expedition-v1-slot-1', '{"other":"save"}'); },
  ]) {
    const f = fixture(); const prepared = f.prepare(); change(f);
    assert.throws(() => f.commit(prepared), /TRIAL_SNAPSHOT_CHANGED|TRIAL_ANCHOR_CHANGED/);
    assert.equal(f.state.materials.protectionScrolls, 2); assert.equal(f.saves, 0);
  }
});
test('invalid anchor structures cannot be prepared', () => {
  for (const change of [
    f => { f.state.rewardClockId = ''; }, f => { f.state.inventory[0].uid = '7'; },
    f => { f.state.characters = []; }, f => { f.state.owned = ['1']; },
  ]) { const f = fixture(); change(f); f.values.set('zseb-expedition-v1', JSON.stringify(f.state)); assert.throws(() => f.prepare(), /TRIAL_ANCHOR_CONTRACT_CHANGED/); assert.equal(f.saves, 0); }
});
test('unrelated serialization delta is rejected and only unsaved trial value restored', () => {
  const f = fixture(); const prepared = f.prepare();
  f.engine.serialize = function() { const copy = structuredClone(this.state); if (copy.materials.protectionScrolls === 3) copy.inventory = []; return JSON.stringify(copy); };
  const result = f.commit(prepared);
  assert.equal(result.status, 'rejected-before-save'); assert.match(result.error, /TRIAL_UNRELATED_FIELDS_CHANGED/);
  assert.equal(result.liveRestored, true); assert.equal(f.state.materials.protectionScrolls, 2); assert.equal(f.saves, 0);
});
test('a state replaced during trial serialization must not be overwritten or saved', () => {
  const f = fixture(); const prepared = f.prepare();
  f.engine.serialize = function() { const raw = JSON.stringify(this.state); if (this.state.materials.protectionScrolls === 3) this.state = structuredClone(this.state); return raw; };
  const result = f.commit(prepared);
  assert.equal(result.status, 'rejected-before-save'); assert.equal(result.liveRestored, false); assert.equal(f.saves, 0);
});
test('throwing or false native saves do not trigger rollback or retry', () => {
  for (const behavior of ['false', 'throw']) {
    const f = fixture(); let calls = 0;
    f.game.platform.save = raw => { calls++; f.values.set('zseb-expedition-v1', raw); if (behavior === 'throw') throw Error('SAVE_THREW'); return false; };
    const result = f.commit();
    assert.equal(result.status, 'uncertain-after-save'); assert.equal(result.nativeSaveCalled, true); assert.equal(result.retryAllowed, false);
    assert.equal(f.state.materials.protectionScrolls, 3); assert.equal(JSON.parse(f.values.get('zseb-expedition-v1')).materials.protectionScrolls, 3); assert.equal(calls, 1);
  }
});
test('Promise save result is uncertain even if origin already changed', () => {
  const f = fixture(); f.game.platform.save = raw => { f.values.set('zseb-expedition-v1', raw); return Promise.resolve(true); };
  const result = f.commit(); assert.equal(result.status, 'uncertain-after-save'); assert.equal(f.state.materials.protectionScrolls, 3);
});
test('true save return without origin update does not pass verification', () => {
  const f = fixture(); f.game.platform.save = () => true;
  const result = f.commit(); assert.equal(result.status, 'uncertain-after-save'); assert.match(result.error, /TRIAL_STORAGE_UNCONFIRMED/);
});
test('changed or newly-created other main slot is detected after native save', () => {
  for (const exists of [true, false]) {
    const f = fixture(); if (exists) f.values.set('zseb-expedition-v1-slot-1', '{"other":"save"}');
    const save = f.game.platform.save;
    f.game.platform.save = raw => { save(raw); f.values.set('zseb-expedition-v1-slot-1', raw); return true; };
    const result = f.commit(); assert.equal(result.status, 'uncertain-after-save'); assert.match(result.error, /TRIAL_STORAGE_UNCONFIRMED/);
  }
});
test('replaced runtime instance after save is uncertain, not automatically restored', () => {
  const f = fixture(); const save = f.game.platform.save;
  f.game.platform.save = raw => { save(raw); f.engine.state = structuredClone(f.state); return true; };
  const result = f.commit(); assert.equal(result.status, 'uncertain-after-save'); assert.match(result.error, /TRIAL_INSTANCE_CHANGED_AFTER_SAVE/);
});
test('atomic dispatch refreshes an old offline timestamp without filtering any fields', () => {
  const f = fixture(); f.state.offline = { lastActiveAt: 100 }; f.values.set('zseb-expedition-v1', JSON.stringify(f.state));
  const old = f.prepare(); f.state.offline.lastActiveAt = 101;
  assert.throws(() => f.commit(old), /TRIAL_SNAPSHOT_CHANGED/); assert.equal(f.saves, 0);
  const result = f.atomic(old.anchor);
  assert.equal(result.outcome.status, 'storage-verified'); assert.equal(f.saves, 1);
  assert.equal(JSON.parse(result.beforeSave).offline.lastActiveAt, 101);
  assert.equal(JSON.parse(result.outcome.afterSave).offline.lastActiveAt, 101);
});
test('atomic trial still rejects an unrelated offline change during serialization', () => {
  const f = fixture(); f.state.offline = { lastActiveAt: 100 }; f.values.set('zseb-expedition-v1', JSON.stringify(f.state));
  const preflight = f.prepare();
  f.engine.serialize = function() { const copy = structuredClone(this.state); if (copy.materials.protectionScrolls === 3) copy.offline.lastActiveAt++; return JSON.stringify(copy); };
  const result = f.atomic(preflight.anchor);
  assert.equal(result.outcome.status, 'rejected-before-save'); assert.match(result.outcome.error, /TRIAL_UNRELATED_FIELDS_CHANGED/);
  assert.equal(f.state.materials.protectionScrolls, 2); assert.equal(f.saves, 0);
});
test('atomic dispatch checks the preflight anchor before any assignment', () => {
  const f = fixture(); const preflight = f.prepare();
  f.state.inventory[0].uid = 8; f.values.set('zseb-expedition-v1', JSON.stringify(f.state));
  assert.throws(() => f.atomic(preflight.anchor), /TRIAL_ORIGINAL_ANCHOR_CHANGED/);
  assert.equal(f.state.materials.protectionScrolls, 2); assert.equal(f.saves, 0);
});
