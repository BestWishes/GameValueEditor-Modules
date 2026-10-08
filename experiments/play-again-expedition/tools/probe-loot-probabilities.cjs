'use strict';
// Pure Engine fixtures only. Never attach to the game or read the original profile.
const assert = require('node:assert/strict');
const path = require('node:path');
const fs = require('original-fs');
const crypto = require('node:crypto');
const [archive, fixturePath] = process.argv.slice(2);
assert.equal(process.env.ELECTRON_RUN_AS_NODE, '1');
assert.equal(process.versions.electron, '44.2.0'); assert.equal(process.versions.node, '24.20.0'); assert.equal(process.arch, 'x64');
assert.equal(crypto.createHash('sha256').update(fs.readFileSync(archive)).digest('hex'), '5a4f4e414bd06fc248cfe2ad30e7d9ee3b110968db888fdb733cd57f78a6b294');
assert.equal(path.resolve(fixturePath).toLowerCase(), path.resolve(__dirname,
  '../../../artifacts/play-again-expedition/20261007-213142-offline-recovery/restore-protection-scrolls-2.json').toLowerCase());
Object.defineProperty(process, 'type', { value: 'browser', configurable: true });
const { Engine, ABYSSES } = require(path.join(archive, 'runtime/src/engine.js'));
const fixture = fs.readFileSync(fixturePath, 'utf8');
function probe(mode, site, value, bonus, lucky) {
  const engine = new Engine(fixture, () => 0.5); engine.mode = mode; engine.start(); engine.enemy.hp = 0;
  if (typeof bonus === 'number') engine.dropBonus = () => bonus;
  if (typeof lucky === 'number') engine.luckyBonus = () => lucky;
  const before = JSON.parse(engine.serialize()), beforeUIDs = new Set(before.inventory.map(item => item.uid));
  const hitSites = [];
  engine.rng = () => {
    const caller = Error().stack.split('\n')[2];
    const match = caller.match(/engine\.js\.jsc:1:(\d+)\)/);
    if (match) hitSites.push(Number(match[1]));
    return caller.includes('engine.js.jsc:1:' + site + ')') ? value : 0.5;
  };
  engine.victory(); const after = JSON.parse(engine.serialize());
  return { mode, site, value, bonus, hitSites: [...new Set(hitSites)],
    newItems: after.inventory.filter(item => !beforeUIDs.has(item.uid)).map(item => ({ key: item.key, quality: item.quality, relic: item.relic, promoted: item.promoted, qualityBeforePromotion: item.qualityBeforePromotion })),
    ticketsAdded: after.abyss.tickets - before.abyss.tickets,
    materialChanges: Object.fromEntries(Object.keys(after.materials).filter(key => after.materials[key] !== before.materials[key])
      .map(key => [key, after.materials[key] - before.materials[key]])),
    changedKeys: Object.keys(after).filter(key => JSON.stringify(after[key]) !== JSON.stringify(before[key])) };
}
const results = [];
for (const [mode, site] of [['journey', 83849], ['guardian', 84076], ['abyss', 82172], ['abyss', 82518]]) {
  for (const value of [0, 0.005, 0.01, 0.05, 0.07, 0.071, 0.1, 0.2, 0.3, 0.311, 0.4, 0.99]) results.push(probe(mode, site, value));
}
const bonusProbes = [-0.3, 0, 0.1, 1].map(bonus => ({ bonus, cases: [0, 0.29, 0.3, 0.39, 0.4, 0.99].map(value => probe('journey', 83849, value, bonus).newItems.length) }));
const prototype = new Engine(fixture, () => 0.5);
function threshold(mode, site, bonus, lucky, predicate) {
  let low = 0, high = 1;
  for (let index = 0; index < 25; index++) {
    const value = (low + high) / 2;
    if (predicate(probe(mode, site, value, bonus, lucky))) low = value; else high = value;
  }
  return { mode, site, bonus, lucky, low, high };
}
const thresholds = [
  ...[-0.3, 0, 0.1, 0.7, 1].map(bonus => threshold('journey', 83849, bonus, 0, result => result.newItems.length > 0)),
  threshold('journey', 83849, 0, 0.1, result => result.newItems.length > 0),
  threshold('journey', 83849, undefined, undefined, result => result.newItems.length > 0),
  threshold('guardian', 84076, undefined, undefined, result => result.ticketsAdded > 0),
  threshold('abyss', 82172, undefined, undefined, result => result.newItems.length > 0),
];
console.log(JSON.stringify({ abyssState: prototype.state.abyss, abyssConfig: prototype.abyssConfig(), ABYSSES, results, bonusProbes,
  getters: { dropBonus: prototype.dropBonus(), luckyBonus: prototype.luckyBonus(), immortalDropChance: prototype.immortalDropChance() }, thresholds }, null, 2));
