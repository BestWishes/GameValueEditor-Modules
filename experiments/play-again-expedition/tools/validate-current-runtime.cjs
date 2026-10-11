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
for (const group of groups) for (const random of [0.001, 0.5, 0.999]) for (const count of [1, 10]) {
  const copy = new Engine(original, () => random); copy.state.materials.wheelTickets = 100; copy.prepareLuckyWheel();
  const nativeRng = copy.rng, coins = copy.state.materials.wheelTickets;
  const material = group === 'protection' ? 'protectionScrolls' : group === 'sacredProtection' ? 'sacredProtectionScrolls' : null;
  const beforeMaterial = material ? copy.state.materials[material] : 0;
  const control = createProbabilitySession(copy, model, { archivePath: archive, sites: discovery.sites, assertCurrent: () => true });
  control.configure({ wheel: { groupPercent: Object.fromEntries(groups.map(key => [key, key === group ? 100 : 0])) },
    wheelBonus: { doublePercent: 0, marqueePercent: 0 } });
  const action = copy.spinLuckyWheel(count);
  assert.equal(action.ok, true); assert.equal(action.count, count);
  assert.equal(copy.state.materials.wheelTickets, coins - count);
  const rewards = copy.luckyWheelResults(); assert.equal(rewards.length, count);
  assert.ok(rewards.every(row => model.groupOf(row.prize) === group));
  assert.equal(control.inspect().hits.wheelPrize, count); assert.equal(control.inspect().hits.wheelBonus, count);
  assert.equal(control.inspect().blocked, null); assert.equal(copy.rng, nativeRng);
  // Rewards are granted during spin; finish completes native animation/board
  // settlement. Do not assume that finishing is a second reward grant.
  copy.finishLuckyWheel(action.round); assert.equal(copy.state.luckyWheel.pending, null);
  if (material) assert.equal(copy.state.materials[material], beforeMaterial + count);
  const afterRewards = copy.serialize(); assert.equal(control.reset().restored, true); assert.equal(copy.serialize(), afterRewards);
  wheelChecks++;
}
for (const [doublePercent, marqueePercent, expected] of [[100, 0, 'double'], [0, 100, 'marquee'], [0, 0, null]]) {
  const copy = new Engine(original, () => .5); copy.state.materials.wheelTickets = 100; copy.prepareLuckyWheel();
  const control = createProbabilitySession(copy, model, { archivePath: archive, sites: discovery.sites, assertCurrent: () => true });
  control.configure({ wheel: { groupPercent: { ordinary: 0, immortal: 0, mythic: 0, protection: 100, sacredProtection: 0 } },
    wheelBonus: { doublePercent, marqueePercent } });
  const before = copy.state.materials.protectionScrolls;
  const action = copy.spinLuckyWheel(10), expectedRewards = expected === 'double' ? 20 : expected === 'marquee' ? 30 : 10;
  assert.equal(action.ok, true); assert.equal(action.draws, 10); assert.equal(action.count, expectedRewards);
  const spins = copy.state.luckyWheel.pending.spins; assert.equal(spins.length, 10);
  assert.ok(spins.every(spin => spin.bonus === expected));
  const rewards = copy.luckyWheelResults(); assert.equal(rewards.length, expectedRewards);
  assert.ok(rewards.every(row => model.groupOf(row.prize) === 'protection'));
  assert.equal(control.inspect().blocked, null); assert.equal(control.inspect().hits.wheelBonus, 10);
  console.log(JSON.stringify({ tenDrawBonus: expected, rewards: rewards.length, hits: control.inspect().hits }));
  copy.finishLuckyWheel(action.round);
  assert.equal(copy.state.materials.protectionScrolls - before, rewards.length); wheelChecks++;
}
const seeded = () => {
  let seed = 192003, calls = 0;
  return { rng: () => { calls++; seed = Math.imul(1664525, seed) + 1013904223 | 0; return (seed >>> 0) / 4294967296; }, calls: () => calls };
};
{
  const sources = [seeded(), seeded()], copies = sources.map(source => new Engine(original, source.rng));
  for (const copy of copies) { copy.state.materials.wheelTickets = 100; copy.prepareLuckyWheel(); }
  const control = createProbabilitySession(copies[1], model, { archivePath: archive, sites: discovery.sites, assertCurrent: () => true });
  control.configure({ wheel: { groupPercent: { ordinary: 92.5, immortal: 5, mythic: 1, protection: 1, sacredProtection: .5 } },
    wheelBonus: { doublePercent: 2, marqueePercent: 1 } });
  const actions = copies.map(copy => copy.spinLuckyWheel(10));
  assert.deepEqual(actions[0], actions[1]); assert.equal(copies[0].serialize(), copies[1].serialize());
  assert.equal(sources[0].calls(), sources[1].calls()); assert.equal(control.inspect().blocked, null); wheelChecks++;
}
{
  const source = seeded(), copy = new Engine(original, source.rng); copy.state.materials.wheelTickets = 9; copy.prepareLuckyWheel();
  const control = createProbabilitySession(copy, model, { archivePath: archive, sites: discovery.sites, assertCurrent: () => true });
  control.configure({ wheel: { groupPercent: { ordinary: 0, immortal: 100, mythic: 0, protection: 0, sacredProtection: 0 },
    typeWeights: { gear: 0, badge: 0, gem: 0, costume: 1 } }, wheelBonus: { doublePercent: 0, marqueePercent: 0 } });
  const before = copy.serialize(), calls = source.calls();
  assert.equal(copy.spinLuckyWheel(10).ok, false); assert.equal(copy.serialize(), before); assert.equal(source.calls(), calls);
  copy.state.materials.wheelTickets = 100;
  const action = copy.spinLuckyWheel(10); assert.equal(action.ok, true);
  assert.ok(copy.luckyWheelResults().every(row => row.prize.kind === 'costume' && row.prize.quality === 5));
  assert.equal(control.inspect().blocked, null); copy.finishLuckyWheel(action.round);
  const single = copy.spinLuckyWheel(1); assert.equal(single.ok, true);
  assert.ok(copy.luckyWheelResults().every(row => row.prize.kind === 'costume' && row.prize.quality === 5));
  assert.equal(copy.rng, source.rng); assert.equal(control.inspect().blocked, null); wheelChecks++;
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
