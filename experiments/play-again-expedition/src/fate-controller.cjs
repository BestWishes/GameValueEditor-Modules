'use strict';
const { validateFateProfile, createFateSession } = require('./fate-session.cjs');
const { assertReadonlyContext } = require('./read-context-contract.cjs');
const { validateEquipmentProfile, readEquipmentDefinitions, createEquipmentSession } = require('./equipment-session.cjs');
const { createEquipmentWheelProbe } = require('./equipment-wheel-validation.cjs');
const { createGeneratedItemProbe } = require('./generated-item-validation.cjs');
const { createProbabilityModel } = require('./probability-model.cjs');
const { createProbabilitySession } = require('./probability-session.cjs');
const { discoverProbabilitySites } = require('./probability-sites.cjs');
const { createWheelRngBridge } = require('./wheel-rng-bridge.cjs');

// Shared module-local lifecycle; each rule has its own registry and native factory.
function createFateRuntime(sessionFactory, validate, kind = 'fate', wheelProbe = null,generatedProbe=null) {
  const key = Symbol.for('GameValueEditor.PlayAgainExpedition.' + (kind === 'fate' ? 'Fate' : 'Equipment') + 'Session.v1');
  return function operate(operation, input, archivePath, qualities) {
    const fail = code => { throw Error(code); };
    if(input.area!==undefined&&(kind!=='equipment'||operation!=='probe'||!['gear','shop','wheel','gem-base','gem-wheel'].includes(input.area)))fail('UNKNOWN_FATE_ARGUMENT');
    const game = window.expedition, engine = game?.engine, state = engine?.state, platform = game?.platform;
    if (game?.getUi?.()?.entered !== true) fail('GAME_NOT_ENTERED');
    if (game.getUi()?.gameMode !== 'expedition') fail('GAME_MODE_NOT_SUPPORTED');
    if (!state || typeof platform?.read !== 'function' || typeof engine.serialize !== 'function') fail('FATE_CONTRACT_CHANGED');
    const storedRaw = platform.read(), stored = JSON.parse(storedRaw);
    if (typeof stored?.rewardClockId !== 'string' || !stored.rewardClockId || state.rewardClockId !== stored.rewardClockId ||
      localStorage.length > 128 || !Number.isFinite(window.performance?.timeOrigin)) fail('LIVE_SAVE_IDENTITY_CHANGED');
    const slots = [];
    for (let i = 0; i < localStorage.length; i++) {
      const slot = localStorage.key(i);
      if (/^zseb-expedition-v1(?:-slot-[1-4])?$/.test(slot) && localStorage.getItem(slot) === storedRaw) slots.push(slot);
    }
    if (slots.length !== 1) fail('ACTIVE_SAVE_AMBIGUOUS');
    const slot = slots[0], clock = state.rewardClockId;
    const current = () => window.expedition === game && game.engine === engine && engine.state === state && game.platform === platform &&
      game.getUi()?.entered === true && game.getUi()?.gameMode === 'expedition' && state.rewardClockId === clock &&
      localStorage.getItem(slot) === platform.read();
    const descriptor = Object.getOwnPropertyDescriptor(window, key);
    if (descriptor && (!Object.hasOwn(descriptor, 'value') || !descriptor.configurable || !descriptor.writable)) fail('FATE_REGISTRY_CONFLICT');
    let registry = descriptor?.value;
    if (registry && (registry.brand !== 'gve-' + kind + '-v1' || !Number.isSafeInteger(registry.revision) || !Number.isSafeInteger(registry.generation))) fail('FATE_REGISTRY_CONFLICT');
    const generation = registry?.generation ?? 0;
    const implementationVersion=kind==='equipment'?5:1;
    const legacyInactive=registry&&kind==='equipment'&&registry.implementationVersion!==implementationVersion&&
      registry.control.inspect().installed===false&&registry.control.inspect().hooksOwned===true;
    if (!registry || registry.engine !== engine || registry.platform !== platform || registry.state !== state || registry.slot !== slot || registry.clock !== clock ||
      registry.origin !== window.performance.timeOrigin||legacyInactive) {
      if (operation !== 'inspect' && operation !== 'probe') fail('READ_RECEIPT_SCOPE_CHANGED');
      const control = sessionFactory(engine, qualities, { archivePath, assertCurrent: current }, validate);
      registry = { brand: 'gve-' + kind + '-v1', engine, platform, state, slot, clock, origin: window.performance.timeOrigin,
        generation: generation + 1, revision: 0, implementationVersion, control };
      Object.defineProperty(window, key, { value: registry, configurable: true, writable: true });
    }
    const anchor = JSON.stringify([kind, slot, clock, registry.origin, registry.generation]);
    const upgradeRequired=kind==='equipment'&&registry.implementationVersion!==implementationVersion;
    const probabilityRegistry=kind==='equipment'?window[Symbol.for('GameValueEditor.PlayAgainExpedition.ProbabilitySession.v1')]:null;
    const probabilityUpgradeRequired=probabilityRegistry?.engine===engine&&probabilityRegistry.implementationVersion!==16&&probabilityRegistry.control.inspect().installed===true;
    if (operation === 'configure' || operation === 'reset') {
      if (input.expectedAnchor !== anchor || input.expectedRevision !== registry.revision) fail('READ_RECEIPT_SCOPE_CHANGED');
      if(operation==='configure'&&upgradeRequired)fail('EQUIPMENT_UPGRADE_REQUIRED');
      if(operation==='configure'&&probabilityUpgradeRequired)fail('EQUIPMENT_PROBABILITY_UPGRADE_REQUIRED');
      if (operation === 'configure') registry.control.configure(input.profile); else registry.control.reset();
      registry.revision++;
    }
    let diagnostic = null;
    if (operation === 'probe' && kind === 'fate') {
      const before = engine.serialize(), storedBefore = platform.read(), Engine = Object.getPrototypeOf(engine).constructor;
      let checks = 0;
      const ensure = condition => { if (!condition) fail('FATE_NATIVE_VALIDATION_FAILED'); checks++; };
      for (const quality of [3,4,5,6]) for (const count of [1,10]) for (const random of [0,0.1,0.5,0.999999]) {
        const copy = new Engine(before, () => random), id = copy.state.owned[0];
        copy.state.materials.immortalShards = 100; copy.state.fatePity = 0; copy.phase = 'idle';
        const control = sessionFactory(copy, qualities, { archivePath, assertCurrent: () => true }, validate), rng = copy.rng;
        const profile = Object.fromEntries(['blue','purple','immortal','mythic'].map((key,i)=>[key,i+3===quality?100:0]));
        const snapshot = copy.serialize(); control.configure(profile); ensure(copy.serialize() === snapshot);
        const result = count === 1 ? [copy.rollFate(id)] : copy.rollFateTen(id);
        ensure(result.length === count && result.every(row => row?.quality === quality));
        ensure(copy.state.materials.immortalShards === 100-count && copy.rng === rng && control.inspect().hits === count && !control.inspect().blocked);
        ensure(copy.fateChoices(id).length === count);
        const rewarded = copy.serialize(); control.reset(); ensure(copy.serialize() === rewarded);
      }
      const copy = new Engine(before, () => 0.1), id = copy.state.owned[0];
      copy.phase = 'idle'; copy.state.materials.immortalShards = 100; copy.state.fatePity = 99;
      const control = sessionFactory(copy, qualities, { archivePath, assertCurrent: () => true }, validate);
      control.configure({blue:100,purple:0,immortal:0,mythic:0});
      const result = copy.rollFateTen(id); ensure(result[0].quality === 6 && result.slice(1).every(row=>row.quality===3));
      ensure(copy.state.fatePity === 9 && control.inspect().hits === 9); control.reset();
      ensure(engine.serialize() === before && platform.read() === storedBefore && current());
      diagnostic = { passedChecks: checks, copyOnly: true, originalGameActionCalled: false, nativeSaveCalled: false };
    }
    if (operation === 'probe' && kind === 'equipment') {
      const before=engine.serialize(),storedBefore=platform.read(),Engine=Object.getPrototypeOf(engine).constructor;
      const prototypeBefore=Object.getOwnPropertyDescriptors(Engine.prototype);
      let checks=0;
      const ensure=condition=>{if(!condition)fail('EQUIPMENT_NATIVE_VALIDATION_FAILED');checks++;};
      const items=[{key:qualities.sample.origin,tier:50,quality:5}, ...state.inventory.filter(item=>/^([wa]):/.test(item.key))].slice(0,30);
      const area=input.area??'gear';
      for(const item of area==='gear'?items:[]) for(const random of [0,.5,.999999]) for(const explicit of [false,true]) {
        const baseline=new Engine(before,()=>random),copy=new Engine(before,()=>random),rng=copy.rng;
        const control=sessionFactory(copy,qualities,{archivePath,assertCurrent:()=>true},validate);
        const snapshot=copy.serialize();control.configure({enabled:true});ensure(copy.serialize()===snapshot);
        const options=explicit?Object.freeze({preview:false,rng:()=>random}):undefined;
        const native=baseline.makeItem(item.key,item.tier,item.quality,options),actual=copy.makeItem(item.key,item.tier,item.quality,options);
        ensure(native.key===actual.key&&native.quality===actual.quality&&native.affixes.length===actual.affixes.length&&
          native.affixes.every((affix,i)=>affix.key===actual.affixes[i].key));
        ensure(actual.affixes.every(affix=>{
          const range=qualities.ranges[affix.key]?.[actual.quality],second=qualities.secondary[affix.key];
          return Array.isArray(range)&&affix.value===range[1]&&(affix.value2===undefined||Array.isArray(second)&&affix.value2===second[1]);
        }));
        ensure(control.inspect().hooksOwned&&!control.inspect().blocked);
        // Reload only this generated copy; the original inventory is untouched.
        copy.state.inventory.push(actual); const serialized=copy.serialize();
        const reloaded=new Engine(serialized,()=>random).itemByUid(actual.uid);
        ensure(JSON.stringify(reloaded.affixes)===JSON.stringify(actual.affixes));
        control.reset();ensure(copy.serialize()===serialized&&copy.rng===rng);
      }
      ensure(engine.serialize()===before&&platform.read()===storedBefore&&current());
      const wheel=area==='gear'&&typeof wheelProbe==='function'?wheelProbe(Engine,before,sessionFactory,validate,qualities,archivePath):null;
      const generated=area!=='gear'&&typeof generatedProbe==='function'?generatedProbe(Engine,before,sessionFactory,validate,qualities,archivePath,area):null;
      ensure(Object.keys(prototypeBefore).length===Object.getOwnPropertyNames(Engine.prototype).length&&
        Object.entries(prototypeBefore).every(([key,row])=>{const now=Object.getOwnPropertyDescriptor(Engine.prototype,key);
          return now&&row.value===now.value&&row.get===now.get&&row.set===now.set&&row.writable===now.writable&&row.configurable===now.configurable&&row.enumerable===now.enumerable;}));
      ensure(engine.serialize()===before&&platform.read()===storedBefore&&current());
      diagnostic={passedChecks:checks,area,wheel,generated,copyOnly:true,originalGameActionCalled:false,nativeSaveCalled:false};
    }
    const report=registry.control.inspect();
    if(upgradeRequired){report.upgradeRequired=true;report.ready=false;}
    if(probabilityUpgradeRequired){report.probabilityUpgradeRequired=true;report.ready=false;}
    return { anchor, revision: registry.revision, readOnly: ['inspect','probe'].includes(operation), sessionOnly: true,
      gameActionCalled: false, nativeSaveCalled: false, report, diagnostic };
  };
}

function buildSessionRuleRequest(config, operation, input = {}, kind = 'fate') {
  if(!['fate','equipment'].includes(kind))throw Error('UNKNOWN_RULE_KIND');
  if (!['inspect','probe','configure','reset'].includes(operation)) throw Error('UNKNOWN_FATE_OPERATION');
  if (!Number.isSafeInteger(config?.processId) || config.processId <= 0 ||
    ['executablePath','archivePath','profilePath'].some(key=>typeof config[key] !== 'string' || !config[key])) throw Error('INVALID_CONTROLLER_CONTEXT');
  const readonly = ['inspect','probe'].includes(operation), allowed = readonly ? kind==='equipment'&&operation==='probe'?['area']:[] : operation === 'configure' ? ['profile','scopeHash','revision'] : ['scopeHash','revision'];
  if (!input || typeof input !== 'object' || Array.isArray(input) || Object.keys(input).some(key=>!allowed.includes(key))) throw Error('UNKNOWN_FATE_ARGUMENT');
  if(input.area!==undefined&&!['gear','shop','wheel','gem-base','gem-wheel'].includes(input.area))throw Error('UNKNOWN_FATE_ARGUMENT');
  if (!readonly && (!/^[a-f0-9]{64}$/.test(input.scopeHash) || !Number.isSafeInteger(input.revision) || input.revision<0)) throw Error('INVALID_CONTROLLER_SCOPE');
  const factory=kind==='fate'?createFateSession:createEquipmentSession,validate=kind==='fate'?validateFateProfile:validateEquipmentProfile;
  if (operation === 'configure') validate(input.profile);
  const strict=f=>`(function(){'use strict';return (${f.toString()});})()`;
  // Only the fixed wheel bridge is deliberately non-strict. All rule factories
  // keep strict semantics, without eval/Function or a CSP exception.
  const wheelProbe=kind==='equipment'&&operation==='probe'
    ? `${strict(createEquipmentWheelProbe)}(${strict(createProbabilityModel)},${strict(createProbabilitySession)},${strict(discoverProbabilitySites)},(${createWheelRngBridge.toString()}))`:'null';
  const generatedProbe=kind==='equipment'&&operation==='probe'
    ? `${strict(createGeneratedItemProbe)}(${strict(createProbabilityModel)},${strict(createProbabilitySession)},${strict(discoverProbabilitySites)},(${createWheelRngBridge.toString()}))`:'null';
  const runtime = `${strict(createFateRuntime)}(${strict(factory)},${strict(validate)},${JSON.stringify(kind)},${wheelProbe},${generatedProbe})`;
  const expression = `(async function(){'use strict';
    const config = ${JSON.stringify(config)}, operation = ${JSON.stringify(operation)}, input = ${JSON.stringify(input)};
    if(process.pid!==config.processId||process.execPath!==config.executablePath||!process.versions.electron||process.arch!=='x64') throw Error('SESSION_BUILD_CHANGED');
    const electron=process.mainModule.require('electron');
    if(electron.app.getPath('userData')!==config.profilePath) throw Error('ORIGINAL_PROFILE_CHANGED');
    const windows=electron.BrowserWindow.getAllWindows().filter(w=>!w.isDestroyed()&&w.webContents.getURL()==='expedition://game/index.html');
    if(windows.length!==1) throw Error('ORIGINAL_WINDOW_AMBIGUOUS');
    const contents=windows[0].webContents;
    const context=(${assertReadonlyContext.toString()})(contents.getLastWebPreferences(),electron.app.getAppPath(),config.archivePath);
    const qualities=${kind==='fate' ? "process.mainModule.require(config.archivePath+'/runtime/src/fate-data.js').FATE_QUALITIES.map(row=>({quality:row.quality,name:row.name,chance:row.chance}))" : `(${readEquipmentDefinitions.toString()})(name=>process.mainModule.require(name),config.archivePath)`};
    const hash=value=>process.mainModule.require('node:crypto').createHash('sha256').update(value).digest('hex');
    const call=async(op,args)=>{
      const code="(()=>{try{return {ok:true,result:("+${JSON.stringify(runtime)}+")("+JSON.stringify(op)+","+JSON.stringify(args)+","+JSON.stringify(config.archivePath)+","+JSON.stringify(qualities)+")};}catch(error){return {ok:false,error:String(error.message)};}})()";
      const packet=await contents.executeJavaScriptInIsolatedWorld(999,[{code}],false);
      if(packet?.ok!==true) throw Error(packet?.error||'RENDERER_RESULT_UNCONFIRMED'); return packet.result;
    };
    const read=await call(${JSON.stringify(readonly ? operation : 'inspect')},${readonly?'input':'{}'}),scopeHash=hash(read.anchor);
    if(operation==='inspect'||operation==='probe'){delete read.anchor;return {${kind}:read,scopeHash,context};}
    if(scopeHash!==input.scopeHash||read.revision!==input.revision) throw Error('READ_RECEIPT_SCOPE_CHANGED');
    const result=await call(operation,{expectedAnchor:read.anchor,expectedRevision:input.revision,...(operation==='configure'?{profile:input.profile}:{})});
    if(hash(result.anchor)!==scopeHash||result.revision!==input.revision+1) throw Error('FATE_REVISION_UNCONFIRMED');
    delete result.anchor; return {${kind}:result,scopeHash,context};
  })()`;
  return {id:402,method:'Runtime.evaluate',params:{expression,awaitPromise:true,returnByValue:true,timeout:5000}};
}
const buildFateRequest=(config,operation,input)=>buildSessionRuleRequest(config,operation,input,'fate');
const buildEquipmentRequest=(config,operation,input)=>buildSessionRuleRequest(config,operation,input,'equipment');
module.exports = { buildFateRequest, buildEquipmentRequest, createFateRuntime };
