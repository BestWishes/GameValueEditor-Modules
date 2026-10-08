'use strict';
// Headless test: import only the native Engine, never main/preload or a profile.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const [archive, fixturePath] = process.argv.slice(2);
assert.equal(process.env.ELECTRON_RUN_AS_NODE, '1');
Object.defineProperty(process, 'type', { value: 'browser', configurable: true });
const { Engine } = require(path.join(archive, 'runtime/src/engine.js'));
const { createProbabilityModel } = require('../src/probability-model.cjs');
const { discoverProbabilitySites } = require('../src/probability-sites.cjs');
const { createProbabilitySession } = require('../src/probability-session.cjs');
const { validateNativeDrops } = require('../src/drops-native-validation.cjs');
const engine = new Engine(fs.readFileSync(fixturePath, 'utf8'), () => 0.5), model = createProbabilityModel();
const original = engine.serialize(), rng = engine.rng;
const discovery = discoverProbabilitySites(engine, model, archive);
console.log(JSON.stringify({ discovered: Object.keys(discovery.sites), errors: discovery.errors }));
assert.equal(engine.serialize(), original); assert.equal(engine.rng, rng);
assert.equal(Object.keys(discovery.sites).length, 13);
const result = validateNativeDrops(Engine, original, createProbabilitySession, model, archive, { sites: discovery.sites });
const groups = ['ordinary', 'immortal', 'mythic', 'protection', 'sacredProtection'];
let wheelChecks = 0;
for (const group of groups) for (const random of [0.001, 0.5, 0.999]) {
  const copy = new Engine(original, () => random); copy.state.materials.wheelTickets = 10; copy.prepareLuckyWheel();
  const control = createProbabilitySession(copy, model, { archivePath: archive, sites: discovery.sites, assertCurrent: () => true });
  control.configure({ wheel: { groupPercent: Object.fromEntries(groups.map(key => [key, key === group ? 100 : 0])) },
    wheelBonus: { doublePercent: 0, marqueePercent: 0 } });
  copy.spinLuckyWheel();
  assert.ok(copy.luckyWheelResults().some(row => model.groupOf(row.prize) === group));
  assert.equal(control.inspect().hits.wheelPrize, 1); assert.equal(control.inspect().hits.wheelBonus, 1); wheelChecks++;
}
for (const percent of [0, 100]) {
  const copy = new Engine(original, () => 0.5); copy.mode = 'guardian'; copy.state.cleared = 10000;
  copy.state.selected = 100; copy.state.guardianSelected = 1;
  const control = createProbabilitySession(copy, model, { archivePath: archive, sites: discovery.sites, assertCurrent: () => true });
  control.configure({ drops: { guardianTicketPercent: percent } }); copy.start(); copy.enemy.hp = 0;
  const before = copy.state.abyss.tickets; copy.victory();
  assert.equal(copy.state.abyss.tickets - before, percent === 100 ? 1 : 0); wheelChecks++;
}
console.log(JSON.stringify({ validation: 'current-native-consumers', dropResult: result, wheelAndTicketChecks: wheelChecks,
  originalUnchanged: engine.serialize() === original, originalActionCalled: false, originalSaveCalled: false }));
