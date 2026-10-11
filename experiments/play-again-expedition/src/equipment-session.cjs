'use strict';
function validateEquipmentProfile(profile) {
  if (!profile || typeof profile !== 'object' || Array.isArray(profile) || Object.keys(profile).length !== 1 || profile.enabled !== true)
    throw Error('EQUIPMENT_PROFILE_INVALID');
  return { enabled: true };
}
// Pure native definitions, imported by the existing main process. No game actions.
function readEquipmentDefinitions(requireModule, archive) {
  const affixes = requireModule(archive + '/runtime/src/affixes.js');
  const sheet = requireModule(archive + '/runtime/src/sheet-affixes.js').SHEET;
  const sample = Object.values(sheet).find(row => /^w:/.test(row.origin) && Number.isFinite(row.min2) && Number.isFinite(row.max2) && row.max2 > row.min2);
  if (!sample || typeof affixes.affixBounds !== 'function') throw Error('EQUIPMENT_CONTRACT_CHANGED');
  const ranges = Object.fromEntries([...new Set([...Object.keys(affixes.AFFIXES), ...Object.keys(sheet)])].map(key => [key,
    Object.fromEntries([2,3,4,5,6].map(quality => [quality, affixes.affixBounds(key, quality)]))]));
  const secondary = Object.fromEntries(Object.values(sheet).filter(row => Number.isFinite(row.min2) && Number.isFinite(row.max2)).map(row => [row.key, [row.min2, row.max2]]));
  const badges=requireModule(archive+'/runtime/src/badges.js'),costumes=requireModule(archive+'/runtime/src/costumes.js'),
    workshop=requireModule(archive+'/runtime/src/workshop.js'),types=requireModule(archive+'/runtime/src/mining-data.js').GEM_TYPES;
  if(typeof badges.rollBadge!=='function'||typeof costumes.rollCostume!=='function'||
    typeof workshop.makeGem!=='function'||!Array.isArray(types)||!types.length||types.length>128||
    types.some(row=>typeof row.key!=='string'||!row.key||!Number.isFinite(row.min)||!Number.isFinite(row.max)||row.min>row.max||!Number.isFinite(row.weight)||row.weight<0)||
    new Set(types.map(row=>row.key)).size!==types.length)throw Error('EQUIPMENT_CONTRACT_CHANGED');
  const gemTypes=Object.fromEntries(types.map(({key,min,max})=>[key,{min,max}])),gemSamples={},total=types.reduce((sum,row)=>sum+row.weight,0);
  if(!Number.isFinite(total)||total<=0)throw Error('EQUIPMENT_CONTRACT_CHANGED');
  let prefix=0;
  for(const row of types){
    if(row.weight>0){const random=(prefix+row.weight/2)/total,item=workshop.makeGem(150,5,0,()=>random);
      if(item?.type!==row.key)throw Error('EQUIPMENT_GEM_CONSUMER_CHANGED');gemSamples[row.key]=random;}
    prefix+=row.weight;
  }
  // Independent pure-factory endpoint reference for copy-only validation. These
  // columns never leave this function and are not renderer hook coordinates.
  const maxima={badge:{},costume:{}},shape=item=>JSON.stringify({...item,affixes:item.affixes.map(({value,value2,...rest})=>rest)});
  for(const kind of ['badge','costume']) {
    const invoke=(rng,q)=>kind==='badge'?Reflect.apply(badges.rollBadge,null,[rng,q]):Reflect.apply(costumes.rollCostume,null,['brokenSword',rng,q]);
    const sites=new Set(),run=(random,q,target)=>{
      const frames=[],rng=function(){const error=new Error();Error.captureStackTrace(error,rng);const frame=error.stack.split('\n')[1];frames.push(frame);return frame===target?1-Number.EPSILON:random;};
      return {item:invoke(rng,q),frames};
    };
    for(const random of [.05,.5,.95]) {
      const q=kind==='badge'?5:6,base=run(random,q);
      for(const frame of new Set(base.frames)) {
        const high=run(random,q,frame);
        if(shape(base.item)===shape(high.item)&&high.item.affixes.some((a,i)=>a.value>base.item.affixes[i].value||a.value2>base.item.affixes[i].value2))sites.add(frame);
      }
    }
    for(const q of kind==='badge'?[3,4,5]:[3,4,5,6]) {
      const table=maxima[kind][q]={};
      for(let i=0;i<128;i++) {
        const rng=function(){const error=new Error();Error.captureStackTrace(error,rng);return sites.has(error.stack.split('\n')[1])?1-Number.EPSILON:(i+.5)/128;};
        for(const a of invoke(rng,q).affixes)if(Number.isFinite(a.value))table[a.key]=Math.max(table[a.key]??-Infinity,a.value);
      }
    }
  }
  return { sample: { key: sample.key, origin: sample.origin }, ranges, secondary, accessories:true, maxima,gemTypes,gemSamples };
}
function createEquipmentSession(engine, definitions, context, validate) {
  const fail = code => { throw Error(code); };
  // A bundled factory can be called directly or via its exports object. Those
  // display aliases refer to the same dynamically discovered numeric location.
  const normalize = value => value.replaceAll('\\','/').toLowerCase().replace('at object.makegem (','at makegem (');
  if (typeof engine?.makeItem !== 'function' || typeof engine.rng !== 'function' || typeof engine.serialize !== 'function' ||
    typeof context?.assertCurrent !== 'function' || !definitions?.sample?.origin || !definitions?.ranges || !definitions?.secondary) fail('EQUIPMENT_CONTRACT_CHANGED');
  const original = engine.makeItem, own = Object.getOwnPropertyDescriptor(engine, 'makeItem'), state = engine.state;
  const Engine = Object.getPrototypeOf(engine)?.constructor;
  if (typeof Engine !== 'function' || Engine.name !== 'Engine') fail('EQUIPMENT_CONTRACT_CHANGED');
  const sites = new Set(), archive = normalize(context.archivePath) + '/';
  const rng = function () {
    const error = new Error(); Error.captureStackTrace(error,rng);
    const frame=normalize(error.stack.split('\n')[1]??'');
    // The real preview bundle inlines these functions; the pure runtime modules
    // keep them in separate files. Discover on THIS Engine, not an assumed file.
    if (frame.includes(archive) && (frame.includes('at rollaffix (') || frame.includes('at roll ('))) sites.add(frame);
    return .5;
  };
  const copy = new Engine(engine.serialize(),rng);
  if(copy===engine||copy.state===state) fail('EQUIPMENT_COPY_NOT_ISOLATED');
  const test = copy.makeItem(definitions.sample.origin,50,5);
  if (sites.size !== 2 || !test.affixes.some(affix => affix.key === definitions.sample.key && Number.isFinite(affix.value2))) fail('EQUIPMENT_CONSUMER_CHANGED');
  // Classify CURRENT native RNG sites by isolated, one-site comparisons. Never
  // guess columns or confuse tier/key/hue/bond/mutation selection with values.
  const accessorySites=new Map(),gemBaseSites=new Set(),generatedKey=Symbol.for('GameValueEditor.PlayAgainExpedition.GeneratedRng.v1');
  const cacheKey=Symbol.for('gve.generated.discovery.cache');
  if(!Object.hasOwn(createEquipmentSession,cacheKey))Object.defineProperty(createEquipmentSession,cacheKey,{value:new WeakMap()});
  const cache=createEquipmentSession[cacheKey],cached=cache.get(Engine),methods=['buyBadge','buyCostume','polishOre'].map(name=>Engine.prototype[name]);
  const reuse=cached&&cached.archive===archive&&cached.methods.every((method,i)=>method===methods[i]);
  if(definitions.accessories===true&&reuse){for(const [site,kind]of cached.sites)accessorySites.set(site,kind);for(const site of cached.gemBaseSites)gemBaseSites.add(site);}
  if(definitions.accessories===true&&!reuse) {
    if(['buyBadge','buyCostume','polishOre'].some(name=>typeof engine[name]!=='function')||!definitions.gemTypes||!definitions.gemSamples)fail('EQUIPMENT_CONTRACT_CHANGED');
    // Bonus is a separate gem base value, not an affix. All other root fields
    // (especially type/quality) and affix choices must still remain identical.
    const snapshot=engine.serialize(),shape=(item,kind)=>JSON.stringify({...item,...(kind==='gem'?{bonus:undefined}:{}),affixes:item.affixes.map(({value,value2,...rest})=>rest)});
    const run=(kind,random,overrides=new Map())=>{
      const frames=[];
      const source=function(){const error=new Error();Error.captureStackTrace(error,source);const frame=normalize(error.stack.split('\n')[1]??'');frames.push(frame);return overrides.get(frame)??random;};
      const copy=new Engine(snapshot,source);
      if(copy===engine||copy.state===state)fail('EQUIPMENT_COPY_NOT_ISOLATED');
      copy.phase='idle';copy.state.badges=[];copy.state.costumes=[];copy.state.gems=[];
      for(const key of Object.keys(copy.state.materials))copy.state.materials[key]=100000;
      frames.length=0;
      let result,collection;
      if(kind==='badge'){result=copy.buyBadge();collection=copy.state.badges;}
      else if(kind==='costume'){result=copy.buyCostume();collection=copy.state.costumes;}
      else{copy.state.ores=[{tier:150,quality:5,count:1}];result=copy.polishOre(150,5);collection=copy.state.gems;}
      const item=collection.find(row=>row.uid===result?.uid);
      if(!item||!Array.isArray(item.affixes)||!item.affixes.length)fail('EQUIPMENT_ACCESSORY_CONSUMER_CHANGED');
      return {item,frames};
    };
    for(const kind of ['badge','costume','gem']) {
      const first=run(kind,.5),qualitySite=kind==='costume'?first.frames.find(frame=>frame.includes('at rollcostume (')):null;
      for(const random of kind==='costume'?[.05,.5,.95]:kind==='gem'?[...new Set([.5,...Object.values(definitions.gemSamples)])]:[.5]) {
        const overrides=new Map(qualitySite?[[qualitySite,1-Number.EPSILON]]:[]),base=run(kind,random,overrides);
        for(const frame of new Set(base.frames.filter(frame=>frame.includes(archive)&&
          (kind==='badge'?frame.includes('at rollbadge ('):kind==='costume'?frame.includes('at rollcostume ('):
          frame.includes('at rollaffix (')||frame.includes('at roll (')||/at (?:object\.)?makegem \(/.test(frame))))) {
          if(frame===qualitySite||accessorySites.has(frame))continue;
          const high=run(kind,random,new Map([...overrides,[frame,1-Number.EPSILON]]));
          const baseIncreased=kind==='gem'&&Number.isFinite(base.item.bonus)&&high.item.bonus>base.item.bonus&&
            high.item.bonus===definitions.gemTypes[base.item.type]?.max;
          if(shape(base.item,kind)===shape(high.item,kind)&&(baseIncreased||high.item.affixes.some((affix,i)=>affix.value>base.item.affixes[i].value||affix.value2>base.item.affixes[i].value2))){
            accessorySites.set(frame,kind);if(baseIncreased)gemBaseSites.add(frame);
          }
        }
      }
      if(![...accessorySites.values()].includes(kind))fail('EQUIPMENT_ACCESSORY_CONSUMER_CHANGED');
    }
    if(Object.entries(definitions.gemTypes).some(([key,row])=>Object.hasOwn(definitions.gemSamples,key)&&row.max>row.min)&&!gemBaseSites.size)fail('EQUIPMENT_GEM_CONSUMER_CHANGED');
    cache.set(Engine,{archive,methods,sites:[...accessorySites],gemBaseSites:[...gemBaseSites]});
  }
  let installed=false,active=false,blocked=null,hits=0,accessoryHits={badge:0,costume:0,gem:0};
  const nativeRng=engine.rng,rngOwn=Object.getOwnPropertyDescriptor(engine,'rng');
  const current=()=>{try{return engine.state===state&&context.assertCurrent()===true;}catch{return false;}};
  const owned=()=>!installed||(Object.getOwnPropertyDescriptor(engine,'makeItem')?.value===make&&
    (!accessorySites.size||Object.getOwnPropertyDescriptor(engine,'rng')?.value===generatedRng));
  const ready=()=>current()&&owned()&&!active&&engine.phase==='idle'&&!state.luckyWheel?.pending;
  function generatedRng(...args) {
    const value=Reflect.apply(nativeRng,this,args);
    if(!installed||blocked)return value;
    const error=new Error();Error.captureStackTrace(error,generatedRng);
    // Probability/fate/gear wrappers can sit above this per-engine source.
    // Select the nearest native frame, not an arbitrary later ancestor.
    const frames=error.stack.split('\n').slice(1).map(normalize),frame=frames.find(row=>row.includes(archive));
    const gemFactory=frames.some(row=>row.includes(archive)&&/at (?:object\.)?makegem \(/.test(row));
    const kind=accessorySites.get(frame)??(gemFactory&&sites.has(frame)?'gem':null);
    if(!kind||kind==='gem'&&!gemFactory||!current())return value;
    if(!Number.isFinite(value)||value<0||value>=1){blocked='EQUIPMENT_RNG_CHANGED';return value;}
    hits++;accessoryHits[kind]++;return 1-Number.EPSILON;
  }
  Object.defineProperty(generatedRng,generatedKey,{value:Object.freeze({base:nativeRng,owner:engine,current:()=>installed&&current()})});
  function make(...args) {
    if (this!==engine||!installed||!current()||Object.getOwnPropertyDescriptor(engine,'makeItem')?.value!==make||blocked||active||typeof args[0]!=='string'||!/^([wa]):/.test(args[0])) return Reflect.apply(original,this,args);
    // Wheel rewards pass their captured RNG explicitly. That source takes
    // precedence over engine.rng in the native formula, even for preview items.
    const options=args[3], supplied=options?.rng;
    const explicit=typeof supplied==='function', previous=explicit?supplied:engine.rng;
    if(typeof previous!=='function'||!explicit&&Object.getOwnPropertyDescriptor(engine,'rng')?.writable!==true){blocked='EQUIPMENT_RNG_CHANGED';return Reflect.apply(original,this,args);}
    const mapped=function(...rngArgs){
      const value=Reflect.apply(previous,this,rngArgs);
      const error=new Error();Error.captureStackTrace(error,mapped);
      if(!sites.has(normalize(error.stack.split('\n')[1]??''))) return value;
      hits++; return 1-Number.EPSILON;
    };
    if(explicit) {
      // Never mutate a native options object (it may be frozen or reused).
      const descriptors=Object.getOwnPropertyDescriptors(options);
      descriptors.rng={value:mapped,writable:true,enumerable:descriptors.rng?.enumerable??true,configurable:true};
      args[3]=Object.create(Object.getPrototypeOf(options),descriptors);
    }
    active=true;if(!explicit)engine.rng=mapped;
    try{return Reflect.apply(original,this,args);}
    finally{if(!explicit){if(engine.rng===mapped)engine.rng=previous;else blocked='EQUIPMENT_RNG_CHANGED';}active=false;}
  }
  function inspect(){return {installed,profile:installed?{enabled:true}:null,hits,blocked,ready:ready()&&!blocked,canRestore:ready(),
    ownerCurrent:current(),hooksOwned:owned(),newEquipmentOnly:true,doubleValues:true,explicitRng:true,
    generatedKinds:accessorySites.size?['gear','gem','costume','badge']:['gear'],gemBase:definitions.accessories===true,accessoryHits:{...accessoryHits},implementationVersion:5};}
  return {inspect,configure(profile){validate(profile);if(!ready()||blocked)fail('EQUIPMENT_BUSY_OR_CONFLICT');
    if(!installed){if(engine.makeItem!==original||own&&own.configurable!==true||accessorySites.size&&(engine.rng!==nativeRng||rngOwn?.writable!==true))fail('EQUIPMENT_HOOK_CONFLICT');
      try {
        if(accessorySites.size)engine.rng=generatedRng;
        Object.defineProperty(engine,'makeItem',{value:make,writable:true,configurable:true});installed=true;
      }catch(error){if(accessorySites.size&&engine.rng===generatedRng)Object.defineProperty(engine,'rng',rngOwn);throw error;}
    }return inspect();},
    reset(){if(!ready())fail('EQUIPMENT_BUSY_OR_CONFLICT');if(installed){if(own)Object.defineProperty(engine,'makeItem',own);else delete engine.makeItem;}
      if(installed&&accessorySites.size)Object.defineProperty(engine,'rng',rngOwn);
      installed=false;blocked=null;return inspect();}};
}
module.exports={validateEquipmentProfile,readEquipmentDefinitions,createEquipmentSession};
