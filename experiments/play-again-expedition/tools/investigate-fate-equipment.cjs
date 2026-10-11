'use strict';
// Owned headless process only: imports pure native runtime, never main/preload.
const fs = require('node:fs'), path = require('node:path');
Object.defineProperty(process, 'type', { value: 'browser' });
const archive = process.argv[2], fixture = process.argv[3];
const { Engine } = require(path.join(archive, 'runtime/src/engine.js'));
const original = fs.readFileSync(fixture, 'utf8');
const engine = new Engine(original, () => 0.5);
const { affixBounds, affixText } = require(path.join(archive, 'runtime/src/affixes.js'));
const affixes = require(path.join(archive, 'runtime/src/affixes.js'));
const sheet = require(path.join(archive, 'runtime/src/sheet-affixes.js'));
console.log('doubleRange', JSON.stringify(sheet.SHEET.unique59_w_704));
for (const random of [0,.5,1-Number.EPSILON]) {
 let calls=0;
 for(const key of ['wind','hex','unique59_w_704']) {
  try { console.log('directRoll',key,random,JSON.stringify(affixes.rollAffix(key,5,()=>{calls++;return random;})),calls); }
  catch(error){console.log('directRoll-error',error.message);}
 }
 // Special affixes are validated through native makeItem below, not a guessed
 // rollSheetAffix signature. This script never supplies results as live proof.
 const e = new Engine(original,()=>random), frames=[];
 e.rng=function(){const error=new Error();Error.captureStackTrace(error,e.rng);frames.push(error.stack.split('\n')[1]);return random;};
 for(const args of [['w:704',50,5],[{key:'w:704',kind:'weapon',tier:50,quality:5}]]) {
  try{console.log('makeItem',JSON.stringify(args),JSON.stringify(e.makeItem(...args)),frames.slice(0,25));}catch(error){console.log('makeItem-error',error.message);}
  frames.length=0;
 }
}
console.log('qualities', require(path.join(archive, 'runtime/src/fate-data.js')).FATE_QUALITIES);
for (const pity of [0, 99]) {
 const copy = new Engine(original, () => 0.1); copy.state.fatePity = pity;
 const before = JSON.parse(copy.serialize());
 const result = copy.rollFateTen(copy.state.party[0]);
 const after = JSON.parse(copy.serialize());
 console.log('ten', JSON.stringify({pity, qualities:result.map(row=>row.quality),
   changedKeys:Object.keys(before).filter(key=>JSON.stringify(before[key])!==JSON.stringify(after[key])),
   materialDelta:Object.fromEntries(Object.keys(before.materials).filter(key=>before.materials[key]!==after.materials[key]).map(key=>[key,after.materials[key]-before.materials[key]]))}));
}
for (const item of engine.state.inventory.filter(item => engine.state.equipped.some(slots => Object.values(slots).includes(item.uid))).slice(0, 5)) {
 const copy = new Engine(engine.serialize(), () => 0.5), selected = copy.itemByUid(item.uid);
 const inspect = entry => ({uid:entry.uid,quality:entry.quality,enhance:entry.enhance,relic:entry.relic,boosts:entry.boosts,
   affixes:entry.affixes.map(affix=>({key:affix.key,value:affix.value,bounds:affixBounds(affix.key,entry.quality),text:affixText(affix)}))});
 console.log('equipmentBefore', JSON.stringify(inspect(selected)));
 try { console.log('effective', JSON.stringify(copy.effectiveItem(selected)).slice(0,2500)); } catch(error){console.log('effective-error',error.message);}
 for (const affix of selected.affixes) { const range=affixBounds(affix.key,selected.quality); if(Array.isArray(range)&&range.every(Number.isFinite)) affix.value=range[1]; }
 const reloaded=new Engine(copy.serialize(),()=>0.5).itemByUid(item.uid);
 console.log('equipmentAfterReload',JSON.stringify(inspect(reloaded)));
}
