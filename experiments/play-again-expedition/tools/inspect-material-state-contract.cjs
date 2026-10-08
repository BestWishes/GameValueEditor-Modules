'use strict';
// Read-only discovery in a matching isolated runtime. No preload/main/profile,
// TCP listener, game UI, save write, or connection to the original game.
const fs = require('node:fs');
const originalFs = require('original-fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const [archive, fixturePath] = process.argv.slice(2);
assert.equal(process.env.ELECTRON_RUN_AS_NODE, '1');
assert.equal(process.versions.electron, '44.2.0');
assert.equal(process.versions.node, '24.20.0');
assert.equal(crypto.createHash('sha256').update(originalFs.readFileSync(archive)).digest('hex'),
  '5a4f4e414bd06fc248cfe2ad30e7d9ee3b110968db888fdb733cd57f78a6b294');
assert.equal(path.resolve(fixturePath).toLowerCase(), path.resolve(__dirname,
  '../../../artifacts/play-again-expedition/20261007-213142-offline-recovery/restore-protection-scrolls-2.json').toLowerCase());
Object.defineProperty(process, 'type', { value: 'browser', configurable: true });
const { Engine } = require(path.join(archive, 'runtime/src/engine.js'));
const fixture = fs.readFileSync(fixturePath, 'utf8');
const engine = new Engine(fixture, () => 0.5);
const properties = Object.keys(engine);
const methods = Object.getOwnPropertyNames(Object.getPrototypeOf(engine));
const state = JSON.parse(engine.serialize());
const describe = key => {
  const descriptor = Object.getOwnPropertyDescriptor(engine, key) ?? Object.getOwnPropertyDescriptor(Object.getPrototypeOf(engine), key);
  return descriptor ? { writable: descriptor.writable, get: typeof descriptor.get, set: typeof descriptor.set, valueType: typeof descriptor.value } : null;
};
const probes = [];
for (const activity of ['guardian', 'journey', 'abyss', 'cow', 'mine', 'flame', 'training', 'terror']) {
  const copy = new Engine(fixture, () => 0.5); copy.mode = activity;
  const materials = copy.state.materials, before = copy.serialize();
  const original = materials.protectionScrolls, target = original + 1;
  materials.protectionScrolls = target;
  const after = copy.serialize(), restored = JSON.parse(after);
  restored.materials.protectionScrolls = original;
  assert.equal(JSON.stringify(restored), JSON.stringify(JSON.parse(before)), activity + ': unrelated data');
  assert.equal(copy.state.materials, materials, activity + ': material owner');
  assert.equal(new Engine(after, () => 0.5).state.materials.protectionScrolls, target, activity + ': reload');
  materials.protectionScrolls = original;
  assert.equal(copy.serialize(), before, activity + ': restore');
  probes.push({ activity, sameMaterialAuthority: true, singleLeafRoundtrip: true });
}
const selectCopy = new Engine(fixture, () => 0.5), selectOwner = selectCopy.state.materials;
selectCopy.select(selectCopy.state.selected);
assert.equal(selectCopy.state.materials, selectOwner);
console.log(JSON.stringify({ mode: engine.mode, phase: engine.phase, properties,
  relevantMethods: methods.filter(name => /select|serialize|escape|startGuardian|startMine|startCow|startTraining/i.test(name)),
  descriptors: { mode: describe('mode'), phase: describe('phase') },
  saveVersion: state.version, saveKeys: Object.keys(state), materialKeys: Object.keys(state.materials),
  probes, nativeSelect: { activity: selectCopy.mode, phase: selectCopy.phase, sameMaterialAuthority: true },
  originalProfileOpened: false, originalGameConnected: false, saveWritten: false }, null, 2));
