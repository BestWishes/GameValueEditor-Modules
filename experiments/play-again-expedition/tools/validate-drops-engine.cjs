'use strict';
// Independent native Engine only. No renderer, main, profile, attach or saving.
const assert = require('node:assert/strict'), path = require('node:path'), crypto = require('node:crypto');
const fs = require('original-fs');
const [archive, fixturePath] = process.argv.slice(2);
assert.equal(process.env.ELECTRON_RUN_AS_NODE, '1');
assert.equal(process.versions.electron, '44.2.0'); assert.equal(process.versions.node, '24.20.0'); assert.equal(process.arch, 'x64');
assert.equal(crypto.createHash('sha256').update(fs.readFileSync(archive)).digest('hex'), '5a4f4e414bd06fc248cfe2ad30e7d9ee3b110968db888fdb733cd57f78a6b294');
assert.equal(path.resolve(fixturePath).toLowerCase(), path.resolve(__dirname,
  '../../../artifacts/play-again-expedition/20261007-213142-offline-recovery/restore-protection-scrolls-2.json').toLowerCase());
Object.defineProperty(process, 'type', { value: 'browser', configurable: true });
const { Engine, DATA } = require(path.join(archive, 'runtime/src/engine.js'));
const cow = require(path.join(archive, 'runtime/src/cow-dungeon.js'));
const { createProbabilityModel } = require('../src/probability-model.cjs');
const { createProbabilitySession } = require('../src/probability-session.cjs');
const fixture = fs.readFileSync(fixturePath, 'utf8'), model = createProbabilityModel();
let checks = 0;
const check = (condition, message) => { checks++; assert.ok(condition, message); };
const make = (rng = () => 0.5) => new Engine(fixture, rng);
const manager = engine => createProbabilitySession(engine, model, { archivePath: archive, layout: 'standalone-0.116.70', assertCurrent: () => true });
const newly = (engine, before) => engine.state.inventory.filter(item => !before.has(item.uid));
const ids = engine => new Set(engine.state.inventory.map(item => item.uid));
const randomSite = column => (Error().stack.split('\n').find(frame => /\.asar[\\/]runtime[\\/]src[\\/]/.test(frame)) ?? '').includes('engine.js.jsc:1:' + column + ')');
function controlled(engine, profile, action) {
  const control = manager(engine), before = engine.serialize(), rng = engine.rng;
  control.configure({ drops: profile }, 'drops'); check(engine.serialize() === before, 'configure changed fixture save');
  const result = action(engine), report = control.inspect();
  check(report.blocked === null && engine.rng === rng, 'hook blocked/RNG not restored: ' + report.blocked);
  return { engine, control, result, report };
}
function cowStart(e, wave) {
  e.state.cleared = 400; e.state.materials.cowTickets = 10;
  const worn = new Set(e.state.equipped.flatMap(Object.values));
  e.state.inventory = e.state.inventory.filter(item => worn.has(item.uid));
  check(e.startCowDungeon(), 'cow start failed'); e.wave = wave; e.spawn();
}
check(JSON.stringify(cow.GEAR) === JSON.stringify(model.cowPools.gear), 'cow gear pool changed');
check(JSON.stringify(cow.IMMORTALS) === JSON.stringify(model.cowPools.immortal), 'cow immortal pool changed');
check(JSON.stringify(cow.MYTHICS) === JSON.stringify(model.cowPools.mythic), 'cow mythic pool changed');
const baseCases = [];
for (const value of [0.299, 0.301, 0.399, 0.401]) {
  const e = make(function () { return randomSite(83849) ? value : 0.5; });
  let dropCalls = 0; const originalDrop = e.drop; e.drop = function (...args) { dropCalls++; return originalDrop.apply(this, args); };
  e.dropBonus = () => 0; e.luckyBonus = () => 0; e.mode = 'journey'; e.start(); const before = ids(e);
  e.enemy.hp = 0; e.victory(); baseCases.push({ value, dropCalls, items: newly(e, before).length });
}
const getterSample = make(); getterSample.mode = 'journey'; getterSample.start();
const thresholds = [];
for (const lucky of [null, 0, 0.01, 0.1, 1]) {
  let low = 0, high = 1;
  for (let step = 0; step < 14; step++) {
    const value = (low + high) / 2, e = make(function () { return randomSite(83849) ? value : 0.5; });
    let calls = 0; const drop = e.drop; e.drop = function (...args) { calls++; return drop.apply(this, args); };
    e.dropBonus = () => 0; if (lucky !== null) e.luckyBonus = () => lucky;
    e.mode = 'journey'; e.start(); e.enemy.hp = 0; e.victory(); if (calls) low = value; else high = value;
  }
  thresholds.push({ lucky, low, high });
}
check(thresholds.every(row => row.low < 0.38 && row.high >= 0.38), 'native ordinary base is not 38%');
check(baseCases[0].dropCalls === 1 && baseCases[3].dropCalls === 0, 'native ordinary gate not confirmed');
for (const tier of [5, 25, 50, 75, 100]) for (const value of [0.38 - 1e-9, 0.38 + 1e-9]) {
  const e = make(function () { return randomSite(83849) ? value : 0.5; });
  e.state.cleared = 400; e.state.selected = tier; e.dropBonus = () => 0; e.partyBondBonus = () => 0;
  let called = 0; const drop = e.drop; e.drop = function (...args) { called++; return drop.apply(this, args); };
  e.mode = 'journey';
  check(e.start() && e.enemy, 'ordinary fixture tier unavailable: ' + tier + '; DATA=' + JSON.stringify(Object.keys(DATA)));
  e.enemy.hp = 0; e.victory();
  check(called === (value < 0.38 ? 1 : 0), 'ordinary native base changed by tier');
}

// Aggregate controls are not save writes and final override has priority.
for (const percent of [0, 25, 100]) {
  const e = make(() => 0.5);
  const tested = controlled(e, { equipmentBonusPercent: 100, ordinaryEquipmentPercent: percent }, copy => {
    copy.mode = 'journey'; copy.start(); copy.enemy.hp = 0; const before = ids(copy); copy.victory(); return newly(copy, before);
  });
  check(tested.result.length === (percent > 50 ? 1 : 0), 'ordinary final target did not override aggregate');
  check(tested.report.hits.ordinaryEquipment === 1, 'ordinary getter not consumed');
}
for (const bonus of [-100, 100]) {
  const tested = controlled(make(), { upgradeBonusPercent: bonus }, copy => {
    copy.mode = 'journey'; copy.start(); const before = ids(copy); copy.drop(copy.enemy, 100); return newly(copy, before)[0];
  });
  check(tested.result?.quality === (bonus > 0 ? 4 : 3), 'upgrade did not reach actual new gear');
  check(tested.report.hits.equipmentUpgrade === 1, 'upgrade getter not consumed');
}
for (const [value, promoted] of [[0.005, true], [0.107 - 1e-9, true], [0.107 + 1e-9, false], [0.9, false]]) {
  const tested = controlled(make(function () { return randomSite(87767) ? value : 0.5; }),
    { upgradeBonusPercent: 10 }, copy => {
      copy.mode = 'journey'; copy.start(); const before = ids(copy); copy.drop(copy.enemy, 100); return newly(copy, before)[0];
    });
  check(tested.result?.quality === (promoted ? 4 : 3), 'upgrade intermediate boundary failed');
}
for (const percent of [25, 80]) {
  let awarded = 0; const outcomes = [];
  for (let i = 0; i < 20; i++) {
    const value = (i + 0.5) / 20;
    const tested = controlled(make(function () { return randomSite(83849) ? value : 0.5; }),
      { equipmentBonusPercent: -100, ordinaryEquipmentPercent: percent }, copy => {
        copy.mode = 'journey'; copy.start(); const before = ids(copy); copy.enemy.hp = 0; copy.victory(); return newly(copy, before).length;
      });
    awarded += tested.result;
    if (tested.result) outcomes.push({ value, count: tested.result, hits: tested.report.hits });
  }
  check(awarded === percent / 5, 'ordinary intermediate final probability failed: ' + JSON.stringify({ percent, awarded, outcomes }));
}
for (const bond of [0.2, 1, 10]) for (const [value, awarded] of [[0.249, true], [0.251, false]]) {
  const e = make(function () { return randomSite(83849) ? value : 0.5; });
  e.partyBondBonus = () => bond;
  const tested = controlled(e, { equipmentBonusPercent: 100, ordinaryEquipmentPercent: 25 }, copy => {
    copy.mode = 'journey'; copy.start(); const before = ids(copy); copy.enemy.hp = 0; copy.victory(); return newly(copy, before);
  });
  check(tested.result.length === (awarded ? 1 : 0), 'final ordinary target must include/cancel party bond');
}
for (const mode of ['journey', 'abyss', 'cow']) {
  const run = profile => controlled(make(), profile, copy => {
    if (mode === 'cow') cowStart(copy, 0); else { copy.mode = mode; copy.start(); }
    copy.enemy.hp = 0; const before = copy.state.gold; copy.victory(); return copy.state.gold - before;
  });
  const baseline = run({ goldBonusPercent: 0 }), extra = run({ goldBonusPercent: 100 });
  const native = baseline.report.nativeGoldBonus;
  check(Math.abs(extra.result / baseline.result - (2 + native) / (1 + native)) < 0.002, 'gold extra should add to base, not double current bonus');
  check(extra.report.hits.goldBonus > 0, 'gold getter not consumed');
}

// Mining uses native creation, hero-sensitive 60/50% source and reverse quality CDF.
for (const percent of [0, 100]) {
  const tested = controlled(make(), { mine: { stonePercent: percent } }, copy => {
    copy.state.cleared = 400; copy.startMine(150); const before = copy.state.materials.stones;
    copy.dropMineReward(2); return { stones: copy.state.materials.stones - before, ores: copy.state.ores.length };
  });
  check(percent === 100 ? tested.result.stones > 0 : tested.result.stones === 0, 'mine stone/ore distribution failed');
  check(tested.report.hits.mineDistribution === 1, 'mine distribution site not hit');
}
for (const [key, quality] of [['blue', 3], ['purple', 4], ['immortal', 5], ['mythic', 6]]) {
  const tested = controlled(make(), { mine: { stonePercent: 0, qualityPercent: Object.fromEntries(['blue', 'purple', 'immortal', 'mythic'].map(name => [name, name === key ? 100 : 0])) } }, copy => {
    copy.state.cleared = 400; copy.startMine(150); const before = copy.state.ores.length; copy.dropMineReward(2); return copy.state.ores.slice(before);
  });
  check(tested.result.length === 1 && tested.result[0].quality === quality, 'actual ore quality failed: ' + key);
  check(tested.report.hits.mineQuality === 1, 'mine quality site not consumed');
}
for (const star of [4, 5]) {
  const e = make(() => 0.55); e.state.party[0] = 105; e.state.characters[105].star = star;
  const tested = controlled(e, { mine: { stonePercent: 60 } }, copy => {
    copy.state.cleared = 400; copy.startMine(150); const before = copy.state.materials.stones; copy.dropMineReward(0); return copy.state.materials.stones - before;
  });
  check(tested.result > 0, 'worker source distribution failed: ' + star);
}
for (const percent of [0, 100]) {
  const tested = controlled(make(), { mine: { rewardPercent: percent, stonePercent: 100 } }, copy => {
    copy.state.cleared = 400; copy.startMine(150); const before = copy.state.materials.stones;
    for (let i = 0; i < 80; i++) copy.tick(0.1); return copy.state.materials.stones - before;
  });
  check(percent === 100 ? tested.result > 0 : tested.result === 0, 'natural tick mining trigger failed');
  check(tested.report.hits.mineReward > 0, 'natural hit not consumed');
}
const filtered = controlled(make(), { mine: { rewardPercent: 100, stonePercent: 100 } }, copy => {
  copy.state.cleared = 400; copy.startMine(150); const before = copy.state.materials.stones;
  copy.afterStarHit(2, 3766, false, false, { noMining: true }); return copy.state.materials.stones - before;
});
check(filtered.result === 0 && !filtered.report.hits.mineReward, 'native noMining filter must remain authoritative');

// Cow common/elite guaranteed reward gates are independent. Gold and stones remain native.
for (const wave of [0, 2]) for (const percent of [0, 100]) {
  const tested = controlled(make(), { cow: { ordinaryEquipmentPercent: percent, eliteEquipmentPercent: percent, eliteImmortalPercent: percent } }, copy => {
    cowStart(copy, wave); const before = ids(copy); copy.enemy.hp = 0; copy.victory(); return newly(copy, before);
  });
  check(tested.result.length === (percent === 100 ? wave === 2 ? 2 : 1 : 0), 'cow fixed gate failed');
}
for (const [common, immortal] of [[100, 0], [0, 100]]) {
  const tested = controlled(make(), { cow: { ordinaryEquipmentPercent: 100 - common, eliteEquipmentPercent: common, eliteImmortalPercent: immortal } }, copy => {
    cowStart(copy, 2); const before = ids(copy); copy.enemy.hp = 0; copy.victory(); return newly(copy, before);
  });
  check(tested.result.length === 1 && tested.result[0].quality === (common === 100 ? 4 : 5), 'elite/common gates were confused');
}
for (const percent of [0, 100]) {
  const tested = controlled(make(), { cow: { mythicPercent: Object.fromEntries(model.cowPools.mythic.map(key => [key, percent])) } }, copy => {
    cowStart(copy, 9); const before = ids(copy); copy.enemy.hp = 0; copy.victory(); return newly(copy, before);
  });
  check(tested.result.length === (percent === 100 ? 3 : 0), 'cow king independent probability failed');
  check(model.cowPools.mythic.every(key => tested.report.hits['cowMythic.' + key] === 1), 'cow king rolls incomplete: ' + JSON.stringify(tested.report.hits));
}
for (const target of model.cowPools.mythic) {
  const tested = controlled(make(), { cow: { mythicPercent: Object.fromEntries(model.cowPools.mythic.map(key => [key, key === target ? 100 : 0])) } }, copy => {
    cowStart(copy, 9); const before = ids(copy); copy.enemy.hp = 0; copy.victory(); return newly(copy, before);
  });
  check(tested.result.length === 1 && tested.result[0].key === target, 'cow king targets not independent');
}
for (const kind of ['gear', 'immortal']) {
  const pool = model.cowPools[kind];
  for (const key of [pool[0], pool.at(-1)]) {
    const tested = controlled(make(), { cow: { [kind + 'Weights']: Object.fromEntries(pool.map(item => [item, item === key ? 100 : 0])) } }, copy => {
      cowStart(copy, kind === 'gear' ? 0 : 2); const before = ids(copy); copy.enemy.hp = 0; copy.victory(); return newly(copy, before);
    });
    check(tested.result.some(item => item.key === key && item.quality === (kind === 'gear' ? 4 : 5)), 'cow pool selector failed');
  }
}

// Explicit defaults preserve native serial data and random consumption.
for (const scenario of ['cow', 'mine', 'journey']) {
  let plainDraws = 0, hookedDraws = 0;
  const plain = make(() => { plainDraws++; return 0.5; }), copy = make(() => { hookedDraws++; return 0.5; });
  const control = manager(copy);
  control.configure({ drops: { equipmentBonusPercent: 0, upgradeBonusPercent: 0, goldBonusPercent: 0,
    mine: { rewardPercent: 1, stonePercent: 60, qualityPercent: { blue: 70, purple: 25, immortal: 4, mythic: 1 } },
    cow: { ordinaryEquipmentPercent: 100, eliteEquipmentPercent: 100, eliteImmortalPercent: 100, mythicPercent: Object.fromEntries(model.cowPools.mythic.map(key => [key, 0.5])) } } }, 'drops');
  for (const e of [plain, copy]) {
    if (scenario === 'cow') { cowStart(e, 2); e.enemy.hp = 0; e.victory(); }
    else if (scenario === 'mine') { e.state.cleared = 400; e.startMine(150); e.dropMineReward(2); }
    else { e.mode = 'journey'; e.start(); e.enemy.hp = 0; e.victory(); }
  }
  check(plain.serialize() === copy.serialize() && plainDraws === hookedDraws, 'explicit defaults altered native ' + scenario);
  check(control.inspect().blocked === null, 'default native hooks blocked');
}
console.log(JSON.stringify({ exactBuild: '0.116.70', layout: 'independent-runtime', passedChecks: checks,
  nativeOrdinaryBasePercent: 38,
  liveGameAttached: false, saveWritten: false, productionBundleValidated: false }));
