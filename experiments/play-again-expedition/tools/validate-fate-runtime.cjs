'use strict';
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
Object.defineProperty(process, 'type', { value: 'browser' });
const archive = process.argv[2], fixture = process.argv[3];
const { Engine } = require(path.join(archive, 'runtime/src/engine.js'));
const qualities = require(path.join(archive, 'runtime/src/fate-data.js')).FATE_QUALITIES;
const { createFateSession, validateFateProfile } = require('../src/fate-session.cjs');
const { createFateRuntime } = require('../src/fate-controller.cjs');
const {createEquipmentSession,readEquipmentDefinitions,validateEquipmentProfile}=require('../src/equipment-session.cjs');
const definitions=readEquipmentDefinitions(require,archive);
const raw = fs.readFileSync(fixture, 'utf8');
// The headless distribution lazily loads its full native extension module on
// first polishing. Initialize this owned runtime BEFORE capturing any controls;
// it is not the renderer and contains no original game/profile objects.
const warm=new Engine(raw,()=>.5);for(const key of Object.keys(warm.state.materials))warm.state.materials[key]=100000;
warm.state.ores=[{tier:150,quality:5,count:1}];warm.polishOre(150,5);
new Engine(warm.serialize(),()=>.5);
const engine = new Engine(raw, () => .5);
const saved = engine.serialize();
global.window = { expedition: { engine, getUi: () => ({entered:true,gameMode:'expedition'}), platform: {read:()=>saved} }, performance: {timeOrigin:100} };
global.localStorage = {length:1,key:()=> 'zseb-expedition-v1',getItem:()=>saved};
const runtime = createFateRuntime(createFateSession, validateFateProfile);
const result = runtime('probe', {}, archive, qualities);
assert(result.diagnostic.passedChecks >= 163);
assert.equal(engine.serialize(), saved);
// Default profile must reproduce all native results, cost and pity, not just tier.
for (const count of [1,10]) for (const pity of [0,99]) {
  function make() { let seed = 123; const e = new Engine(raw, () => ((seed = (Math.imul(seed,1664525)+1013904223)>>>0)/4294967296)); e.state.fatePity=pity; return e; }
  const native = make(), edited = make(), id = native.state.owned[0];
  const control = createFateSession(edited, qualities, {archivePath:archive,assertCurrent:()=>true}, validateFateProfile);
  control.configure({blue:70,purple:25,immortal:4,mythic:1});
  const act = e => count===1 ? e.rollFate(id) : e.rollFateTen(id);
  assert.deepEqual(act(edited),act(native)); assert.equal(edited.serialize(),native.serialize());
  control.reset(); assert.equal(edited.serialize(),native.serialize());
}
// Another independent probability manager must not be reset by fate.
const { createProbabilityModel } = require('../src/probability-model.cjs');
const { createProbabilitySession } = require('../src/probability-session.cjs');
const { discoverProbabilitySites } = require('../src/probability-sites.cjs');
const model=createProbabilityModel(), discovery=discoverProbabilitySites(engine,model,archive);
const drops=createProbabilitySession(engine,model,{archivePath:archive,sites:discovery.sites,assertCurrent:()=>true});
drops.configure({drops:{goldBonusPercent:10}});
const fate=createFateSession(engine,qualities,{archivePath:archive,assertCurrent:()=>true},validateFateProfile);
fate.configure({blue:0,purple:0,immortal:0,mythic:100}); fate.reset();
assert.equal(drops.inspect().installed,true); assert.equal(engine.serialize(),saved); drops.reset();
const {createEquipmentWheelProbe}=require('../src/equipment-wheel-validation.cjs');
const {createGeneratedItemProbe}=require('../src/generated-item-validation.cjs');
// Headless bytecode lazily installs shared native modules for some accessory
// variants; do not pretend it is the original renderer. Run its consumers in
// this owned runtime. The fixed controller/prototype invariants are tested on
// the actual renderer by --equipment-wheel-readonly.
const wheel=createEquipmentWheelProbe(createProbabilityModel,createProbabilitySession,discoverProbabilitySites)(
  Engine,saved,createEquipmentSession,validateEquipmentProfile,definitions,archive);
const generated=createGeneratedItemProbe(createProbabilityModel,createProbabilitySession,discoverProbabilitySites)(
  Engine,saved,createEquipmentSession,validateEquipmentProfile,definitions,archive);
assert(wheel.doubleValuesChecked>0);assert(wheel.gearRewards>=132);assert(generated.passedChecks>1000);
// Same seeded random stream: keys/count/quality/base stats stay native.
for(const seedStart of [1,23,987]) {
 function make(){let seed=seedStart;return new Engine(raw,()=>((seed=(Math.imul(seed,1664525)+1013904223)>>>0)/4294967296));}
 const native=make(),edited=make(),control=createEquipmentSession(edited,definitions,{archivePath:archive,assertCurrent:()=>true},validateEquipmentProfile);
 control.configure({enabled:true});
 const a=native.makeItem(definitions.sample.origin,50,5),b=edited.makeItem(definitions.sample.origin,50,5);
 assert.deepEqual(a.affixes.map(row=>row.key),b.affixes.map(row=>row.key));
 assert.deepEqual({...a,affixes:undefined},{...b,affixes:undefined});control.reset();
}
// Real wheel/drop/fate factories coexist with generation max and clean up separately.
const combined=new Engine(saved,()=>.5);
const gearControl=createEquipmentSession(combined,definitions,{archivePath:archive,assertCurrent:()=>true},validateEquipmentProfile);
const fateControl=createFateSession(combined,qualities,{archivePath:archive,assertCurrent:()=>true},validateFateProfile);
const combinedDrops=createProbabilitySession(combined,model,{archivePath:archive,sites:discovery.sites,assertCurrent:()=>true});
gearControl.configure({enabled:true});fateControl.configure({blue:0,purple:0,immortal:0,mythic:100});
combinedDrops.configure({wheelBonus:{doublePercent:100,marqueePercent:0},drops:{goldBonusPercent:10}});
assert.equal(combined.rollFate(combined.state.owned[0]).quality,6);
combined.prepareLuckyWheel();combined.spinLuckyWheel(10);
assert.equal(combined.state.luckyWheel.pending.spins.length,10);
assert(combined.state.luckyWheel.pending.spins.every(spin=>spin.bonus==='double'));
combined.finishLuckyWheel(combined.state.luckyWheel.pending.round);
gearControl.reset();assert(fateControl.inspect().installed);assert(combinedDrops.inspect().installed);
fateControl.reset();combinedDrops.reset();
console.log(JSON.stringify({verification:'native-rule-copies',fate:result.diagnostic,equipment:{wheel,generated},
 defaultParityCases:4,generationSeedCases:3,independentRulesPreserved:true}));
