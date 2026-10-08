'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const { createProbabilityModel } = require('../src/probability-model.cjs');
const { createProbabilitySession } = require('../src/probability-session.cjs');
const model = createProbabilityModel();
const board = [
  ...Array.from({ length: 10 }, () => ({ kind: 'badge', quality: 3 })),
  { kind: 'gear', quality: 5 }, { kind: 'costume', quality: 5 }, { kind: 'costume', quality: 6 }, { kind: 'costume', quality: 6 },
  { kind: 'material', quality: 5, key: 'protectionScrolls' }, { kind: 'material', quality: 6, key: 'sacredProtectionScrolls' },
];
const percentages = { ordinary: 92.5, immortal: 5, mythic: 1, protection: 1, sacredProtection: 0.5 };
test('Updated gem prizes use quality groups and their own optional type weight', () => {
  for (const quality of [3, 4, 5, 6]) assert.equal(model.groupOf({ kind: 'gem', quality }),
    quality === 6 ? 'mythic' : quality === 5 ? 'immortal' : 'ordinary');
  const updated = board.map((prize, index) => index === 0 ? { kind: 'gem', quality: 3, tier: 200, type: 'attack' } : prize);
  const profile = { groupPercent: percentages, typeWeights: { gem: 0 } };
  model.validate({ wheel: profile });
  const weights = model.wheelWeights(updated, profile);
  assert.equal(weights.desired[0], 0); assert.equal(weights.desired.reduce((a, b) => a + b), 1);
  assert.throws(() => model.groupOf({ kind: 'unknown', quality: 5 }), /CONTRACT_CHANGED/);
});
for (const target of [0, 5, 25, 50, 75, 100]) test('Bernoulli distribution ' + target, () => {
  let hits = 0;
  for (let index = 0; index < 10000; index++) if (model.mapBernoulli(index / 10000, 50, target) < 0.5) hits++;
  assert.equal(hits, target * 100);
});
test('Default weights are exact passthrough', () => {
  const weights = model.wheelWeights(board, { groupPercent: percentages });
  assert.deepEqual(weights.native, weights.desired);
  for (const value of [0, 0.001, 0.5, 0.999999]) assert.equal(model.mapCategorical(value, weights.native, weights.desired), value);
});
for (const group of Object.keys(percentages)) test('100 percent selects only ' + group, () => {
  const profile = { groupPercent: Object.fromEntries(Object.keys(percentages).map(key => [key, key === group ? 100 : 0])) };
  const weights = model.wheelWeights(board, profile), total = weights.native.reduce((a, b) => a + b);
  for (const value of [0, 0.00001, 0.25, 0.5, 0.999999999]) {
    let remaining = model.mapCategorical(value, weights.native, weights.desired) * total;
    const index = weights.native.findIndex(weight => { if (remaining <= weight) return true; remaining -= weight; return false; });
    assert.equal(model.groupOf(board[index]), group);
  }
});
for (const input of [null, [], { arbitrary: 1 }, { drops: { abyssImmortalPercent: NaN } },
  { drops: { guardianTicketPercent: 101 } }, { drops: { equipmentBonusPercent: -101 } },
  { wheelBonus: { doublePercent: 70, marqueePercent: 31 } }, { wheel: { groupPercent: { ...percentages, ordinary: 90 } } },
  JSON.parse('{"__proto__":{"drops":{"guardianTicketPercent":100}}}'), Object.create({ drops: {} })]) {
  test('Reject invalid profile ' + String(input), () => assert.throws(() => model.validate(input)));
}
test('Clone settings and reject unavailable groups or all-zero in-group weights', () => {
  const input = { drops: { guardianTicketPercent: 10 } }, copy = model.validate(input); input.drops.guardianTicketPercent = 90;
  assert.equal(copy.drops.guardianTicketPercent, 10);
  assert.throws(() => model.wheelWeights(Array.from({ length: 16 }, () => ({ kind: 'badge', quality: 3 })), { groupPercent: percentages }), /UNAVAILABLE/);
  assert.throws(() => model.wheelWeights(board, { groupPercent: percentages, typeWeights: { badge: 0 } }), /UNAVAILABLE/);
});
function fixture() {
  const engine = { state: { luckyWheel: { board, pending: null } }, mode: 'guardian', phase: 'idle', rng: () => 0.5,
    spinLuckyWheel() { return this.rng(); }, victory() { return this.rng(); }, dropBonus() { return 0.02; }, immortalDropChance() { return 0.07; } };
  let live = true;
  return { engine, session: createProbabilitySession(engine, model, { archivePath: 'C:/game/app.asar', assertCurrent: () => live }),
    changeOwner: () => { live = false; } };
}
test('Configure/reset never mutate save state, RNG or global Math.random', () => {
  const { engine, session } = fixture(), before = JSON.stringify(engine.state), rng = engine.rng, random = Math.random, original = engine.victory;
  session.configure({ drops: { equipmentBonusPercent: 10, abyssImmortalPercent: 100 } });
  assert.equal(engine.dropBonus(), 0.12000000000000001);
  assert.equal(engine.immortalDropChance(), 0.07); engine.mode = 'abyss'; assert.equal(engine.immortalDropChance(), 1);
  assert.equal(engine.victory(), 0.5); assert.equal(engine.rng, rng); assert.equal(Math.random, random); assert.equal(JSON.stringify(engine.state), before);
  assert.equal(session.reset().restored, true); assert.equal(engine.victory, original); assert.equal(engine.dropBonus(), 0.02);
});
test('Changed owner defaults to native behavior, reset does not clobber others', () => {
  const { engine, session, changeOwner } = fixture(); session.configure({ drops: { equipmentBonusPercent: 100 } }); changeOwner();
  assert.equal(engine.dropBonus(), 0.02);
  const other = () => 42; engine.victory = other;
  assert.equal(session.reset().restored, false); assert.equal(engine.victory, other);
});
test('Reject active battle, pending wheel, changed methods and RNG before install', () => {
  for (const change of [engine => { engine.phase = 'fighting'; }, engine => { engine.state.luckyWheel.pending = {}; },
    engine => { engine.victory = () => 1; }, engine => { engine.rng = () => 0.9; }]) {
    const { engine, session } = fixture(); change(engine); assert.throws(() => session.configure({}));
  }
});
test('Native errors still restore the original RNG', () => {
  const { engine } = fixture(); engine.victory = () => { throw Error('native failure'); };
  const session = createProbabilitySession(engine, model, { archivePath: 'C:/game/app.asar', assertCurrent: () => true });
  const rng = engine.rng; session.configure({ drops: { guardianTicketPercent: 100 } });
  assert.throws(() => engine.victory(), /native failure/); assert.equal(engine.rng, rng); assert.equal(session.reset().restored, true);
});
test('empty profiles and accessor fields are rejected', () => {
  for (const profile of [{}, { drops: {} }, { get wheelBonus() { throw Error('accessed'); } }]) assert.throws(() => model.validate(profile));
});
test('RNG made readonly before configure is rejected without new game errors', () => {
  const { engine, session } = fixture(); Object.defineProperty(engine, 'rng', { writable: false });
  assert.throws(() => session.configure({ drops: { guardianTicketPercent: 100 } })); assert.equal(engine.victory(), 0.5);
});
test('RNG locked after configure passes native and reports conflict', () => {
  const { engine, session } = fixture(); session.configure({ drops: { guardianTicketPercent: 100 } });
  Object.defineProperty(engine, 'rng', { writable: false }); assert.equal(engine.victory(), 0.5); assert.equal(session.inspect().blocked, 'RNG_OWNER_CHANGED');
});
test('RNG locked during a native action does not introduce a cleanup exception', () => {
  const { engine } = fixture(); engine.victory = function () { Object.defineProperty(this, 'rng', { writable: false }); return this.rng(); };
  const control = createProbabilitySession(engine, model, { archivePath: 'C:/game/app.asar', assertCurrent: () => true });
  control.configure({ drops: { guardianTicketPercent: 100 } }); assert.equal(engine.victory(), 0.5); assert.equal(control.inspect().blocked, 'RNG_RESTORE_CONFLICT');
});
test('frozen owned hook on reset is a reported conflict, not an overwrite/exception', () => {
  const { engine, session } = fixture(); session.configure({ drops: { equipmentBonusPercent: 10 } });
  Object.defineProperty(engine, 'dropBonus', { configurable: false }); const outcome = session.reset();
  assert.equal(outcome.restored, false); assert.ok(outcome.conflicts.includes('dropBonus')); assert.equal(engine.dropBonus(), 0.02);
});
test('inspection tolerates unavailable native probability getter', () => {
  const { engine } = fixture(); engine.dropBonus = () => { throw Error('native getter unavailable'); };
  const control = createProbabilitySession(engine, model, { archivePath: 'C:/game/app.asar', assertCurrent: () => true });
  assert.equal(control.inspect().nativeDropBonus, null);
});
test('flame probabilities require the verified native function', () => {
  const { session } = fixture(); assert.throws(() => session.configure({ drops: { flameMythicPercent: 100 } }), /NOT_SUPPORTED/);
});
function bundledCall(method, name, column) {
  const { engine } = fixture(); let actualColumn;
  engine.rng = () => {
    actualColumn = Number(Error().stack.match(/preview\.js\.jsc:1:(\d+)/)?.[1]); return 0.5;
  };
  const context = vm.createContext({ engine });
  const statement = name.includes('Engine.') ? `class Engine { ${method}(){return engine.rng();} }; Object.setPrototypeOf(engine,Engine.prototype); engine.${method}=Engine.prototype.${method};` :
    `function ${name}(){return engine.rng();} engine.${method}=function(){return ${name}();};`;
  const filename = 'C:/game/app.asar/app/preview.js.jsc';
  vm.runInContext(statement, context, { filename }); engine[method](25400);
  vm.runInContext(' '.repeat(column - actualColumn) + statement, vm.createContext({ engine }), { filename });
  engine[method](25400); assert.equal(actualColumn, column);
  const session = createProbabilitySession(engine, model, { archivePath: 'C:/game/app.asar', assertCurrent: () => true });
  return { engine, session };
}
test('formal bundle wheel bonus callsite is recognized', () => {
  const { engine, session } = bundledCall('spinLuckyWheel', 'rollWheelBonus', 3117159);
  session.configure({ wheelBonus: { doublePercent: 100, marqueePercent: 0 } });
  assert.ok(engine.spinLuckyWheel() < 0.02); assert.equal(session.inspect().hits.wheelBonus, 1);
});
test('formal bundle prize callsite is recognized', () => {
  const { engine, session } = bundledCall('spinLuckyWheel', 'chooseWheelIndex', 3116971);
  const profile = { groupPercent: Object.fromEntries(Object.keys(percentages).map(key => [key, key === 'protection' ? 100 : 0])) };
  session.configure({ wheel: profile }); const value = engine.spinLuckyWheel(), weights = model.wheelWeights(board).native;
  let remaining = value * weights.reduce((a, b) => a + b);
  const selected = weights.findIndex(weight => { if (remaining <= weight) return true; remaining -= weight; return false; });
  assert.equal(model.groupOf(board[selected]), 'protection'); assert.equal(session.inspect().hits.wheelPrize, 1);
});
test('formal bundle guardian and flame callsites are recognized', () => {
  const guard = bundledCall('victory', 'Engine.victory', 671294);
  guard.session.configure({ drops: { guardianTicketPercent: 0 } }); assert.ok(guard.engine.victory() >= 0.5); assert.equal(guard.session.inspect().hits.guardianTicket, 1);
  const fire = bundledCall('dropFlameImmortals', 'Engine.dropFlameImmortals', 3527587); fire.engine.mode = 'flame';
  fire.session.configure({ drops: { flameImmortalPercent: 100 } }); assert.ok(fire.engine.dropFlameImmortals(25400) < 0.05);
  assert.equal(fire.session.inspect().hits.flameImmortalPercent, 1);
});
test('small update moves the bundle location without disabling the named consumer', () => {
  const { engine, session } = bundledCall('spinLuckyWheel', 'rollWheelBonus', 3117160);
  session.configure({ wheelBonus: { doublePercent: 100, marqueePercent: 0 } }); assert.ok(engine.spinLuckyWheel() < 0.02); assert.equal(session.inspect().hits.wheelBonus, 1);
});
