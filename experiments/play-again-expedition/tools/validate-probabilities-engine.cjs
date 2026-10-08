'use strict';
// Only a headless copied-save fixture; never import main/preload or attach/save.
const assert = require('node:assert/strict');
const path = require('node:path');
const crypto = require('node:crypto');
const fs = require('original-fs');
const [archive, fixturePath] = process.argv.slice(2);
assert.equal(process.env.ELECTRON_RUN_AS_NODE, '1');
assert.equal(process.versions.electron, '44.2.0'); assert.equal(process.versions.node, '24.20.0'); assert.equal(process.arch, 'x64');
assert.equal(crypto.createHash('sha256').update(fs.readFileSync(archive)).digest('hex'), '5a4f4e414bd06fc248cfe2ad30e7d9ee3b110968db888fdb733cd57f78a6b294');
assert.equal(path.resolve(fixturePath).toLowerCase(), path.resolve(__dirname,
  '../../../artifacts/play-again-expedition/20261007-213142-offline-recovery/restore-protection-scrolls-2.json').toLowerCase());
Object.defineProperty(process, 'type', { value: 'browser', configurable: true });
const { Engine, DATA } = require(path.join(archive, 'runtime/src/engine.js'));
const wheel = require(path.join(archive, 'runtime/src/lucky-wheel.js'));
const flame = require(path.join(archive, 'runtime/src/flame-immortals.js'));
const { createProbabilityModel } = require('../src/probability-model.cjs');
const { createProbabilitySession } = require('../src/probability-session.cjs');
const fixture = fs.readFileSync(fixturePath, 'utf8'), model = createProbabilityModel();
let cases = 0;
const session = engine => createProbabilitySession(engine, model, { archivePath: archive, assertCurrent: () => true });
const constant = value => () => value;
const getNewItems = (before, engine) => engine.state.inventory.filter(item => !before.has(item.uid));
const defaultGroups = { ordinary: 92.5, immortal: 5, mythic: 1, protection: 1, sacredProtection: 0.5 };
// Model must agree with the real native sampler, including its <= boundary.
const reference = new Engine(fixture, constant(0.5)); reference.prepareLuckyWheel();
const board = reference.state.luckyWheel.board;
assert.deepEqual(model.wheelWeights(board).native, wheel.wheelWeights(board)); cases++;
for (const group of Object.keys(defaultGroups)) {
  const profile = { groupPercent: Object.fromEntries(Object.keys(defaultGroups).map(key => [key, key === group ? 100 : 0])) };
  const { native, desired } = model.wheelWeights(board, profile);
  for (const value of [0, 0.000001, 0.1, 0.5, 0.99, 1 - Number.EPSILON]) {
    const index = wheel.chooseWheelIndex(board, constant(model.mapCategorical(value, native, desired)));
    assert.equal(model.groupOf(board[index]), group); cases++;
  }
}
function spin(profile, value = 0.5) {
  const engine = new Engine(fixture, constant(value)); engine.prepareLuckyWheel();
  const control = session(engine), snapshot = engine.serialize(), beforeTickets = engine.state.materials.wheelTickets;
  const before = new Set(engine.state.inventory.map(item => item.uid)), rng = engine.rng;
  control.configure(profile); assert.equal(engine.serialize(), snapshot); cases++;
  engine.spinLuckyWheel();
  assert.equal(engine.rng, rng); assert.equal(engine.state.materials.wheelTickets, beforeTickets - 1);
  const report = control.inspect(); assert.equal(report.blocked, null); cases++;
  return { engine, control, report, newItems: getNewItems(before, engine) };
}
for (const group of Object.keys(defaultGroups)) {
  for (const value of [0, 0.001, 0.01, 0.1, 0.5, 0.99, 1 - Number.EPSILON]) {
    const result = spin({ wheel: { groupPercent: Object.fromEntries(Object.keys(defaultGroups).map(key => [key, key === group ? 100 : 0])) },
      wheelBonus: { doublePercent: 0, marqueePercent: 0 } }, value);
    assert.equal(result.report.hits.wheelPrize, 1);
    const awards = result.engine.luckyWheelResults();
    assert.ok(awards.some(row => model.groupOf(row.prize) === group), JSON.stringify({ group, pending: result.engine.state.luckyWheel.pending, awards })); cases++;
  }
}
for (const [doublePercent, marqueePercent, expected] of [[100, 0, 'double'], [0, 100, 'marquee'], [0, 0, null]]) {
  const result = spin({ wheelBonus: { doublePercent, marqueePercent } });
  assert.equal(result.report.hits.wheelBonus, 1);
  assert.equal(result.engine.state.luckyWheel.pending.bonus, expected); cases++;
}
// Default profile remains bit-for-bit native, not merely statistically similar.
const plain = new Engine(fixture, constant(0.5)); plain.prepareLuckyWheel(); plain.spinLuckyWheel();
const defaults = spin({ wheel: { groupPercent: defaultGroups }, wheelBonus: { doublePercent: 2, marqueePercent: 1 } });
assert.equal(defaults.engine.serialize(), plain.serialize()); cases++;
function victory(mode, profile, pity) {
  const engine = new Engine(fixture, constant(0.5)); engine.mode = mode;
  if (pity !== undefined) engine.state.abyss.progress[engine.abyssConfig().legacyIndex].pity = pity;
  const control = session(engine), snapshot = engine.serialize(); control.configure(profile);
  assert.equal(snapshot, engine.serialize()); cases++;
  engine.start(); engine.enemy.hp = 0;
  const before = JSON.parse(engine.serialize()), uids = new Set(before.inventory.map(item => item.uid)), rng = engine.rng;
  engine.victory(); const report = control.inspect();
  assert.equal(report.blocked, null); assert.equal(engine.rng, rng); cases++;
  return { engine, before, newItems: getNewItems(uids, engine), report };
}
for (const percent of [0, 100]) {
  const result = victory('guardian', { drops: { guardianTicketPercent: percent } });
  assert.equal(result.engine.state.abyss.tickets - result.before.abyss.tickets, percent === 100 ? 1 : 0);
  assert.equal(result.report.hits.guardianTicket, 1); cases++;
  const abyss = victory('abyss', { drops: { abyssImmortalPercent: percent } }, 0);
  assert.equal(abyss.newItems.length, percent === 100 ? 1 : 0); assert.ok(abyss.report.hits.abyssImmortal > 0); cases++;
  const journey = victory('journey', { drops: { equipmentBonusPercent: percent === 100 ? 100 : -100 } });
  assert.equal(journey.newItems.length, percent === 100 ? 1 : 0); cases++;
}
const guaranteed = victory('abyss', { drops: { abyssImmortalPercent: 0 } }, 100);
assert.equal(guaranteed.newItems.length, 1); assert.equal(guaranteed.newItems[0].quality, 5); cases++;
const clean = new Engine(fixture, constant(0.5)), control = session(clean), before = clean.serialize();
control.configure({ drops: { guardianTicketPercent: 100 } }); assert.equal(control.reset().restored, true);
assert.equal(clean.serialize(), before); assert.equal(Object.hasOwn(clean, 'victory'), false); cases++;
// The 5 original boss pools: independent rolls, not a fabricated replacement award.
for (const [id, keys] of Object.entries(flame.POOLS)) {
  const mythic = Number(id) === 25404;
  for (const key of keys) { assert.equal(flame.dropChance(DATA.gear[key]), mythic ? 0.005 : 0.05); cases++; }
  for (const percent of [0, 100]) {
    const e = new Engine(fixture, constant(0.5)); e.mode = 'flame';
    const control = session(e), original = e.dropFlameImmortals, before = new Set(e.state.inventory.map(item => item.uid));
    const rng = e.rng; control.configure({ drops: { flameImmortalPercent: percent, flameMythicPercent: percent } });
    e.dropFlameImmortals(Number(id)); const items = getNewItems(before, e), report = control.inspect();
    assert.equal(items.length, percent === 100 ? keys.length : 0);
    if (percent === 100) { assert.deepEqual(items.map(item => item.key).sort(), [...keys].sort()); assert.ok(items.every(item => item.quality === (mythic ? 6 : 5))); }
    assert.equal(report.hits[mythic ? 'flameMythicPercent' : 'flameImmortalPercent'], keys.length);
    assert.equal(report.blocked, null); assert.equal(e.rng, rng); control.reset(); assert.equal(e.dropFlameImmortals, original); cases++;
  }
}
// Native default comparison and independent controls for the two flame pools.
for (const id of [25400, 25404]) {
  const plain = new Engine(fixture, constant(0.003)); plain.mode = 'flame'; plain.dropFlameImmortals(id);
  const e = new Engine(fixture, constant(0.003)); e.mode = 'flame'; const control = session(e);
  control.configure({ drops: { flameImmortalPercent: 5, flameMythicPercent: 0.5 } }); e.dropFlameImmortals(id);
  assert.equal(e.serialize(), plain.serialize()); cases++;
  for (const [immortal, mythic] of [[0, 100], [100, 0]]) {
    const x = new Engine(fixture, constant(0.5)); x.mode = 'flame'; const manager = session(x);
    const uids = new Set(x.state.inventory.map(item => item.uid));
    manager.configure({ drops: { flameImmortalPercent: immortal, flameMythicPercent: mythic } }); x.dropFlameImmortals(id);
    const expected = (id === 25404 ? mythic : immortal) === 100 ? flame.POOLS[id].length : 0;
    assert.equal(getNewItems(uids, x).length, expected); cases++;
  }
}
console.log(JSON.stringify({ exactBuild: '0.116.70', passedCases: cases, liveGameAttached: false, saveWritten: false }));
