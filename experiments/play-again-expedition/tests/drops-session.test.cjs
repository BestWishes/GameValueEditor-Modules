'use strict';
const test = require('node:test'), assert = require('node:assert/strict');
const { createProbabilityModel } = require('../src/probability-model.cjs');
const { createProbabilitySession } = require('../src/probability-session.cjs');
const model = createProbabilityModel();
function fixture(layout = 'standalone-0.116.70') {
  let draws = 0;
  const engine = { state: { luckyWheel: { pending: null }, party: [105], characters: { 105: { star: 5 } } }, phase: 'idle', mode: 'journey',
    rng() { draws++; return 0.5; }, dropBonus() { return 0.02; }, goldBonus() { return 0.1; },
    badgePartyBonus() { return 0.005; }, costumePartyBonus() { return 0.002; }, partyBondBonus() { return 0; },
    immortalDropChance() { return 0.05 + this.dropBonus(); },
    spinLuckyWheel() { return this.rng(); }, victory() { return this.rng(); }, drop() { return this.rng(); },
    afterStarHit() { return this.rng(); }, dropMineReward() { return this.rng(); }, dropOre() { return this.rng(); },
    cowDrop86(pool) { return pool[0]; } };
  let current = true;
  const session = createProbabilitySession(engine, model, { layout, archivePath: 'C:/game/app.asar', assertCurrent: () => current });
  return { engine, session, get draws() { return draws; }, invalidate: () => { current = false; } };
}
const wheel = { wheelBonus: { doublePercent: 25, marqueePercent: 0 } }, drops = { drops: { equipmentBonusPercent: 20 } };
test('scoped configuration replaces only its own namespace in either order', () => {
  for (const order of [['wheel', 'drops'], ['drops', 'wheel']]) {
    const f = fixture(), profiles = { wheel, drops };
    for (const key of order) f.session.configure(profiles[key], key);
    const report = f.session.inspect();
    assert.deepEqual(report.settings, { ...profiles[order[0]], ...profiles[order[1]] });
    assert.equal(report.namespaces.wheel.enabled, true); assert.equal(report.namespaces.drops.enabled, true);
    f.session.configure({ drops: { goldBonusPercent: 100 } }, 'drops');
    assert.deepEqual(f.session.inspect().settings, { ...wheel, drops: { goldBonusPercent: 100 } });
  }
});
test('scoped reset preserves other settings/hooks, last reset removes hooks', () => {
  for (const first of ['wheel', 'drops']) {
    const f = fixture(), native = f.engine.victory;
    f.session.configure(wheel, 'wheel'); f.session.configure(drops, 'drops');
    const installed = f.engine.victory;
    assert.equal(f.session.reset(first).restored, true);
    const second = first === 'wheel' ? 'drops' : 'wheel';
    const report = f.session.inspect();
    assert.equal(report.namespaces[first].enabled, false); assert.equal(report.namespaces[second].enabled, true);
    assert.equal(f.engine.victory, installed); assert.equal(report.installed, true);
    assert.equal(f.session.reset(second).restored, true);
    assert.equal(f.engine.victory, native); assert.equal(f.session.inspect().installed, false);
  }
});
test('unscoped legacy APIs still replace/reset the complete session', () => {
  const { session } = fixture(); session.configure(wheel, 'wheel'); session.configure(drops);
  assert.deepEqual(session.inspect().settings, drops); assert.equal(session.reset().restored, true);
});
test('namespace mix, unknown namespace and unavailable current consumers fail before mutation', () => {
  const f = fixture(), before = f.engine.victory;
  for (const [profile, namespace] of [[drops, 'wheel'], [wheel, 'drops'], [drops, 'unknown']]) assert.throws(() => f.session.configure(profile, namespace), /NAMESPACE/);
  assert.throws(() => f.session.reset('unknown'), /NAMESPACE/); assert.equal(f.engine.victory, before);
  const missingSites = createProbabilitySession(f.engine, model, { archivePath: 'C:/game/app.asar', sites: {}, assertCurrent: () => true });
  for (const profile of [{ upgradeBonusPercent: 0 }, { ordinaryEquipmentPercent: 100 }, { mine: { rewardPercent: 100 } }])
    assert.throws(() => missingSites.configure({ drops: profile }, 'drops'), /CONSUMER_NOT_SUPPORTED/);
  const { engine } = fixture(); delete engine.dropOre;
  const missing = createProbabilitySession(engine, model, { layout: 'standalone-0.116.70', archivePath: 'C:/game/app.asar', assertCurrent: () => true });
  assert.throws(() => missing.configure({ drops: { mine: { rewardPercent: 100 } } }), /CONTRACT_CHANGED/);
});
test('changing one namespace during pending/conflict/foreign owner does not drop the other', () => {
  for (const change of [f => { f.engine.phase = 'fighting'; }, f => { f.engine.state.luckyWheel.pending = {}; },
    f => { f.engine.victory = () => 99; }, f => f.invalidate()]) {
    const f = fixture(); f.session.configure(wheel, 'wheel'); f.session.configure(drops, 'drops'); change(f);
    assert.throws(() => f.session.reset('wheel'));
    assert.deepEqual(f.session.inspect().settings, { ...wheel, ...drops });
  }
});
test('native reports never include this module bonus; effective reports do', () => {
  const f = fixture(); f.engine.mode = 'abyss';
  f.session.configure({ drops: { equipmentBonusPercent: 20, goldBonusPercent: 100, upgradeBonusPercent: 5 } }, 'drops');
  const report = f.session.inspect();
  assert.equal(report.nativeDropBonus, 0.02); assert.equal(report.nativeImmortalChance, 0.07);
  assert.equal(report.effectiveDropBonus, 0.22); assert.equal(report.nativeGoldBonus, 0.1); assert.equal(report.effectiveGoldBonus, 1.1);
  assert.equal(report.nativeUpgradeChance, 0.007); assert.equal(report.effectiveUpgradeChance, 0.057);
  f.invalidate(); assert.equal(f.session.inspect().effectiveDropBonus, 0.02);
});
test('cow fixed rewards are scoped to cow and use independent common/elite gates', () => {
  const f = fixture(); f.session.configure({ drops: { cow: { ordinaryEquipmentPercent: 0, eliteEquipmentPercent: 100, eliteImmortalPercent: 0 } } }, 'drops');
  const pool = model.cowPools.gear;
  assert.equal(f.engine.cowDrop86(pool, 4), pool[0]); // wrong mode is native
  f.engine.mode = 'cow'; f.engine.enemy = { cow: true, cowElite: false, cowKing: false };
  assert.equal(f.engine.cowDrop86(pool, 4), undefined); assert.equal(f.draws, 0);
  f.engine.enemy.cowElite = true; assert.equal(f.engine.cowDrop86(pool, 4), pool[0]); assert.equal(f.draws, 0);
  assert.equal(f.engine.cowDrop86(model.cowPools.immortal, 5), undefined); assert.equal(f.draws, 0);
});
test('unconfigured cow hooks do not require an enemy/context/pool', () => {
  const f = fixture(); f.session.configure(wheel, 'wheel'); f.engine.mode = 'cow';
  assert.equal(f.engine.cowDrop86(['native-unknown-pool'], 4), 'native-unknown-pool');
  assert.equal(f.session.inspect().blocked, null); assert.equal(f.draws, 0);
});
test('cow midpoint gate consumes one draw, invalid RNG blocks; foreign pool is native', () => {
  const f = fixture(); f.session.configure({ drops: { cow: { ordinaryEquipmentPercent: 50 } } }, 'drops'); f.engine.mode = 'cow';
  f.engine.enemy = { cow: true, cowElite: false, cowKing: false };
  assert.equal(f.engine.cowDrop86(model.cowPools.gear, 4), undefined); assert.equal(f.draws, 1);
  assert.equal(f.engine.cowDrop86(['foreign'], 4), 'foreign'); assert.equal(f.session.inspect().blocked, 'COW_POOL_CONTRACT_CHANGED');
  const invalid = fixture(); invalid.engine.rng = () => NaN; invalid.engine.mode = 'cow'; invalid.engine.enemy = { cow: true, cowElite: false, cowKing: false };
  const control = createProbabilitySession(invalid.engine, model, { layout: 'standalone-0.116.70', archivePath: 'C:/game/app.asar', assertCurrent: () => true });
  control.configure({ drops: { cow: { ordinaryEquipmentPercent: 50 } } }, 'drops');
  assert.equal(invalid.engine.cowDrop86(model.cowPools.gear, 4), model.cowPools.gear[0]); assert.equal(control.inspect().blocked, 'RNG_VALUE_INVALID');
});
const valid = { drops: { equipmentBonusPercent: -100, upgradeBonusPercent: 100, goldBonusPercent: 1000, ordinaryEquipmentPercent: 100,
  mine: { rewardPercent: 1, stonePercent: 50, qualityPercent: { blue: 70, purple: 25, immortal: 4, mythic: 1 } },
  cow: { ordinaryEquipmentPercent: 25, eliteEquipmentPercent: 50, eliteImmortalPercent: 75,
    mythicPercent: { 'w:10007': 100 }, gearWeights: { 'w:14000': 100 }, immortalWeights: { 'w:903': 100 } } } };
test('complete drop model clones inputs with strict numeric/pool bounds', () => {
  const copy = model.validate(valid); assert.deepEqual(copy, valid); copy.drops.mine.rewardPercent = 99;
  assert.equal(valid.drops.mine.rewardPercent, 1);
});
for (const [index, drops] of [{ mine: {} }, { cow: {} }, { goldBonusPercent: 1001 }, { upgradeBonusPercent: -101 }, { ordinaryEquipmentPercent: NaN },
  { mine: { qualityPercent: { blue: 70, purple: 25, immortal: 4 } } }, { mine: { qualityPercent: { blue: 70, purple: 25, immortal: 4, mythic: 2 } } },
  { cow: { gearWeights: {} } }, { cow: { gearWeights: { 'w:14000': null } } }, { cow: { gearWeights: { foreign: 100 } } },
  { cow: { gearWeights: Object.fromEntries(model.cowPools.gear.map(key => [key, 0])) } }, { cow: { mythicPercent: { foreign: 10 } } },
  { mine: { rewardPercent: undefined } }, { goldBonusPercent: undefined }, { mine: { get rewardPercent() { throw Error('accessed'); } } }].entries())
  test('invalid drop profile rejected: ' + index, () => assert.throws(() => model.validate({ drops })));
