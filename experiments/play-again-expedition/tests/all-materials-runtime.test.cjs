'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const { createMaterialsRuntime } = require('../src/all-materials-runtime.cjs');
const catalog = createMaterialsRuntime()('catalog');
function fixture(slot = 'zseb-expedition-v1') {
  const state = { version: 38, rewardClockId: 'clock-fixture', gold: 81,
    inventory: [{ uid: 7, atk: 32 }], ores: [{ uid: 8, tier: 2 }], gems: [{ uid: 9, color: 'red' }],
    characters: { hero1: { level: 11 } }, owned: [1], materials: {}, tickets: 2,
    abyss: { tickets: 2, clears: 10 }, offline: { lastActiveAt: 10 }, luckyWheel: { round: 2, incomeAt: 1000 } };
  for (const item of catalog.filter(item => !['recruitTickets', 'abyssTickets'].includes(item.id))) state.materials[item.id] = 2;
  const values = new Map([[slot, JSON.stringify(state)], ['zseb-expedition-v1-backup', JSON.stringify(state)], ['other-key', 'preserve']]);
  const engine = { state, mode: 'guardian', phase: 'idle', serialize() { serializeCalls++; return JSON.stringify(this.state); } };
  const ui = { entered: true, gameMode: 'expedition' };
  let saveCalls = 0, serializeCalls = 0;
  const game = { engine, getUi: () => ui, platform: { read: () => values.get(slot), save: raw => { saveCalls++; values.set(slot, raw); return true; } } };
  const storage = { get length() { return values.size; }, key: i => [...values.keys()][i], getItem: key => values.get(key) ?? null };
  const context = vm.createContext({ window: { expedition: game }, localStorage: storage });
  const run = vm.runInContext(`(${createMaterialsRuntime.toString()})()`, context);
  const list = () => run('list');
  const set = (id = 'stones', targetValue = 3, read = list(), expectedValue = read.rows.find(row => row.id === id).value) => run('set', {
    materialId: id, expectedValue, targetValue, slot: read.slot, journeyMode: read.journeyMode, anchor: read.anchor,
  });
  const leaf = id => id === 'recruitTickets' ? [state, 'tickets'] : id === 'abyssTickets' ? [state.abyss, 'tickets'] : [state.materials, id];
  const snapshot = () => values.set(slot, JSON.stringify(state));
  return { run, list, set, leaf, state, engine, ui, game, values, context, snapshot,
    get saves() { return saveCalls; }, get serializes() { return serializeCalls; } };
}

test('catalog has unique semantic identities, all 27 materials plus two ticket authorities', () => {
  assert.equal(catalog.length, 29); assert.equal(new Set(catalog.map(item => item.id)).size, 29);
  assert.ok(catalog.some(item => item.id === 'protectionScrolls'));
  assert.equal(catalog.find(item => item.id === 'soulGems').maximum, 9999);
  assert.equal(catalog.find(item => item.id === 'creationGems').maximum, 9999);
  assert.ok(catalog.every(item => !Object.hasOwn(item, 'path')));
});
test('catalog works offline, mutation of returned catalog cannot affect the allowlist', () => {
  const runtime = createMaterialsRuntime(); const definitions = runtime('catalog');
  definitions[0].maximum = Infinity; definitions[0].id = 'gold';
  assert.equal(runtime('catalog')[0].maximum, 1000000000); assert.equal(runtime('catalog')[0].id, 'stones');
});
test('list is read-only, exposes zero counts, does not serialize/save or create global caches', () => {
  const f = fixture(); f.state.materials.stones = 0; f.snapshot();
  const keys = vm.runInContext('Object.getOwnPropertyNames(globalThis)', f.context);
  Object.freeze(f.state.materials); Object.freeze(f.state); Object.freeze(f.engine); Object.freeze(f.game.platform); Object.freeze(f.game);
  const before = [...f.values]; const result = f.list();
  assert.equal(result.rows.length, 29); assert.equal(result.rows.find(row => row.id === 'stones').value, 0);
  assert.equal(result.readOnly, true); assert.equal(f.saves, 0); assert.equal(f.serializes, 0);
  assert.deepEqual([...f.values], before); assert.deepEqual(vm.runInContext('Object.getOwnPropertyNames(globalThis)', f.context), keys);
  assert.equal(Object.hasOwn(result, 'beforeSave'), false); assert.equal(Object.hasOwn(result, 'storedBefore'), false);
});

for (const activity of ['guardian', 'journey', 'abyss', 'cow', 'mine', 'flame', 'training', 'terror', 'future-activity']) {
  test(`${activity}: shared materials stay readable/editable without selecting an internal activity`, () => {
    const f = fixture(); f.engine.mode = activity;
    const read = f.list(), baseline = JSON.stringify(f.state);
    assert.equal(read.journeyMode, activity); assert.equal(read.rows.length, 29);
    assert.ok(read.rows.every(row => row.canWrite && !/模式/.test(row.status)));
    for (const item of catalog) {
      const result = f.set(item.id, 3);
      assert.equal(result.status, 'storage-verified'); assert.equal(result.journeyMode, activity);
      const restored = f.set(item.id, 2); assert.equal(restored.status, 'storage-verified');
      assert.equal(JSON.stringify(f.state), baseline); assert.equal(f.values.get('zseb-expedition-v1'), baseline);
    }
  });
}
test('changing activity after read rejects stale receipt before writing; fresh receipt works', () => {
  const f = fixture(), read = f.list(); f.engine.mode = 'journey';
  assert.throws(() => f.set('stones', 3, read), /ACTIVE_SAVE_CHANGED/); assert.equal(f.saves, 0);
  assert.equal(f.set('stones', 3).status, 'storage-verified');
});
test('activity replacement during serialization cannot save against another context', () => {
  const f = fixture(), original = f.engine.serialize;
  f.engine.serialize = function() { const raw = original.call(this); this.mode = 'journey'; return raw; };
  assert.throws(() => f.set(), /CHANGED_BEFORE_ASSIGNMENT/);
  assert.equal(f.saves, 0); assert.equal(f.state.materials.stones, 2);
});

for (const item of catalog) {
  for (const targetValue of [0, 17, item.maximum]) test(`${item.id}: exact single-leaf set to ${targetValue} and restore`, () => {
    const f = fixture(), before = JSON.stringify(f.state), otherKeys = [...f.values].filter(([key]) => key !== 'zseb-expedition-v1');
    const result = f.set(item.id, targetValue);
    assert.equal(result.status, 'storage-verified'); assert.equal(result.value, targetValue);
    assert.equal(f.leaf(item.id)[0][f.leaf(item.id)[1]], targetValue); assert.equal(f.saves, 1);
    assert.equal(result.onlySerializedMaterialChanged, true); assert.equal(result.storageVerified, true);
    assert.deepEqual([...f.values].filter(([key]) => key !== 'zseb-expedition-v1'), otherKeys);
    const restore = f.set(item.id, 2); assert.equal(restore.status, 'storage-verified'); assert.equal(f.saves, 2);
    assert.equal(JSON.stringify(f.state), before); assert.equal(f.values.get('zseb-expedition-v1'), before);
  });
  test(`${item.id}: same quantity is zero-write; invalid targets cannot save`, () => {
    const f = fixture(), read = f.list(); const unchanged = f.set(item.id, 2, read);
    assert.equal(unchanged.status, 'unchanged'); assert.equal(f.saves, 0); assert.equal(f.serializes, 0);
    for (const target of [-1, 1.5, NaN, Infinity, '3', item.maximum + 1, Number.MAX_SAFE_INTEGER]) {
      assert.throws(() => f.set(item.id, target, read), /MATERIAL_RANGE_INVALID/);
    }
    assert.equal(f.saves, 0); assert.equal(f.serializes, 0);
  });
}
test('foreign operations/arguments, prototype keys, paths, non-material currencies are rejected', () => {
  const f = fixture();
  for (const operation of ['eval', 'prepare', 'commit', 'save']) assert.throws(() => f.run(operation), /UNKNOWN_MATERIAL_OPERATION/);
  assert.throws(() => f.run('list', { source: 'anything' }), /UNKNOWN_MATERIAL_ARGUMENT/);
  for (const id of ['gold', '__proto__', 'constructor', 'materials.stones', 'ores', 'unrecognized.other']) {
    assert.throws(() => f.set(id, 3, f.list(), 2), /MATERIAL_NOT_ALLOWED/);
  }
  assert.equal(f.saves, 0);
});
test('missing, accessor, frozen, malformed, saved mismatch are per-row read-only, never created or coerced', () => {
  for (const [mutate, reason] of [
    [f => { delete f.state.materials.stones; }, 'MATERIAL_NOT_DEFINED'],
    [f => Object.defineProperty(f.state.materials, 'stones', { get() { throw Error('accessor invoked'); }, configurable: true }), 'MATERIAL_NOT_DEFINED'],
    [f => Object.freeze(f.state.materials), 'MATERIAL_READ_ONLY'],
    [f => { f.state.materials.stones = '2'; }, 'MATERIAL_RANGE_INVALID'],
    [f => { f.state.materials.stones = -2; }, 'MATERIAL_RANGE_INVALID'],
    [f => { f.state.materials.stones = 3; }, 'MATERIAL_SAVE_PENDING'],
  ]) {
    const f = fixture(); mutate(f); const read = f.list();
    assert.equal(read.rows.find(row => row.id === 'stones').canWrite, false);
    assert.throws(() => f.set('stones', 3, read, 2), { message: reason }); assert.equal(f.saves, 0);
  }
});
test('unknown material is displayed read-only and cannot become a setter', () => {
  const f = fixture(); f.state.materials.futureMaterial = 100; f.snapshot();
  const row = f.list().rows.find(row => row.id === 'unrecognized.futureMaterial');
  assert.equal(row.value, 100); assert.equal(row.canWrite, false);
  assert.throws(() => f.set('unrecognized.futureMaterial', 101, f.list(), 100), /MATERIAL_NOT_ALLOWED/);
});
test('pending roulette prevents material writes without serializing or saving', () => {
  const f = fixture(); f.state.luckyWheel.pending = { prize: 'protectionScrolls' }; f.snapshot();
  const read = f.list(); assert.ok(read.rows.every(row => !row.canWrite));
  assert.throws(() => f.set('protectionScrolls', 3, read), /MATERIAL_WHEEL_PENDING/);
  assert.equal(f.saves, 0); assert.equal(f.serializes, 0); assert.equal(f.state.materials.protectionScrolls, 2);
});
test('pending roulette appearing during serialization is rejected before assignment', () => {
  const f = fixture(), read = f.list();
  const serialize = f.engine.serialize;
  f.engine.serialize = function () { this.state.luckyWheel.pending = { animation: true }; return serialize.call(this); };
  assert.throws(() => f.set('protectionScrolls', 3, read), /CHANGED_BEFORE_ASSIGNMENT/);
  assert.equal(f.saves, 0); assert.equal(f.state.materials.protectionScrolls, 2);
});
test('list does not touch accessors for unknown material values', () => {
  const f = fixture(); Object.defineProperty(f.state.materials, 'evil', { enumerable: true, get() { throw Error('getter called'); } });
  assert.equal(f.list().rows.find(row => row.id === 'unrecognized.evil').value, null);
});
test('stale expected count and save identity are checked before assignment', () => {
  const f = fixture(), read = f.list(); f.state.materials.stones = 4; f.snapshot();
  assert.throws(() => f.set('stones', 3, read, 2), /MATERIAL_VALUE_CHANGED/);
  f.state.materials.stones = 2; f.state.rewardClockId = 'other'; f.snapshot();
  assert.throws(() => f.set('stones', 3, read, 2), /ACTIVE_SAVE_CHANGED/); assert.equal(f.saves, 0);
});
test('unentered/other save, malformed activity and malformed format are rejected without guessing a mode', () => {
  for (const mutate of [f => { f.ui.entered = false; }, f => { f.ui.gameMode = 'threeKingdoms'; },
    f => { f.engine.mode = ''; }, f => { f.engine.mode = null; }, f => { f.engine.mode = 'x'.repeat(129); },
    f => Object.defineProperty(f.engine, 'mode', { get() { throw Error('accessor invoked'); } }),
    f => { f.state.version = 'unknown'; f.snapshot(); }]) {
    const f = fixture(); mutate(f); assert.throws(f.list, /GAME_NOT_ENTERED|GAME_MODE_NOT_SUPPORTED|MATERIAL_CONTRACT_CHANGED|SAVE_FORMAT_CHANGED/); assert.equal(f.saves, 0);
  }
});
test('save format increments preserve semantic material editing and unrelated fields', () => {
  for (const version of [39, 40]) {
    const f = fixture(); f.state.version = version; f.state.newUpdateField = { enabled: true }; f.snapshot();
    const read = f.list(); assert.equal(read.saveFormat, version);
    assert.equal(f.set('stones', 3, read).status, 'storage-verified');
    assert.equal(f.state.version, version); assert.deepEqual(f.state.newUpdateField, { enabled: true });
  }
});
test('busy states remain readable and supply accurate per-row/action reasons without saving', () => {
  for (const [mutate, reason, description] of [
    [f => { f.engine.phase = 'fighting'; }, 'MATERIAL_BATTLE_ACTIVE', '战斗尚未结束'],
    [f => { f.engine.phase = 'fighting'; f.engine.paused = true; }, 'MATERIAL_BATTLE_PAUSED', '战斗已暂停'],
    [f => { f.engine.phase = 'reward'; }, 'MATERIAL_OPERATION_PENDING', '正在处理当前操作'],
    [f => { f.game.platform.storageBlocked = true; }, 'MATERIAL_SAVE_BLOCKED', '游戏保存受阻'],
    [f => { f.state.luckyWheel.pending = { animation: true }; f.snapshot(); }, 'MATERIAL_WHEEL_PENDING', '大转盘'],
  ]) {
    const f = fixture(); mutate(f); assert.equal(f.list().rows.every(row => !row.canWrite), true);
    assert.ok(f.list().rows.every(row => row.reason === reason && row.status.includes(description)));
    assert.throws(() => f.set('stones', 3), { message: reason }); assert.equal(f.saves, 0);
    assert.equal(f.serializes, 0); assert.equal(f.state.materials.stones, 2);
  }
});
test('all four unique main slots work; identical slots and backup-only matches refuse', () => {
  for (let index = 1; index <= 4; index++) {
    const f = fixture('zseb-expedition-v1-slot-' + index); assert.equal(f.set().status, 'storage-verified');
  }
  const f = fixture(); f.values.set('zseb-expedition-v1-slot-1', f.values.get('zseb-expedition-v1'));
  assert.throws(f.list, /ACTIVE_SAVE_AMBIGUOUS/); f.values.delete('zseb-expedition-v1-slot-1'); f.values.delete('zseb-expedition-v1');
  f.game.platform.read = () => f.values.get('zseb-expedition-v1-backup'); assert.throws(f.list, /ACTIVE_SAVE_AMBIGUOUS/);
});
test('origin mutation or game replacement during read cannot return a valid receipt', () => {
  const f = fixture(); let calls = 0;
  f.game.platform.read = () => { if (++calls === 2) vm.runInContext('window.expedition = {}', f.context); return f.values.get('zseb-expedition-v1'); };
  assert.throws(f.list, /READ_INSTANCE_CHANGED/);
});
test('serialization changes unrelated inventory/offline/wheel data reject before save and restore only target', () => {
  for (const key of ['inventory', 'offline', 'luckyWheel']) {
    const f = fixture(); f.engine.serialize = function() { const copy = structuredClone(this.state); if (copy.materials.stones === 3) copy[key] = null; return JSON.stringify(copy); };
    const result = f.set(); assert.equal(result.status, 'rejected-before-save'); assert.equal(result.liveRestored, true);
    assert.equal(f.state.materials.stones, 2); assert.equal(f.saves, 0);
  }
});
test('nested owner replaced during serialization is never restored or saved over', () => {
  const f = fixture(); f.engine.serialize = function() { const raw = JSON.stringify(this.state); if (this.state.abyss.tickets === 3) this.state.abyss = { ...this.state.abyss }; return raw; };
  const result = f.set('abyssTickets'); assert.equal(result.status, 'rejected-before-save'); assert.equal(result.liveRestored, false); assert.equal(f.saves, 0);
});
test('phase and storage-block transitions during serialization reject before native save', () => {
  for (const mutate of [f => { f.engine.phase = 'fighting'; }, f => { f.game.platform.storageBlocked = true; }]) {
    const f = fixture(); f.engine.serialize = function() { const raw = JSON.stringify(this.state); if (this.state.materials.stones === 3) mutate(f); return raw; };
    const result = f.set(); assert.equal(result.status, 'rejected-before-save'); assert.equal(result.liveRestored, true); assert.equal(f.saves, 0);
  }
});
test('native save false, throw, async, fake success, unrelated-slot change are uncertain with no rollback/retry', () => {
  for (const behavior of ['false', 'throw', 'async', 'fake', 'other-slot']) {
    const f = fixture(); let calls = 0;
    f.game.platform.save = raw => { calls++; if (behavior !== 'fake') f.values.set('zseb-expedition-v1', raw);
      if (behavior === 'throw') throw Error('uncertain'); if (behavior === 'false') return false;
      if (behavior === 'async') return Promise.resolve(true); if (behavior === 'other-slot') f.values.set('zseb-expedition-v1-slot-1', 'unexpected'); return true; };
    const result = f.set(); assert.equal(result.status, 'uncertain-after-save'); assert.equal(result.retryAllowed, false);
    assert.equal(result.nativeSaveCalled, true); assert.equal(f.state.materials.stones, 3); assert.equal(calls, 1);
  }
});
test('natural changes between preflight and set are preserved by synchronous fresh capture', () => {
  const f = fixture(), read = f.list(); f.state.offline.lastActiveAt++;
  const result = f.set('stones', 3, read); assert.equal(result.status, 'storage-verified');
  assert.equal(JSON.parse(f.values.get('zseb-expedition-v1')).offline.lastActiveAt, 11);
});

module.exports = { fixture };
