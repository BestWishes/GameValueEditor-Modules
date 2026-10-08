'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const { discoverProbabilitySites } = require('../src/probability-sites.cjs');
const { createProbabilityModel } = require('../src/probability-model.cjs');
const model = createProbabilityModel(), archive = 'C:/game/app.asar';
const state = { cleared: 0, selected: 1, guardianSelected: 1, materials: {}, inventory: [], equipped: [],
  luckyWheel: {}, abyss: { tickets: 0 } };
function nativeFixture(padding = 0, missingGuardian = false) {
  // Different minifier output and bundle line numbers, no old column constants.
  const source = '\n'.repeat(padding) + `
    function chooseWheelIndex(rng) { return rng(); }
    function rollWheelBonus(rng) { return rng(); }
    class Engine {
      constructor(raw, rng) { this.state=JSON.parse(raw); this.rng=rng; this.mode='journey'; }
      serialize() { return JSON.stringify(this.state); }
      prepareLuckyWheel() { this.state.luckyWheel.board=[]; }
      spinLuckyWheel() { chooseWheelIndex(this.rng); rollWheelBonus(this.rng); }
      start() { this.enemy={hp:1}; return true; }
      victory() {
        if(this.mode==='guardian' && this.state.cleared>=500 && this.state.selected>=100) {
          ${missingGuardian ? '' : 'if(this.rng()<0.5) this.state.abyss.tickets++;'}
        } else if(this.mode==='journey') { this.rng(); this.dropBonus(); }
        else if(this.mode==='cow') { this.rng(); [1,2,3].forEach(()=>this.rng()); }
      }
      dropBonus() { return 0; }
      badgePartyBonus() { return 0; }
      drop() { this.rng(); this.badgePartyBonus('upgrade'); }
      dropFlameImmortals() { this.rng(); }
      startMine() { this.mode='mine'; }
      afterStarHit() { this.rng(); }
      dropMineReward() { this.rng(); }
      dropOre() { this.rng(); }
      cowDrop86() { this.rng(); this.rng(); }
      startCowDungeon() { this.mode='cow'; }
      spawn() { this.enemy={hp:1}; }
    }
    Engine;
  `;
  const Engine = vm.runInNewContext(source, {}, { filename: archive + '/app/updated-bundle.js' });
  return new Engine(JSON.stringify(state), () => 0.5);
}
test('Rediscover all current consumers after bundle relocation without original actions', () => {
  const first = nativeFixture(), moved = nativeFixture(250);
  const raw = moved.serialize(), rng = moved.rng;
  const before = discoverProbabilitySites(first, model, archive), after = discoverProbabilitySites(moved, model, archive);
  assert.deepEqual(before.errors, {}); assert.deepEqual(after.errors, {});
  assert.equal(Object.keys(after.sites).length, 13);
  for (const key of Object.keys(after.sites)) {
    assert.notDeepEqual(after.sites[key], before.sites[key]);
    assert.ok(after.sites[key].every(frame => frame.includes(archive.toLowerCase() + '/')));
  }
  assert.equal(moved.serialize(), raw); assert.equal(moved.rng, rng);
});
test('A changed individual rule does not hide the other consumers or mutate live state', () => {
  const engine = nativeFixture(400, true), before = engine.serialize();
  const result = discoverProbabilitySites(engine, model, archive);
  assert.deepEqual(result.errors, { guardianTicket: 'PROBABILITY_CONSUMER_NOT_UNIQUE' });
  assert.equal(Object.keys(result.sites).length, 12);
  assert.ok(result.sites.wheelPrize && result.sites.mineQuality);
  assert.equal(engine.serialize(), before);
});
test('Do not accept a consumer from a different archive', () => {
  const engine = nativeFixture(), before = engine.serialize();
  const result = discoverProbabilitySites(engine, model, 'C:/other/app.asar');
  assert.equal(Object.keys(result.sites).length, 0); assert.equal(engine.serialize(), before);
});
