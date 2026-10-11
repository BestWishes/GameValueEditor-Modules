'use strict';
// Copies of the CURRENT native Engine, never actions on the original character.
function createGeneratedItemProbe(modelFactory,probabilityFactory,discover,bridgeFactory) {
  return function verify(Engine,snapshot,equipmentFactory,validate,definitions,archivePath,area='all') {
    let checks=0,cases=0;const counts={badge:0,costume:0,gem:0},gemBaseTypes={};
    const ensure=(ok,label)=>{if(!ok)throw Error('GENERATED_ITEM_'+label.toUpperCase()+'_FAILED');checks++;};
    const shape=(item,kind)=>JSON.stringify({...item,...(kind==='gem'?{bonus:undefined}:{}),affixes:item.affixes.map(({value,value2,...rest})=>rest)});
    const collection=(copy,kind)=>copy.state[kind==='badge'?'badges':kind==='costume'?'costumes':kind==='gem'?'gems':'inventory'];
    const item=(copy,kind,uid)=>collection(copy,kind).find(row=>row.uid===uid);
    const maximum=(kind,row)=>{if(kind==='gem'){
      ensure(Number.isFinite(row.bonus)&&row.bonus===definitions.gemTypes[row.type]?.max,'base_'+row.type);
      gemBaseTypes[row.type]=(gemBaseTypes[row.type]??0)+1;
    }for(const a of row.affixes) {
      const max=kind==='gem'?definitions.ranges[a.key]?.[row.quality]?.[1]:definitions.maxima[kind]?.[row.quality]?.[a.key];
      if(Number.isFinite(a.value))ensure(Number.isFinite(max)&&a.value===max,'max_'+kind+'_'+a.key);
      if(a.value2!==undefined)ensure(a.value2===definitions.secondary[a.key]?.[1],'secondary');
    }};
    const prepare=copy=>{copy.phase='idle';for(const key of Object.keys(copy.state.materials))copy.state.materials[key]=100000;};
    const unchanged=(copy,before)=>before.every(([kind,uid,json])=>JSON.stringify(item(copy,kind,uid))===json);
    const old=copy=>['gear','badge','costume','gem'].flatMap(kind=>collection(copy,kind).map(row=>[kind,row.uid,JSON.stringify(row)]));
    const compare=(native,edited,kind,uid)=>{
      const a=item(native,kind,uid),b=item(edited,kind,uid);ensure(a&&b,'stored_item');
      ensure(shape(a,kind)===shape(b,kind),'only_values');maximum(kind,b);counts[kind]++;
    };
    // Cover ordinary attributes, bonds and all rarity-selected costume variants.
    for(const kind of ['wheel','gem-wheel'].includes(area)?[]:area==='gem-base'?['gem']:['badge','costume','gem'])for(const quality of kind==='gem'?[3,4,5,6]:[null])for(const random of area==='gem-base'?Object.values(definitions.gemSamples):[.01,.1,.3,.5,.7,.9,.99,.999999])for(const batch of [false,true]) {
      let aCalls=0,bCalls=0;
      const native=new Engine(snapshot,()=>{aCalls++;return random;}),edited=new Engine(snapshot,()=>{bCalls++;return random;});
      for(const copy of [native,edited])prepare(copy);
      if(kind==='gem')for(const copy of [native,edited])copy.state.ores=[{tier:150,quality,count:10}];
      const savedBefore=edited.serialize(),previous=old(edited),rng=edited.rng;
      const control=equipmentFactory(edited,definitions,{archivePath,assertCurrent:()=>true},validate);
      control.configure({enabled:true});ensure(edited.serialize()===savedBefore,'configure');
      const idsA=new Set(collection(native,kind).map(row=>row.uid)),idsB=new Set(collection(edited,kind).map(row=>row.uid));
      const action=copy=>kind==='gem'?(batch?copy.polishOres(150,quality,10):copy.polishOre(150,quality)):
        kind==='badge'?(batch?copy.buyBadges(10):copy.buyBadge()):(batch?copy.buyCostumes(10):copy.buyCostume());
      action(native);action(edited);
      const rowsA=collection(native,kind).filter(row=>!idsA.has(row.uid)),rowsB=collection(edited,kind).filter(row=>!idsB.has(row.uid));
      ensure(rowsA.length===rowsB.length&&rowsB.length>0,'count');ensure(aCalls===bCalls,'random_calls');
      for(const row of rowsB)compare(native,edited,kind,row.uid);
      ensure(JSON.stringify(native.state.materials)===JSON.stringify(edited.state.materials),'cost');
      ensure(unchanged(edited,previous)&&control.inspect().hooksOwned&&!control.inspect().blocked,'existing');
      const saved=edited.serialize(),reload=new Engine(saved,()=>random);
      for(const row of rowsB)ensure(JSON.stringify(item(reload,kind,row.uid))===JSON.stringify(item(edited,kind,row.uid)),'reload');
      control.reset();ensure(edited.serialize()===saved&&edited.rng===rng,'reset');cases++;
    }
    if(['shop','gem-base'].includes(area)){
      ensure(counts.gem>0&&(area==='gem-base'||counts.badge>0&&counts.costume>0),'coverage');
      if(area==='gem-base')ensure(Object.keys(definitions.gemSamples).every(key=>gemBaseTypes[key]>0),'base_type_coverage');
      return {passedChecks:checks,cases,generatedItems:counts,gemBaseTypes,copyOnly:true};
    }
    // Also test the complete native wheel award/settlement/reload consumer.
    const model=modelFactory(),source=new Engine(snapshot,()=>.5);prepare(source);source.prepareLuckyWheel();
    const sites=discover(source,model,archivePath).sites,wheelKinds={badge:0,costume:0,gem:0};
    const wheelCases=[];
    if(area!=='gem-wheel')for(const random of [.05,.5,.95])for(const count of [1,10])for(const bonus of ['none','double','marquee'])for(const order of ['max-first','probability-first'])wheelCases.push({random,count,bonus,order});
    for(const target of area==='gem-wheel'?['gem']:['badge','costume','gem']) {
      const prize=source.state.luckyWheel.board.find(row=>row.kind===target);
      if(prize)for(const random of area==='gem-wheel'?Object.values(definitions.gemSamples):[.5])for(const count of [1,10])for(const bonus of ['none','double','marquee'])wheelCases.push({random,count,bonus,order:'probability-first',target,group:model.groupOf(prize)});
    }
    const targetedWheelItems={badge:0,costume:0,gem:0};
    for(const {random,count,bonus,order,target,group}of wheelCases) {
      let aCalls=0,bCalls=0;const native=new Engine(snapshot,()=>{aCalls++;return random;}),edited=new Engine(snapshot,()=>{bCalls++;return random;});
      for(const copy of [native,edited]){prepare(copy);copy.prepareLuckyWheel();}
      const nativeWheel=probabilityFactory(native,model,{archivePath,sites,assertCurrent:()=>true},bridgeFactory);
      const createWheel=()=>probabilityFactory(edited,model,{archivePath,sites,assertCurrent:()=>true},bridgeFactory);
      const control=equipmentFactory(edited,definitions,{archivePath,assertCurrent:()=>true},validate);
      const profile={wheelBonus:{doublePercent:bonus==='double'?100:0,marqueePercent:bonus==='marquee'?100:0}};
      if(target)profile.wheel={groupPercent:Object.fromEntries(['ordinary','immortal','mythic','protection','sacredProtection'].map(key=>[key,key===group?100:0])),
        typeWeights:Object.fromEntries(['gear','badge','gem','costume','material'].map(key=>[key,key===target?1:0]))};
      let editedWheel;
      if(order==='max-first'){control.configure({enabled:true});editedWheel=createWheel();editedWheel.configure(profile);}
      else{editedWheel=createWheel();editedWheel.configure(profile);control.configure({enabled:true});}
      nativeWheel.configure(profile);const previous=old(edited);
      const a=native.spinLuckyWheel(count),b=edited.spinLuckyWheel(count);ensure(a.ok===true&&b.ok===true,'wheel_draw');
      ensure(aCalls===bCalls,'wheel_random_calls');ensure(JSON.stringify(native.state.luckyWheel.pending.spins)===JSON.stringify(edited.state.luckyWheel.pending.spins),'wheel_choices');
      const rowsA=native.luckyWheelResults(),rowsB=edited.luckyWheelResults();ensure(rowsA.length===rowsB.length,'wheel_count');
      if(target){const matching=rowsB.filter(row=>row.prize.kind===target);ensure(matching.length>=count,'targeted_wheel_'+target);targetedWheelItems[target]+=matching.length;}
      rowsB.forEach((row,i)=>{ensure(JSON.stringify(row.prize)===JSON.stringify(rowsA[i].prize),'wheel_prize');
        if(['badge','costume','gem'].includes(row.prize.kind)){compare(native,edited,row.prize.kind,row.item.uid);wheelKinds[row.prize.kind]++;}
      });
      ensure(!control.inspect().blocked&&!editedWheel.inspect().blocked,'coexist');
      edited.finishLuckyWheel(b.round);native.finishLuckyWheel(a.round);
      ensure(unchanged(edited,previous),'wheel_existing');ensure(JSON.stringify(native.state.materials)===JSON.stringify(edited.state.materials),'wheel_cost');
      const saved=edited.serialize(),reload=new Engine(saved,()=>random);
      for(const row of rowsB)if(['badge','costume','gem'].includes(row.prize.kind))ensure(JSON.stringify(item(reload,row.prize.kind,row.item.uid))===JSON.stringify(item(edited,row.prize.kind,row.item.uid)),'wheel_reload');
      if(order==='max-first'){editedWheel.reset();ensure(control.inspect().installed&&control.inspect().hooksOwned,'prob_reset_independent');control.reset();}
      else{control.reset();ensure(editedWheel.inspect().installed&&editedWheel.inspect().rngOriginal,'max_reset_independent');
        // The probability session created before max remains usable after reset.
        editedWheel.configure(profile);editedWheel.reset();}
      ensure(edited.serialize()===saved,'reward_preserved');nativeWheel.reset();cases++;
    }
    if(area==='gem-wheel')ensure(Object.keys(definitions.gemSamples).every(key=>gemBaseTypes[key]>0),'wheel_base_type_coverage');
    else if(area!=='wheel')ensure(counts.badge>0&&counts.costume>0&&counts.gem>0,'coverage');
    return {passedChecks:checks,cases,generatedItems:counts,wheelItems:wheelKinds,targetedWheelItems,gemBaseTypes,copyOnly:true};
  };
}
module.exports={createGeneratedItemProbe};
