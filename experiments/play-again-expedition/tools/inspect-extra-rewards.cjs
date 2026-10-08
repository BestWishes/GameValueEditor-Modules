'use strict';
// Exact-build pure module research; no original profile, main/preload or writes.
const assert = require('node:assert/strict'), path = require('node:path'), crypto = require('node:crypto');
const fs = require('original-fs');
const [archive, fixturePath] = process.argv.slice(2);
assert.equal(process.env.ELECTRON_RUN_AS_NODE, '1'); assert.equal(process.versions.electron, '44.2.0'); assert.equal(process.arch, 'x64');
assert.equal(crypto.createHash('sha256').update(fs.readFileSync(archive)).digest('hex'), '5a4f4e414bd06fc248cfe2ad30e7d9ee3b110968db888fdb733cd57f78a6b294');
assert.equal(path.resolve(fixturePath).toLowerCase(), path.resolve(__dirname,
  '../../../artifacts/play-again-expedition/20261007-213142-offline-recovery/restore-protection-scrolls-2.json').toLowerCase());
Object.defineProperty(process, 'type', { value: 'browser', configurable: true });
const { Engine, DATA } = require(path.join(archive, 'runtime/src/engine.js'));
const fixture = fs.readFileSync(fixturePath, 'utf8'), base = new Engine(fixture, () => 0.5);
const modules = {};
const nativeModules = {};
for (const name of ['cow-dungeon', 'cow-farming', 'flame-throne', 'flame-throne-data', 'flame-mythics', 'flame-immortals', 'offline-rewards']) {
  const exported = require(path.join(archive, 'runtime/src/' + name + '.js'));
  nativeModules[name] = exported;
  modules[name] = Object.fromEntries(Object.entries(exported).map(([key, value]) => [key,
    typeof value === 'function' ? { name: value.name, args: value.length } : typeof value !== 'object' ? value : { keys: Object.keys(value ?? {}).slice(0, 20) }]));
}
function probe(method, args, mode) {
  const e = new Engine(fixture, () => 0.5); e.mode = mode; const calls = [];
  e.rng = () => { calls.push(Error().stack.split('\n')[2]?.trim()); return 0.5; };
  const before = JSON.parse(e.serialize()), uids = new Set(before.inventory.map(item => item.uid));
  try {
    e[method](...args); const after = JSON.parse(e.serialize());
    return { method, args, mode, calls: [...new Set(calls)],
      newItems: after.inventory.filter(item => !uids.has(item.uid)).map(item => ({ key: item.key, quality: item.quality })),
      changedKeys: Object.keys(after).filter(key => JSON.stringify(after[key]) !== JSON.stringify(before[key])),
      materialChanges: Object.fromEntries(Object.keys(after.materials).filter(key => after.materials[key] !== before.materials[key]).map(key => [key, after.materials[key] - before.materials[key]])) };
  } catch (error) { return { method, error: error.message }; }
}
const probes = [probe('dropOre', [], 'journey'), probe('dropMineReward', [1], 'journey'),
  probe('dropMineReward', [{ tier: 150, id: 13000, stones: [1, 5] }], 'mine'),
  probe('dropFlameImmortals', [25400], 'flame'), probe('dropFlameImmortals', [{ id: 25400 }], 'flame'),
  probe('cowDrop86', [1, 100], 'cow')];
const tables = {};
for (const [key, action] of Object.entries({
  cowDrops: () => [12200, 12201, 12205, 12206].map(id => ({ id, rows: nativeModules['cow-dungeon'].itemDrops({ id }) })),
  flameDrops: () => [25400, 25401, 25404].map(id => ({ id, rows: nativeModules['flame-throne-data'].itemDrops({ id }) })),
  flameChance: () => nativeModules['flame-immortals'].dropChance(base),
  flameChanceCases: () => [-0.5, 0, 0.01, 0.1, 1].map(bonus => {
    const e = new Engine(fixture, () => 0.5); e.dropBonus = () => bonus;
    return { bonus, rate: nativeModules['flame-immortals'].dropChance(e) };
  }),
  flameChanceArguments: () => [undefined, null, 0, 1, 5, 100, base].map(value => {
    try { return { type: typeof value, value: typeof value === 'number' ? value : null, chance: nativeModules['flame-immortals'].dropChance(value) }; }
    catch (error) { return { type: typeof value, error: error.message }; }
  }),
  flameChanceMythics: () => [false, true, 0, 0.001, 0.05, 1, 5, 100].map(mythic => ({ mythic,
    chance: nativeModules['flame-immortals'].dropChance({ mythic }) })),
  stateKeys: () => Object.keys(base.state),
  mythic: () => base.mythicDrops(),
  oreDefinition: () => base.mineDefinition(),
  cowExamples: () => ({ gear: nativeModules['cow-dungeon'].GEAR.slice(0, 2), mythics: nativeModules['cow-dungeon'].MYTHICS, immortals: nativeModules['cow-dungeon'].IMMORTALS.slice(0, 2) }),
  flamePools: () => nativeModules['flame-immortals'].POOLS,
  flamePoolRates: () => Object.fromEntries(Object.entries(nativeModules['flame-immortals'].POOLS).map(([id, keys]) => [id, keys.map(key => ({ key,
    rowType: typeof DATA.gear[key], quality: DATA.gear[key]?.quality, mythic: DATA.gear[key]?.mythic,
    chance: nativeModules['flame-immortals'].dropChance(DATA.gear[key]) }))])),
  coward: () => { const result = base.startCowDungeon(); return { result, mode: base.mode, phase: base.phase, enemy: base.enemy?.id }; },
  flame: () => { const e = new Engine(fixture, () => 0.5), result = e.startFlameThrone(); return { result, mode: e.mode, phase: e.phase, enemy: e.enemy?.id }; },
})) { try { tables[key] = action(); } catch (error) { tables[key] = { error: error.message }; } }
const victoryBranches = [];
for (const kind of ['mine', 'cow', 'flame', 'mythic']) {
  const e = new Engine(fixture, () => 0.5);
  e.state.cleared = 400; e.state.selected = 100;
  if (kind === 'cow') e.state.materials.cowTickets = 10;
  if (kind === 'flame') e.state.materials.flameKeys = 10;
  const calls = [];
  e.rng = () => { calls.push(Error().stack.split('\n')[2]?.trim()); return 0; };
  try {
    const start = kind === 'mine' ? e.startMine(150) : kind === 'cow' ? e.startCowDungeon() : kind === 'flame' ? e.startFlameThrone() : (e.mode = 'journey', e.start());
    const before = JSON.parse(e.serialize()); const enemyId = e.enemy?.id;
    if (kind === 'cow') tables.cowEngineDrops = nativeModules['cow-dungeon'].itemDrops(e);
    if (kind === 'flame') tables.flameEngineDrops = nativeModules['flame-throne-data'].itemDrops(e);
    if (kind === 'flame') tables.flameTargets = { properties: Object.keys(e).filter(key => /flame/i.test(key)), living: e.flameLiving(),
      enemies: e.flameEnemies.map(row => ({ keys: Object.keys(row), id: row.id, enemyId: row.enemy?.id, boss: row.boss, hp: row.hp, drops: row.drops })) };
    if (e.enemy) { e.enemy.hp = 0; e.victory(); }
    const after = JSON.parse(e.serialize());
    victoryBranches.push({ kind, start, mode: e.mode, enemyId, calls: [...new Set(calls)],
      materialChanges: Object.fromEntries(Object.keys(after.materials).filter(key => after.materials[key] !== before.materials[key]).map(key => [key, after.materials[key] - before.materials[key]])),
      newItems: after.inventory.filter(item => !before.inventory.some(old => old.uid === item.uid)).map(item => ({ key: item.key, quality: item.quality })),
      changedKeys: Object.keys(after).filter(key => JSON.stringify(after[key]) !== JSON.stringify(before[key])) });
  } catch (error) { victoryBranches.push({ kind, error: error.message }); }
}
const materialRolls = [];
for (const value of [0, 0.005, 0.01, 0.05, 0.1, 0.49, 0.5, 0.99]) {
  const e = new Engine(fixture, () => 0.5); e.mode = 'abyss'; e.start(); e.enemy.hp = 0;
  e.rng = () => Error().stack.split('\n')[2]?.includes('engine.js.jsc:1:82518)') ? value : 0.5;
  const before = JSON.parse(e.serialize()); e.victory(); const after = JSON.parse(e.serialize());
  materialRolls.push({ value, changes: Object.fromEntries(Object.keys(after.materials).filter(key => after.materials[key] !== before.materials[key]).map(key => [key, after.materials[key] - before.materials[key]])) });
}
const cowRolls = [];
for (const value of [0, 0.01, 0.1, 0.2, 0.3, 0.4, 0.5, 0.9, 0.99]) {
  const e = new Engine(fixture, () => 0.5); e.state.cleared = 400; e.state.materials.cowTickets = 10; e.startCowDungeon(); e.enemy.hp = 0;
  const calls = []; e.rng = () => { const frame = Error().stack.split('\n')[2]?.trim(); calls.push(frame);
    return frame.includes('cow-dungeon.js.jsc:1:6268)') ? value : 0.5; };
  const before = JSON.parse(e.serialize()); e.victory(); const after = JSON.parse(e.serialize());
  cowRolls.push({ value, calls: [...new Set(calls)].filter(line => /cow-dungeon/.test(line)),
    newItems: after.inventory.filter(item => !before.inventory.some(old => old.uid === item.uid)).map(item => ({ key: item.key, quality: item.quality })) });
}
const qualityThresholds = [];
for (const site of [87593, 87767]) for (const bonus of [0, 0.01, 0.1, 1]) {
  let low = 0, high = 1;
  for (let i = 0; i < 22; i++) {
    const value = (low + high) / 2, e = new Engine(fixture, () => 0.5); e.mode = 'journey'; e.start();
    e.luckyBonus = () => bonus;
    e.rng = () => Error().stack.split('\n')[2]?.includes('engine.js.jsc:1:' + site + ')') ? value : 0.5;
    const uids = new Set(e.state.inventory.map(item => item.uid)); e.drop(e.enemy, 100);
    if (e.state.inventory.find(item => !uids.has(item.uid))?.quality === 4) low = value; else high = value;
  }
  qualityThresholds.push({ site, luckyBonus: bonus, low, high });
}
console.log(JSON.stringify({ modules, tables, victoryBranches, materialRolls, cowRolls, prototypes: Object.getOwnPropertyNames(Engine.prototype).filter(name => /flame|mine|cow|mythic/i.test(name)),
  states: { mine: base.state.mine, cow: base.state.cow, flame: base.state.flame, mythic: base.state.mythic }, probes, qualityThresholds }));
