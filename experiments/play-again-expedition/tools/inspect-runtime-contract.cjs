'use strict';
// Execute only pure engine/config modules in a separate matching Node runtime.
// Never load main/preload, open the user profile, or connect to the running game.
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const { materialTransaction } = require('../src/material-transaction.cjs');
const archive = process.argv[2];
if (!archive || process.versions.electron !== '44.2.0') throw Error('Matching research runtime required');
Object.defineProperty(process, 'type', { value: 'browser', configurable: true });
const protection = require(path.join(archive, 'runtime/src/enhance-protection.js'));
const supplies = require(path.join(archive, 'runtime/src/crafting-supplies.js'));
const engineModule = require(path.join(archive, 'runtime/src/engine.js'));
console.log(JSON.stringify({ electron: process.versions.electron, protectionExports: Object.keys(protection), protectionDefinitions: protection.PROTECTION_SCROLLS, suppliesExports: Object.keys(supplies), engineExports: Object.keys(engineModule) }, null, 2));
if (typeof protection.validProtectionMaterials === 'function') {
  const probes = [0, 1, 999, 9999, 10000, 999999, 1000000, 1000000000, Number.MAX_SAFE_INTEGER, -1, 1.5, NaN, Infinity];
  console.log(JSON.stringify({ rangeProbes: probes.map(value => ({ value: Number.isFinite(value) ? value : String(value), valid: protection.validProtectionMaterials({ protectionScrolls: value, sacredProtectionScrolls: 0 }) })) }, null, 2));
  let low = 0, high = Number.MAX_SAFE_INTEGER;
  while (low < high) {
    const middle = low + Math.ceil((high - low) / 2);
    if (protection.validProtectionMaterials({ protectionScrolls: middle, sacredProtectionScrolls: 0 })) low = middle;
    else high = middle - 1;
  }
  console.log(JSON.stringify({ quantityLimit: low, boundaryAccepted: protection.validProtectionMaterials({ protectionScrolls: low, sacredProtectionScrolls: 0 }), nextRejected: !protection.validProtectionMaterials({ protectionScrolls: low + 1, sacredProtectionScrolls: 0 }) }));
}
if (typeof engineModule.Engine === 'function') {
  const engine = new engineModule.Engine(null, () => 0.5);
  const serialized = engine.serialize();
  const save = JSON.parse(serialized);
  const original = engine.state.materials.protectionScrolls;
  engine.state.materials.protectionScrolls = original + 1;
  const changed = JSON.parse(engine.serialize());
  const reloaded = new engineModule.Engine(JSON.stringify(changed), () => 0.5);
  changed.materials.protectionScrolls = original;
  console.log(JSON.stringify({ defaultEngine: { saveVersion: save.version, materials: save.materials, statePropertyNames: Object.keys(engine), serializeType: typeof serialized, saveKeys: Object.keys(save), serializedOnlyMaterialChanged: JSON.stringify(save) === JSON.stringify(changed), reloadQuantity: reloaded.state.materials.protectionScrolls } }, null, 2));
  const storage = new Map([['zseb-expedition-v1', engine.serialize()]]);
  const game = { engine, getUi: () => ({ entered: true, gameMode: 'expedition' }), platform: { read() { return storage.get('zseb-expedition-v1'); }, save(raw) { storage.set('zseb-expedition-v1', raw); return true; } } };
  const context = vm.createContext({ window: { expedition: game }, localStorage: { get length() { return storage.size; }, key: index => [...storage.keys()][index], getItem: key => storage.get(key) } });
  const run = (operation, args) => vm.runInContext(`(${materialTransaction.toString()})(${JSON.stringify(operation)},${JSON.stringify(args ?? null)},'synthetic-real-engine')`, context);
  for (const targetValue of [original + 2, original]) {
    const current = run('read');
    const input = { expectedInstance: current.instance, expectedValue: current.value, targetValue };
    const prepared = run('prepare', input);
    const result = run('commit', { ...input, beforeSave: prepared.beforeSave });
    assert.equal(result.value, targetValue);
    assert.equal(result.storageVerified, true);
    assert.equal(new engineModule.Engine(storage.get('zseb-expedition-v1'), () => 0.5).state.materials.protectionScrolls, targetValue);
  }
  assert.equal(engine.serialize(), serialized);
  console.log(JSON.stringify({ realEngineTransactionFixture: 'PASS', fakeStorageOnly: true, originalProfileOpened: false, liveGameTouched: false }));
}
