'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const { createProbabilityModel } = require('../src/probability-model.cjs');
const { createProbabilitySession } = require('../src/probability-session.cjs');
const model = createProbabilityModel();
const groups = ['ordinary', 'immortal', 'mythic', 'protection', 'sacredProtection'];
const board = [
  ...Array.from({ length: 10 }, () => ({ kind: 'badge', quality: 3 })),
  { kind: 'gear', quality: 5 }, { kind: 'costume', quality: 5 },
  { kind: 'costume', quality: 6 }, { kind: 'gem', quality: 6 },
  { kind: 'material', quality: 5, key: 'protectionScrolls' },
  { kind: 'material', quality: 6, key: 'sacredProtectionScrolls' },
];
const defaults = { ordinary: 92.5, immortal: 5, mythic: 1, protection: 1, sacredProtection: 0.5 };
function fixture({ strict = false, failAt = -1, random = 0.5, relocated = false } = {}) {
  let draws = 0;
  const engine = { state: { materials: { wheelTickets: 100 }, luckyWheel: { board, pending: null } },
    mode: 'guardian', phase: 'idle', rng() { draws++; return random; },
    victory() { return this.rng(); }, dropBonus() { return .02; }, immortalDropChance() { return .07; } };
  const context = vm.createContext({ engine, model, failAt });
  // Like the actual native batch: work on a local board, rotate it each draw,
  // publish the new outer board only AFTER the complete batch.
  vm.runInContext((strict ? '"use strict";\n' : '') + (relocated ? '\n'.repeat(250) : '') + `
    function chooseWheelIndex(board, rng) {
      const weights = model.wheelWeights(board).native;
      let value = rng() * weights.reduce((a, b) => a + b);
      return weights.findIndex(weight => { if(value <= weight) return true; value -= weight; return false; });
    }
    function rollWheelBonus(rng) { const n = rng(); return n < .02 ? 'double' : n < .03 ? 'marquee' : null; }
    engine.spinLuckyWheel = function(count = 1) {
      if(this.state.luckyWheel.pending || this.state.materials.wheelTickets < count) return {ok:false};
      const wheel = this.state.luckyWheel; let local = wheel.board; const spins = [];
      for(let i=0;i<count;i++) {
        if(i === failAt) throw Error('native batch failure');
        const bonus = rollWheelBonus(this.rng), index = chooseWheelIndex(local, this.rng);
        spins.push({board:local, prize:local[index], index, bonus});
        local = [...local.slice(3), ...local.slice(0,3)];
      }
      this.state.materials.wheelTickets -= count; wheel.board = local; wheel.pending = {spins};
      return {ok:true,count};
    };
  `, context, { filename: 'C:/game/app.asar/runtime/src/lucky-wheel.js.jsc' });
  const session = createProbabilitySession(engine, model, { archivePath: 'C:/game/app.asar', assertCurrent: () => true });
  return { engine, session, draws: () => draws };
}
for (const group of groups) for (const count of [1, 10]) test(`${count} native-shaped draws use each local board: ${group}`, () => {
  const f = fixture({ relocated: true }), rng = f.engine.rng;
  f.session.configure({ wheel: { groupPercent: Object.fromEntries(groups.map(k => [k, k === group ? 100 : 0])) },
    wheelBonus: { doublePercent: 0, marqueePercent: 0 } });
  assert.equal(f.engine.spinLuckyWheel(count).ok, true);
  assert.deepEqual(Array.from(f.engine.state.luckyWheel.pending.spins, s => model.groupOf(s.prize)), Array(count).fill(group));
  assert.equal(f.engine.state.materials.wheelTickets, 100 - count);
  assert.equal(f.draws(), count * 2); assert.equal(f.engine.rng, rng);
  assert.equal(f.session.inspect().blocked, null); assert.equal(f.session.inspect().hits.wheelPrize, count);
});
for (const [doublePercent, marqueePercent, expected] of [[100, 0, 'double'], [0, 100, 'marquee'], [0, 0, null]]) {
  test('ten draws retain per-draw bonus ' + expected, () => {
    const f = fixture(); f.session.configure({ wheelBonus: { doublePercent, marqueePercent } });
    f.engine.spinLuckyWheel(10);
    assert.deepEqual(Array.from(f.engine.state.luckyWheel.pending.spins, s => s.bonus), Array(10).fill(expected));
    assert.equal(f.draws(), 20); assert.equal(f.session.inspect().blocked, null);
  });
}
test('default ten rules preserve every native result and RNG draw exactly', () => {
  const native = fixture(), controlled = fixture();
  controlled.session.configure({ wheel: { groupPercent: defaults }, wheelBonus: { doublePercent: 2, marqueePercent: 1 } });
  assert.equal(JSON.stringify(controlled.engine.spinLuckyWheel(10)), JSON.stringify(native.engine.spinLuckyWheel(10)));
  assert.equal(JSON.stringify(controlled.engine.state), JSON.stringify(native.engine.state));
  assert.equal(controlled.draws(), native.draws());
});
test('ten draws honor type weights on every regenerated board', () => {
  const f = fixture(); f.session.configure({ wheel: { groupPercent: { ...defaults, ordinary: 0, immortal: 100, mythic: 0, protection: 0, sacredProtection: 0 },
    typeWeights: { gear: 0, costume: 1 } }, wheelBonus: { doublePercent: 0, marqueePercent: 0 } });
  f.engine.spinLuckyWheel(10);
  assert.ok(f.engine.state.luckyWheel.pending.spins.every(s => s.prize.kind === 'costume' && s.prize.quality === 5));
});
test('insufficient tickets and pending animation do not spend or invoke RNG', () => {
  const f = fixture(); f.session.configure({ wheel: { groupPercent: defaults } });
  f.engine.state.materials.wheelTickets = 9; const before = JSON.stringify(f.engine.state);
  assert.equal(f.engine.spinLuckyWheel(10).ok, false); assert.equal(JSON.stringify(f.engine.state), before); assert.equal(f.draws(), 0);
  f.engine.state.materials.wheelTickets = 100; f.engine.state.luckyWheel.pending = {};
  assert.equal(f.engine.spinLuckyWheel(10).ok, false); assert.equal(f.draws(), 0);
});
test('native batch failure restores RNG and reset preserves another editor and prior rewards', () => {
  const f = fixture({ failAt: 3 }), rng = f.engine.rng;
  f.session.configure({ wheel: { groupPercent: defaults }, drops: { equipmentBonusPercent: 25 } });
  assert.throws(() => f.engine.spinLuckyWheel(10), /native batch failure/); assert.equal(f.engine.rng, rng);
  f.engine.state.materials.protectionScrolls = 7;
  assert.equal(f.session.reset('wheel').restored, true); assert.equal(f.engine.dropBonus(), .27);
  assert.equal(f.engine.state.materials.protectionScrolls, 7); assert.equal(f.session.inspect().namespaces.drops.enabled, true);
});
test('unavailable consumer arguments report failure instead of claiming stale-board mapping succeeded', () => {
  const f = fixture({ strict: true });
  f.session.configure({ wheel: { groupPercent: { ordinary: 0, immortal: 0, mythic: 0, protection: 100, sacredProtection: 0 } } });
  f.engine.spinLuckyWheel(10);
  assert.equal(f.session.inspect().blocked, 'WHEEL_DRAW_BOARD_UNAVAILABLE');
  assert.equal(f.session.inspect().hits.wheelPrize ?? 0, 0);
});
test('non-wheel RNG retains receiver, arguments and return value without global/prototype changes', () => {
  const f = fixture(); let observed;
  const original = function (...args) { observed = { receiver: this, args }; return .77; };
  f.engine.rng = original; f.engine.victory = function () { return this.rng('native', 42); };
  const descriptors = Object.getOwnPropertyDescriptors(Array.prototype), random = Math.random;
  const control = createProbabilitySession(f.engine, model, { archivePath: 'C:/game/app.asar', assertCurrent: () => true });
  control.configure({ drops: { guardianTicketPercent: 25 } });
  assert.equal(f.engine.victory(), .77); assert.equal(observed.receiver, f.engine); assert.deepEqual(observed.args, ['native', 42]);
  assert.equal(f.engine.rng, original); assert.equal(Math.random, random);
  assert.deepEqual(Object.getOwnPropertyDescriptors(Array.prototype), descriptors);
});
