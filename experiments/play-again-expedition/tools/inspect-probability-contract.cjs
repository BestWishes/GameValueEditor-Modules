'use strict';
// Exact-build, headless pure modules only. No live attach, profile or save writes.
const assert = require('node:assert/strict');
const path = require('node:path');
const crypto = require('node:crypto');
const fs = require('original-fs');
const archive = process.argv[2];
assert.equal(process.env.ELECTRON_RUN_AS_NODE, '1');
assert.equal(process.versions.electron, '44.2.0');
assert.equal(process.versions.node, '24.20.0');
assert.equal(process.arch, 'x64');
assert.equal(crypto.createHash('sha256').update(fs.readFileSync(archive)).digest('hex'),
  '5a4f4e414bd06fc248cfe2ad30e7d9ee3b110968db888fdb733cd57f78a6b294');
Object.defineProperty(process, 'type', { value: 'browser', configurable: true });
const modules = ['lucky-wheel', 'probability-notes', 'abyss-immortal-loot', 'engine'];
const describe = (value, depth = 0) => {
  if (typeof value === 'function') return { functionName: value.name, arguments: value.length };
  if (value === null || typeof value !== 'object') return value;
  if (depth >= 5) return { type: Array.isArray(value) ? 'array' : 'object', keys: Object.keys(value).slice(0, 60) };
  const keys = Object.keys(value).slice(0, 100);
  return Object.fromEntries(keys.map(key => [key, describe(value[key], depth + 1)]));
};
const output = {};
for (const name of modules) {
  const module = require(path.join(archive, 'runtime/src/' + name + '.js'));
  output[name] = name === 'engine' ? { exports: Object.keys(module), dataKeys: Object.keys(module.DATA) } :
    name === 'abyss-immortal-loot' ? { exports: Object.keys(module), poolCount: Object.keys(module.POOLS).length } : describe(module);
  if (name === 'engine') {
    const copiedSave = process.argv[3];
    if (copiedSave) assert.equal(path.resolve(copiedSave).toLowerCase(), path.resolve(__dirname,
      '../../../artifacts/play-again-expedition/20261007-213142-offline-recovery/restore-protection-scrolls-2.json').toLowerCase());
    const engine = new module.Engine(copiedSave ? fs.readFileSync(copiedSave, 'utf8') : null, () => 0.5);
    output.enginePrototype = Object.getOwnPropertyNames(module.Engine.prototype).filter(name => /drop|loot|wheel|reward|victory|rate|bonus/i.test(name)).map(name => ({
      name, type: typeof Object.getOwnPropertyDescriptor(module.Engine.prototype, name).value,
      arguments: Object.getOwnPropertyDescriptor(module.Engine.prototype, name).value?.length,
    }));
    output.engineProperties = { keys: Object.keys(engine), mode: engine.mode, phase: engine.phase,
      stateKeys: Object.keys(engine.state), wheelBefore: describe(engine.state.luckyWheel) };
    const wheel = require(path.join(archive, 'runtime/src/lucky-wheel.js'));
    const probe = (label, action) => { try { output[label] = action(); } catch (error) { output[label] = { error: error.message,
      callers: error.stack.split('\n').slice(1, 6).map(line => line.trim()) }; } };
    probe('wheelContext', () => describe(wheel.wheelContext(engine)));
    probe('wheelPools', () => describe(wheel.wheelPools(engine)));
    probe('wheelPrepare', () => ({ result: engine.prepareLuckyWheel(), wheel: describe(engine.state.luckyWheel) }));
    probe('wheelWeights', () => wheel.wheelWeights(engine.state.luckyWheel.board));
    probe('wheelSpin', () => {
      const calls = [];
      engine.rng = () => { calls.push(Error().stack.split('\n').slice(2, 7).map(line => line.trim().replace(/ \(.+$/, ''))); return 0.5; };
      const before = engine.serialize(); const result = engine.spinLuckyWheel();
      return { result, coinBefore: JSON.parse(before).materials.wheelTickets,
        coinAfter: engine.state.materials.wheelTickets, callCount: calls.length,
        callers: [...new Set(calls.map(call => call.join(' > ')))],
        awarded: engine.luckyWheelResults().map(row => ({ kind: row.prize.kind, key: row.prize.key, quality: row.prize.quality })) };
    });
    probe('wheelBonus', () => [0, 0.01, 0.1, 0.25, 0.5, 0.75, 0.9, 0.99].map(value => [value, wheel.rollWheelBonus(() => value)]));
    probe('dropBonus', () => engine.dropBonus());
    for (const method of ['immortalDropChance', 'drop', 'dropMythic', 'dropOre', 'victory']) {
      probe(method + 'Trace', () => {
        const current = new module.Engine(copiedSave ? fs.readFileSync(copiedSave, 'utf8') : null, () => 0.5);
        if (method !== 'immortalDropChance') { current.mode = 'journey'; current.start(); }
        if (method === 'victory') current.enemy.hp = 0;
        const calls = [];
        const methodCalls = [];
        if (method === 'victory') for (const name of ['drop', 'dropMythic', 'ordinaryDrops', 'abyssDrops', 'dropOre']) {
          const original = current[name];
          current[name] = function (...args) { methodCalls.push({ name, args: describe(args) }); return Reflect.apply(original, this, args); };
        }
        current.rng = () => { calls.push(Error().stack.split('\n').slice(2, 7).map(line => line.trim().replace(/ \(.+$/, ''))); return 0.5; };
        const before = JSON.parse(current.serialize());
        const result = ['drop', 'dropMythic'].includes(method) ? current[method](current.enemy, current.state.selected) : current[method]();
        const after = JSON.parse(current.serialize());
        return { result, mode: current.mode, phase: current.phase, selected: current.state.selected,
          guardianSelected: current.state.guardianSelected, enemyId: current.enemy?.id,
          methodCalls, callCount: calls.length, callers: [...new Set(calls.map(call => call.join(' > ')))],
          changedRootKeys: Object.keys(after).filter(key => JSON.stringify(after[key]) !== JSON.stringify(before[key])) };
      });
    }
    output.bonusIntervals = [];
    for (let index = 0; index < 10000; index++) {
      const value = index / 10000, result = wheel.rollWheelBonus(() => value);
      const previous = output.bonusIntervals.at(-1);
      if (previous?.result === result) previous.end = (index + 1) / 10000;
      else output.bonusIntervals.push({ start: value, end: (index + 1) / 10000, result });
    }
    output.victoryChanceProbes = [];
    for (const boss of [false, true]) for (const value of [0, 0.001, 0.01, 0.02, 0.05, 0.1, 0.15, 0.2, 0.25, 0.5, 0.9]) {
      const current = new module.Engine(fs.readFileSync(copiedSave, 'utf8'), () => 0.5);
      current.mode = 'journey'; current.start();
      if (boss) { current.wave = current.waveCount(); current.spawn(); }
      current.enemy.hp = 0;
      const calls = [], methodCalls = [];
      for (const name of ['drop', 'dropMythic']) {
        const original = current[name];
        current[name] = function (...args) { methodCalls.push(name); return Reflect.apply(original, this, args); };
      }
      current.rng = () => { const stack = Error().stack.split('\n')[2].trim(); calls.push(stack); return value; };
      const before = JSON.parse(current.serialize()), actualBoss = current.enemy.boss; current.victory(); const after = JSON.parse(current.serialize());
      output.victoryChanceProbes.push({ boss, actualBoss, value, methodCalls, calls: [...new Set(calls)].filter(line => /Engine\.(?:victory|dropMythic)/.test(line)),
        materialChanges: Object.keys(after.materials).filter(key => after.materials[key] !== before.materials[key]),
        ticketsAdded: after.abyss.tickets - before.abyss.tickets, inventoryAdded: after.inventory.length - before.inventory.length });
    }
    output.wheelWeightCases = [];
    for (const kind of ['badge', 'costume', 'gear', 'material']) for (const quality of [3, 4, 5, 6]) {
      const prize = kind === 'badge' ? { kind, quality } : kind === 'costume' ? { kind, quality, key: 'sunglasses' } :
        kind === 'gear' ? { kind, quality, key: 'w:349', tier: 100, source: 'journey' } :
          { kind, quality, key: quality === 6 ? 'sacredProtectionScrolls' : 'protectionScrolls' };
      const board = Array.from({ length: 16 }, () => ({ kind: 'badge', quality: 3 })); board[0] = prize;
      output.wheelWeightCases.push({ prize, valid: wheel.validPrize(prize), weights: wheel.wheelWeights(board).slice(0, 2) });
    }
    output.exportedRates = { rarities: module.RARITIES, qualities: module.QUALITIES, abyssPity: module.ABYSS_PITY };
    output.modeRewardTraces = [];
    for (const mode of ['journey', 'guardian', 'abyss']) {
      const current = new module.Engine(fs.readFileSync(copiedSave, 'utf8'), () => 0.5);
      current.mode = mode; current.start(); current.enemy.hp = 0;
      const calls = [], methodCalls = [];
      for (const name of ['drop', 'dropMythic', 'dropOre']) {
        const original = current[name]; current[name] = function (...args) { methodCalls.push(name); return Reflect.apply(original, this, args); };
      }
      current.rng = () => { calls.push(Error().stack.split('\n')[2].trim()); return 0; };
      const before = JSON.parse(current.serialize());
      probe('mode-' + mode, () => current.victory()); const after = JSON.parse(current.serialize());
      output.modeRewardTraces.push({ mode, enemy: current.enemy?.name, methodCalls,
        calls: [...new Set(calls)].filter(line => /Engine\.(?:victory|drop|immortal|abyss)/.test(line)),
        materials: Object.fromEntries(Object.keys(after.materials).filter(key => after.materials[key] !== before.materials[key])
          .map(key => [key, after.materials[key] - before.materials[key]])),
        abyssTicketsAdded: after.abyss.tickets - before.abyss.tickets,
        inventoryAdded: after.inventory.length - before.inventory.length });
    }
    output.qualityIntervals = [];
    for (const site of [87424, 87593, 87767]) {
      const mode = 'journey';
      const intervals = [];
      for (let index = 0; index < 100; index++) {
        const current = new module.Engine(fs.readFileSync(copiedSave, 'utf8'), () => 0.5);
        current.mode = mode; current.start();
        current.rng = () => Error().stack.split('\n')[2].includes('engine.js.jsc:1:' + site + ')') ? index / 100 : 0.5;
        const beforeUIDs = new Set(current.state.inventory.map(item => item.uid));
        current.drop(current.enemy, 100);
        const quality = current.state.inventory.find(item => !beforeUIDs.has(item.uid))?.quality;
        const previous = intervals.at(-1);
        if (previous?.quality === quality) previous.end = (index + 1) / 100;
        else intervals.push({ start: index / 100, end: (index + 1) / 100, quality });
      }
      output.qualityIntervals.push({ mode, site, intervals });
    }
  }
}
console.log(JSON.stringify(output, null, 2));
