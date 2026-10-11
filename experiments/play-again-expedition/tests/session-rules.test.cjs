'use strict';
const test=require('node:test'),assert=require('node:assert/strict'),vm=require('node:vm'),crypto=require('node:crypto');
const {createFateSession,validateFateProfile}=require('../src/fate-session.cjs');
const {createEquipmentSession,validateEquipmentProfile}=require('../src/equipment-session.cjs');
const {buildFateRequest,buildEquipmentRequest}=require('../src/fate-controller.cjs');
const qualities=[3,4,5,6].map((quality,i)=>({quality,name:String(quality),chance:[.7,.25,.04,.01][i]}));
const folder=__dirname.replaceAll('\\','/');
const rollAffix=vm.runInNewContext('(function rollAffix(rng){return {key:"power",value:Math.floor(rng.call(this,"affix",3)*11)+10};})',{}, {filename:folder+'/affixes.js'});
const roll=vm.runInNewContext('(function roll(rng){return Math.floor(rng.call(this,"secondary",5)*3)+3;})',{}, {filename:folder+'/sheet-affixes.js'});
const rollBadge=vm.runInNewContext('(function rollBadge(rng,q){q=q??(3+Math.floor(rng()*3));return {quality:q,affixes:[{key:"power",value:10+Math.floor(rng()*11)}]};})',{}, {filename:folder+'/badges.js'});
const rollCostume=vm.runInNewContext('(function rollCostume(key,rng,q){q=q??(3+Math.floor(rng()*4));return {key,quality:q,hue:Math.floor(rng()*360),affixes:[{key:"power",value:10+Math.floor(rng()*11)}]};})',{}, {filename:folder+'/costumes.js'});
const gemTypes=[{key:'earth',min:0,max:0,weight:40},{key:'opal',min:3,max:5,weight:60}];
const makeGem=vm.runInNewContext('(function makeGem(tier,q,uid,rng,type){type=type??(rng()<.4?"earth":"opal");const bonus=type==="earth"?0:Math.round((3+2*rng())*10)/10;return {uid,tier,quality:q,type,bonus,affixes:[rollAffix(rng)]};})',{rollAffix}, {filename:folder+'/workshop.js'});
const gemModule={makeGem};
const definitions={sample:{key:'power',origin:'w:sample'},ranges:{power:{5:[10,20]}},secondary:{power:[3,5]},gemTypes:Object.fromEntries(gemTypes.map(row=>[row.key,row])),gemSamples:{earth:.1,opal:.5}};
class Engine {
 constructor(raw,rng){this.state=JSON.parse(raw);this.rng=rng;this.phase='idle';this.mode='journey';}
 serialize(){return JSON.stringify(this.state);}
 fatePool(){return qualities.map(row=>({...row,key:'q'+row.quality}));}
 sampleFate(id,force=false){const value=this.rng();let sum=0;let q=6;for(const row of qualities){sum+=row.chance;if(value<sum){q=row.quality;break;}}this.rng();return {quality:force?6:q,key:'q'+(force?6:q)};}
 samplePaidFates(id,force=false){return this.sampleFate(id,force);}
 makeItem(key,tier,quality,options){const rng=typeof options?.rng==='function'?options.rng:this.rng;const a=rollAffix(rng);a.value2=roll(rng);return {key,tier,quality,affixes:[a]};}
 buyBadge(){const row={...rollBadge(this.rng),uid:++this.state.uid};this.state.badges.push(row);return row;}
 buyCostume(){const row={...rollCostume('hat',this.rng),uid:++this.state.uid};this.state.costumes.push(row);return row;}
 polishOre(tier,q){const row=makeGem(tier,q,++this.state.uid,this.rng);this.state.gems.push(row);return row;}
 wheelGem(){const row=gemModule.makeGem(150,5,++this.state.uid,this.rng);this.state.gems.push(row);return row;}
 spinLuckyWheel(){return this.buyCostume();}
 victory(){} dropBonus(){return 0;} immortalDropChance(){return 0;}
}
function fixture(){
 const state={rewardClockId:'private-clock',owned:[1],inventory:[],materials:{},version:1,uid:1,badges:[],costumes:[],gems:[]};
 const engine=new Engine(JSON.stringify(state),()=>.5),stored=new Map([['zseb-expedition-v1',engine.serialize()]]);
 const game={engine,getUi:()=>({entered:true,gameMode:'expedition'}),platform:{read:()=>stored.get('zseb-expedition-v1')}};
 const window={expedition:game,performance:{timeOrigin:10}};
 const localStorage={get length(){return stored.size;},key:i=>[...stored.keys()][i],getItem:key=>stored.get(key)??null};
 const renderer=vm.createContext({window,localStorage},{codeGeneration:{strings:false,wasm:false}});
 const preferences={contextIsolation:true,nodeIntegration:false};
 const contents={getURL:()=> 'expedition://game/index.html',getLastWebPreferences:()=>preferences,
  executeJavaScriptInIsolatedWorld:async(world,scripts)=>{assert.equal(world,999);return vm.runInContext(scripts[0].code,renderer);}};
 const config={processId:7,executablePath:'fake.exe',archivePath:folder,profilePath:'fake-profile'};
 const electron={app:{getPath:()=>config.profilePath,getAppPath:()=>folder},BrowserWindow:{getAllWindows:()=>[{isDestroyed:()=>false,webContents:contents}]}};
 const process={pid:7,execPath:'fake.exe',versions:{electron:'next'},arch:'x64',mainModule:{require:name=>{
  if(name==='electron')return electron;if(name==='node:crypto')return crypto;if(name.endsWith('/fate-data.js'))return {FATE_QUALITIES:qualities};
  if(name.endsWith('/sheet-affixes.js'))return {SHEET:{power:{key:'power',origin:'w:sample',min2:3,max2:5}}};
  if(name.endsWith('/affixes.js'))return {AFFIXES:{power:[]},affixBounds:()=>[10,20]};
  if(name.endsWith('/badges.js'))return {rollBadge};if(name.endsWith('/costumes.js'))return {rollCostume};if(name.endsWith('/workshop.js'))return {makeGem};
  if(name.endsWith('/mining-data.js'))return {GEM_TYPES:gemTypes};
  throw Error('Unexpected import');}}};
 const main=vm.createContext({process});
 const run=(kind,op,input={})=>vm.runInContext((kind==='fate'?buildFateRequest:buildEquipmentRequest)(config,op,input).params.expression,main);
 return {engine,stored,game,window,run,config,process,preferences};
}
const blue={blue:100,purple:0,immortal:0,mythic:0};
test('strict fate and equipment schemas reject absent, extra, nonfinite or wrong values',()=>{
 for(const p of [{}, {...blue,blue:NaN},{...blue,blue:101},{...blue,purple:1},{...blue,code:'x'},[]])assert.throws(()=>validateFateProfile(p));
 for(const p of [{},{enabled:false},{enabled:true,other:1},[]])assert.throws(()=>validateEquipmentProfile(p));
 assert.deepEqual(validateFateProfile(blue),blue);assert.deepEqual(validateEquipmentProfile({enabled:true}),{enabled:true});
});
for(const kind of ['fate','equipment']) {
 const profile=kind==='fate'?blue:{enabled:true};
 test(kind+' fixed controller is CSP-safe and inspection changes no live data or hook',async()=>{
  const f=fixture(),before=f.engine.serialize(),method=kind==='fate'?'sampleFate':'makeItem',original=f.engine[method];
  const read=await f.run(kind,'inspect');assert.equal(read[kind].readOnly,true);assert.equal(f.engine.serialize(),before);assert.equal(f.engine[method],original);
  assert.doesNotMatch(JSON.stringify(read),/private-clock|"anchor"/);
  const after=await f.run(kind,'configure',{scopeHash:read.scopeHash,revision:0,profile});
  assert.equal(after[kind].report.installed,true);assert.equal(f.engine.serialize(),before);
  await assert.rejects(f.run(kind,'configure',{scopeHash:read.scopeHash,revision:0,profile}),/READ_RECEIPT_SCOPE_CHANGED/);
  const reset=await f.run(kind,'reset',{scopeHash:after.scopeHash,revision:1});assert.equal(reset[kind].report.installed,false);assert.equal(f.engine[method],original);
 });
 test(kind+' conflicts and changed sessions do not overwrite foreign hooks',async()=>{
  const f=fixture(),read=await f.run(kind,'inspect');
  const after=await f.run(kind,'configure',{scopeHash:read.scopeHash,revision:0,profile});
  const method=kind==='fate'?'sampleFate':'makeItem',other=()=>123;f.engine[method]=other;
  await assert.rejects(f.run(kind,'reset',{scopeHash:after.scopeHash,revision:1}));assert.equal(f.engine[method],other);
  f.game.engine=new Engine(f.engine.serialize(),()=>.5);
  await assert.rejects(f.run(kind,'configure',{scopeHash:read.scopeHash,revision:0,profile}),/READ_RECEIPT_SCOPE_CHANGED/);
 });
 test(kind+' battle, wrong identity and duplicate slots reject without changing state',async()=>{
  for(const mutate of [f=>{f.engine.phase='fighting';},f=>{f.engine.state.rewardClockId='wrong';},f=>{f.stored.set('zseb-expedition-v1-slot-1',f.engine.serialize());}]) {
   const f=fixture(),read=await f.run(kind,'inspect');mutate(f);const before=f.engine.serialize();
   await assert.rejects(f.run(kind,'configure',{scopeHash:read.scopeHash,revision:0,profile}));assert.equal(f.engine.serialize(),before);
  }
 });
 test(kind+' request cannot inject operations, code or scope extras',()=>{
  const f=fixture(),build=kind==='fate'?buildFateRequest:buildEquipmentRequest;
  assert.throws(()=>build(f.config,'spawn'));assert.throws(()=>build(f.config,'inspect',{code:'evil'}));
  assert.throws(()=>build(f.config,'reset',{scopeHash:'a'.repeat(64),revision:0,namespace:'other'}));
  assert.throws(()=>build(f.config,'probe',{area:'arbitrary'}));assert.throws(()=>build(f.config,'inspect',{area:'shop'}));
  if(kind==='equipment')for(const area of ['gear','shop','wheel','gem-base','gem-wheel'])assert.doesNotThrow(()=>build(f.config,'probe',{area}));
 });
}
test('fate maps only tier and preserves second random draw and forced mythic',()=>{
 const f=fixture(),control=createFateSession(f.engine,qualities,{archivePath:folder,assertCurrent:()=>true},validateFateProfile),rng=f.engine.rng;
 control.configure({blue:0,purple:0,immortal:0,mythic:100});assert.equal(f.engine.sampleFate(1).quality,3);
 assert.equal(f.engine.samplePaidFates(1).quality,6);
 assert.equal(f.engine.rng,rng);assert.equal(f.engine.samplePaidFates(1,true).quality,6);assert.equal(control.inspect().hits,1);control.reset();
});
test('equipment numbers use native maximum formula and restore the RNG after throws',()=>{
 const f=fixture(),control=createEquipmentSession(f.engine,definitions,{archivePath:folder,assertCurrent:()=>true},validateEquipmentProfile),rng=f.engine.rng;
 control.configure({enabled:true});const item=f.engine.makeItem('w:sample',50,5);
 assert.equal(item.affixes[0].value,20);assert.equal(item.affixes[0].value2,5);assert.equal(f.engine.rng,rng);control.reset();
 assert.equal(f.engine.makeItem('w:sample',50,5).affixes[0].value,15);
 const original=f.engine.makeItem;f.engine.makeItem=function(...args){if(args[0]==='w:throw')throw Error('native');return Reflect.apply(original,this,args);};
 const throwing=createEquipmentSession(f.engine,definitions,{archivePath:folder,assertCurrent:()=>true},validateEquipmentProfile);
 throwing.configure({enabled:true});assert.throws(()=>f.engine.makeItem('w:throw',50,5),/native/);assert.equal(f.engine.rng,rng);throwing.reset();
});
test('equipment explicit RNG preserves frozen options, receiver, arguments and default RNG',()=>{
 const f=fixture(),calls=[],source=function(...args){calls.push({receiver:this,args});return .5;};
 const options=Object.freeze(Object.defineProperty({rng:source,preview:true},'nativeTag',{value:'retained',enumerable:false}));
 const before=f.engine.serialize(),nativeRng=f.engine.rng;
 const control=createEquipmentSession(f.engine,definitions,{archivePath:folder,assertCurrent:()=>true},validateEquipmentProfile);
 const originalOptions=Object.getOwnPropertyDescriptors(options);
 const native=f.engine.makeItem('w:sample',50,5,options),baselineCalls=calls.splice(0);
 control.configure({enabled:true});
 const actual=f.engine.makeItem('w:sample',50,5,options);
 assert.equal(native.affixes[0].value,15);assert.equal(actual.affixes[0].value,20);assert.equal(actual.affixes[0].value2,5);
 assert.deepEqual(calls,baselineCalls);assert.equal(f.engine.rng,nativeRng);assert.equal(f.engine.serialize(),before);
 assert.deepEqual(calls.map(row=>row.args),[['affix',3],['secondary',5]]);
 assert.deepEqual(Object.getOwnPropertyDescriptors(options),originalOptions);assert.equal(options.rng,source);
 assert.equal(control.inspect().hits,2);assert.equal(control.inspect().explicitRng,true);control.reset();
 assert.equal(f.engine.makeItem('w:sample',50,5,options).affixes[0].value,15);
});
test('equipment explicit RNG throws once and does not leave a wrapper or active session',()=>{
 const f=fixture(),native=f.engine.rng,options=Object.freeze({preview:true,rng(){throw Error('native explicit RNG failure');}});
 const control=createEquipmentSession(f.engine,definitions,{archivePath:folder,assertCurrent:()=>true},validateEquipmentProfile);
 control.configure({enabled:true});assert.throws(()=>f.engine.makeItem('w:sample',50,5,options),/native explicit RNG failure/);
 assert.equal(f.engine.rng,native);assert.equal(control.inspect().blocked,null);assert.equal(control.inspect().ready,true);control.reset();
});
test('equipment inherited explicit RNG and nonfunction option keep native selection semantics',()=>{
 const f=fixture(),source=()=>.5,options=Object.freeze(Object.create({rng:source}, {preview:{value:true}}));
 const control=createEquipmentSession(f.engine,definitions,{archivePath:folder,assertCurrent:()=>true},validateEquipmentProfile);
 control.configure({enabled:true});assert.equal(f.engine.makeItem('w:sample',50,5,options).affixes[0].value,20);
 assert.equal(f.engine.makeItem('w:sample',50,5,{rng:null}).affixes[0].value,20);assert.equal(options.rng,source);control.reset();
});
test('equipment explicit source works without changing a readonly default RNG',()=>{
 const f=fixture(),source=()=>.5,native=f.engine.rng;
 Object.defineProperty(f.engine,'rng',{value:native,writable:false,configurable:true});
 const control=createEquipmentSession(f.engine,definitions,{archivePath:folder,assertCurrent:()=>true},validateEquipmentProfile);
 control.configure({enabled:true});assert.equal(f.engine.makeItem('w:sample',50,5,{rng:source}).affixes[0].value,20);
 assert.equal(f.engine.rng,native);assert.equal(control.inspect().blocked,null);control.reset();
});
test('equipment max does not affect non-equipment rewards with an explicit RNG',()=>{
 const f=fixture(),control=createEquipmentSession(f.engine,definitions,{archivePath:folder,assertCurrent:()=>true},validateEquipmentProfile);
 control.configure({enabled:true});const item=f.engine.makeItem('gem:sample',50,5,{rng:()=>.5});
 assert.equal(item.affixes[0].value,15);assert.equal(control.inspect().hits,0);control.reset();
});
test('equipment inactive legacy registry upgrades on read without installing rules',async()=>{
 const f=fixture(),before=f.engine.serialize(),method=f.engine.makeItem;
 const read=await f.run('equipment','inspect'),key=Symbol.for('GameValueEditor.PlayAgainExpedition.EquipmentSession.v1');
 delete f.window[key].implementationVersion;
 const upgraded=await f.run('equipment','inspect');assert.notEqual(upgraded.scopeHash,read.scopeHash);
 assert.equal(upgraded.equipment.report.implementationVersion,5);assert.equal(upgraded.equipment.report.installed,false);
 assert.equal(f.engine.serialize(),before);assert.equal(f.engine.makeItem,method);
});
test('equipment upgrade keeps an applied legacy hook restorable and activates new code only after reset',async()=>{
 const f=fixture(),read=await f.run('equipment','inspect');
 const enabled=await f.run('equipment','configure',{scopeHash:read.scopeHash,revision:0,profile:{enabled:true}});
 const key=Symbol.for('GameValueEditor.PlayAgainExpedition.EquipmentSession.v1'),registry=f.window[key],method=f.engine.makeItem;
 registry.implementationVersion=3; // Applied 0.1.0 generation rules remain owned and restorable.
 const legacy=await f.run('equipment','inspect');assert.equal(legacy.equipment.report.upgradeRequired,true);
 assert.equal(legacy.equipment.report.ready,false);assert.equal(legacy.equipment.report.canRestore,true);assert.equal(f.engine.makeItem,method);
 await assert.rejects(f.run('equipment','configure',{scopeHash:enabled.scopeHash,revision:1,profile:{enabled:true}}),/EQUIPMENT_UPGRADE_REQUIRED/);
 const reset=await f.run('equipment','reset',{scopeHash:legacy.scopeHash,revision:1});assert.equal(reset.equipment.report.installed,false);
 const current=await f.run('equipment','inspect');assert.equal(current.equipment.report.implementationVersion,5);assert.equal(current.equipment.report.ready,true);
 assert.notEqual(current.scopeHash,legacy.scopeHash);assert.equal(current.equipment.revision,0);
 await f.run('equipment','configure',{scopeHash:current.scopeHash,revision:0,profile:{enabled:true}});
 assert.equal(f.engine.makeItem('w:sample',50,5,{rng:()=>.5}).affixes[0].value,20);
});
test('fate and equipment restoration leave the other namespace untouched',async()=>{
 const f=fixture(),fate=await f.run('fate','inspect'),gear=await f.run('equipment','inspect');
 await f.run('fate','configure',{scopeHash:fate.scopeHash,revision:0,profile:blue});
 await f.run('equipment','configure',{scopeHash:gear.scopeHash,revision:0,profile:{enabled:true}});
 await f.run('equipment','reset',{scopeHash:gear.scopeHash,revision:1});
 assert.equal((await f.run('fate','inspect')).fate.report.installed,true);
 assert.equal((await f.run('fate','inspect')).fate.revision,1);
});
test('new accessories change numeric values only and preserve old items and RNG consumption',()=>{
 const f=fixture(),d={...definitions,accessories:true};let calls=0;f.engine.rng=()=>{calls++;return .5;};
 const old=f.engine.buyCostume(),before=JSON.stringify(old),source=f.engine.rng;
 const control=createEquipmentSession(f.engine,d,{archivePath:folder,assertCurrent:()=>true},validateEquipmentProfile);
 const saved=f.engine.serialize();control.configure({enabled:true});assert.equal(f.engine.serialize(),saved);
 calls=0;const badge=f.engine.buyBadge(),costume=f.engine.buyCostume(),gem=f.engine.polishOre(150,5);
 assert.equal(calls,8);for(const row of [badge,costume,gem])assert.equal(row.affixes[0].value,20);
 assert.equal(costume.hue,180);assert.equal(costume.quality,5);assert.equal(JSON.stringify(old),before);
 assert.deepEqual(control.inspect().accessoryHits,{badge:1,costume:1,gem:2});
 const rewarded=f.engine.serialize();control.reset();assert.equal(f.engine.rng,source);assert.equal(f.engine.serialize(),rewarded);
 assert.equal(f.engine.buyBadge().affixes[0].value,15);
});
test('gem base bonus and affixes take native maxima without changing type, fixed bonuses, old gems or RNG count',()=>{
 for(const random of [.1,.5,.9]){
  const f=fixture();let calls=0;f.engine.rng=()=>{calls++;return random;};
  const old=f.engine.polishOre(150,5),oldJson=JSON.stringify(old),nativeCalls=calls;
  const control=createEquipmentSession(f.engine,{...definitions,accessories:true},{archivePath:folder,assertCurrent:()=>true},validateEquipmentProfile);
  control.configure({enabled:true});calls=0;const actual=f.engine.polishOre(150,5);
  assert.equal(actual.type,old.type);assert.equal(actual.bonus,definitions.gemTypes[actual.type].max);assert.equal(actual.affixes[0].value,20);
  assert.equal(calls,nativeCalls);assert.equal(JSON.stringify(old),oldJson);assert.equal(actual.quality,old.quality);assert.equal(actual.tier,old.tier);
  control.reset();const restored=f.engine.polishOre(150,5);assert.equal(restored.bonus,old.bonus);assert.equal(restored.affixes[0].value,old.affixes[0].value);
 }
});
test('gem base mapping matches native module-object calls as well as direct factory calls',()=>{
 const f=fixture(),control=createEquipmentSession(f.engine,{...definitions,accessories:true},{archivePath:folder,assertCurrent:()=>true},validateEquipmentProfile);
 control.configure({enabled:true});const gem=f.engine.wheelGem();assert.equal(gem.type,'opal');assert.equal(gem.bonus,5);assert.equal(gem.affixes[0].value,20);control.reset();
 assert.equal(f.engine.wheelGem().bonus,4);
});
for(const order of ['max-first','probability-first'])test('independent generation/probability restore: '+order,()=>{
 const {createProbabilitySession}=require('../src/probability-session.cjs'),{createProbabilityModel}=require('../src/probability-model.cjs');
 const f=fixture(),source=f.engine.rng,gear=createEquipmentSession(f.engine,{...definitions,accessories:true},{archivePath:folder,assertCurrent:()=>true},validateEquipmentProfile);
 const create=()=>createProbabilitySession(f.engine,createProbabilityModel(),{archivePath:folder,assertCurrent:()=>true});let probability;
 const profile={wheelBonus:{doublePercent:10,marqueePercent:0}};
 if(order==='max-first'){gear.configure({enabled:true});probability=create();probability.configure(profile);}
 else{probability=create();probability.configure(profile);gear.configure({enabled:true});}
 assert.equal(f.engine.spinLuckyWheel().affixes[0].value,20);assert.equal(gear.inspect().blocked,null);assert.equal(probability.inspect().blocked,null);
 gear.reset();assert.equal(f.engine.rng,source);assert.equal(probability.inspect().installed,true);assert.equal(probability.inspect().rngOriginal,true);
 probability.configure(profile);assert.equal(f.engine.spinLuckyWheel().affixes[0].value,15);
 gear.configure({enabled:true});probability.reset();assert.equal(gear.inspect().installed,true);assert.equal(gear.inspect().hooksOwned,true);
 assert.equal(f.engine.spinLuckyWheel().affixes[0].value,20);gear.reset();assert.equal(f.engine.rng,source);
});
test('generation mapping forwards unrelated random draws without checking storage and never restores over a foreign source',()=>{
 const f=fixture();let reads=0;
 const control=createEquipmentSession(f.engine,{...definitions,accessories:true},{archivePath:folder,assertCurrent:()=>{reads++;return true;}},validateEquipmentProfile);
 control.configure({enabled:true});reads=0;assert.equal(f.engine.rng.call({other:true},'native'),.5);assert.equal(reads,0);
 const foreign=()=>.2;f.engine.rng=foreign;assert.throws(()=>control.reset(),/BUSY_OR_CONFLICT/);assert.equal(f.engine.rng,foreign);
});
test('old active probability controller requires explicit independent restore before generation upgrade',async()=>{
 const f=fixture(),key=Symbol.for('GameValueEditor.PlayAgainExpedition.ProbabilitySession.v1');
 f.window[key]={engine:f.engine,implementationVersion:15,control:{inspect:()=>({installed:true})}};
 const read=await f.run('equipment','inspect');assert.equal(read.equipment.report.probabilityUpgradeRequired,true);assert.equal(read.equipment.report.ready,false);
 await assert.rejects(f.run('equipment','configure',{scopeHash:read.scopeHash,revision:0,profile:{enabled:true}}),/EQUIPMENT_PROBABILITY_UPGRADE_REQUIRED/);
 assert.equal(read.equipment.report.installed,false);delete f.window[key];const current=await f.run('equipment','inspect');assert.equal(current.equipment.report.ready,true);
});
test('generation configuration rolls back its RNG if installing the item hook fails',()=>{
 const f=fixture(),source=f.engine.rng;
 const engine=new Proxy(f.engine,{defineProperty(target,key,descriptor){if(key==='makeItem')throw Error('native install failed');return Reflect.defineProperty(target,key,descriptor);}});
 const control=createEquipmentSession(engine,{...definitions,accessories:true},{archivePath:folder,assertCurrent:()=>true},validateEquipmentProfile);
 assert.throws(()=>control.configure({enabled:true}),/native install failed/);assert.equal(f.engine.rng,source);assert.equal(control.inspect().installed,false);
});
