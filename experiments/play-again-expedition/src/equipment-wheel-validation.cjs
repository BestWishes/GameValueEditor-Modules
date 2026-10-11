'use strict';
// Only disposable native Engine objects. Never call an original game action/save.
function createEquipmentWheelProbe(modelFactory, probabilityFactory, discover, bridgeFactory) {
  return function verify(Engine, snapshot, equipmentFactory, validate, definitions, archivePath) {
    let checks=0, cases=0, gearRewards=0, doubleValuesChecked=0;
    const ensure=(condition,label)=>{if(!condition)throw Error('EQUIPMENT_WHEEL_'+label.replaceAll('-','_').toUpperCase()+'_FAILED');checks++;};
    const model=modelFactory(), source=new Engine(snapshot,()=>.5);
    source.prepareLuckyWheel();
    const groups=[...new Set(source.state.luckyWheel.board.filter(row=>row.kind==='gear').map(model.groupOf))];
    ensure(groups.length>0,'gear-pool');
    const sites=discover(source,model,archivePath).sites;
    const withoutValues=item=>({...item,affixes:item.affixes.map(({value,value2,...other})=>other)});
    for(const group of groups) for(const count of [1,10]) for(const bonus of ['none','double','marquee']) {
      let nativeCalls=0, editedCalls=0;
      const native=new Engine(snapshot,()=>{nativeCalls++;return .5;}), edited=new Engine(snapshot,()=>{editedCalls++;return .5;});
      for(const copy of [native,edited]) {
        // Keep equipped-item references valid for the native reload contract.
        // Currency/animation setup belongs only to the copies.
        copy.state.materials.wheelTickets=100;copy.phase='idle';copy.prepareLuckyWheel();
      }
      const profile={wheel:{groupPercent:Object.fromEntries(['ordinary','immortal','mythic','protection','sacredProtection'].map(key=>[key,key===group?100:0])),
        typeWeights:{gear:1,badge:0,gem:0,costume:0}},wheelBonus:{doublePercent:bonus==='double'?100:0,marqueePercent:bonus==='marquee'?100:0}};
      const nativeWheel=probabilityFactory(native,model,{archivePath,sites,assertCurrent:()=>true},bridgeFactory);
      const editedWheel=probabilityFactory(edited,model,{archivePath,sites,assertCurrent:()=>true},bridgeFactory);
      const gear=equipmentFactory(edited,definitions,{archivePath,assertCurrent:()=>true},validate);
      nativeWheel.configure(profile);editedWheel.configure(profile);
      const before=edited.serialize(),originalInventory=edited.state.inventory.map(item=>[item.uid,JSON.stringify(item)]);
      gear.configure({enabled:true});ensure(edited.serialize()===before,'configure');
      const a=native.spinLuckyWheel(count),b=edited.spinLuckyWheel(count);
      ensure(a.ok===true&&b.ok===true,'draw-success');
      ensure(nativeCalls===editedCalls,'rng-calls');
      ensure(edited.state.materials.wheelTickets===100-count&&native.state.materials.wheelTickets===100-count,'ticket-cost');
      ensure(JSON.stringify(native.state.luckyWheel.pending.spins)===JSON.stringify(edited.state.luckyWheel.pending.spins),'draw-choice');
      const expected=native.luckyWheelResults(),actual=edited.luckyWheelResults();
      ensure(expected.length===actual.length&&actual.length>=count,'reward-count');
      let generated=0;
      actual.forEach((row,index)=>{
        const baseline=expected[index];
        ensure(JSON.stringify(row.prize)===JSON.stringify(baseline.prize),'prize');
        // Accessory values are now intentionally covered by the same rule.
        // The dedicated generated-item probe verifies their raw factory values.
        if(row.prize.kind!=='gear') {ensure(row.prize.kind===baseline.prize.kind,'non-gear-kind');return;}
        const item=row.item;ensure(item&&baseline.item,'generated-item');
        ensure(JSON.stringify(withoutValues(item))===JSON.stringify(withoutValues(baseline.item)),'only-affix-values');
        for(const affix of item.affixes) {
          const range=definitions.ranges[affix.key]?.[item.quality];
          ensure(Array.isArray(range)&&affix.value===range[1],'affix-max');
          if(affix.value2!==undefined) {
            const second=definitions.secondary[affix.key];
            ensure(Array.isArray(second)&&affix.value2===second[1],'secondary-max');doubleValuesChecked++;
          }
        }
        ensure(JSON.stringify(edited.itemByUid(item.uid)?.affixes)===JSON.stringify(item.affixes),'inventory-item');
        gearRewards++;generated++;
      });
      ensure(generated>=count&&!gear.inspect().blocked&&!editedWheel.inspect().blocked,'all-gear');
      edited.finishLuckyWheel(b.round);native.finishLuckyWheel(a.round);
      // Native settlement can sort the bag; identity/content, not array order,
      // is the invariant for equipment that existed before this draw.
      ensure(originalInventory.every(([uid,json])=>JSON.stringify(edited.itemByUid(uid))===json),'old-equipment-preserved');
      const saved=edited.serialize(),reloaded=new Engine(saved,()=>.5);
      for(const row of actual.filter(row=>row.prize.kind==='gear'))
        ensure(JSON.stringify(reloaded.itemByUid(row.item.uid)?.affixes)===JSON.stringify(row.item.affixes),'reload');
      gear.reset();ensure(edited.serialize()===saved&&editedWheel.inspect().installed,'independent-restore');
      // Restoring max-value rules must leave subsequent native rolls unchanged.
      const nextNative=new Engine(saved,()=>.5),nextEdited=new Engine(saved,()=>.5);
      const reset=equipmentFactory(nextEdited,definitions,{archivePath,assertCurrent:()=>true},validate);
      reset.configure({enabled:true});reset.reset();
      const restored=nextEdited.makeItem(definitions.sample.origin,50,5),original=nextNative.makeItem(definitions.sample.origin,50,5);
      ensure(JSON.stringify(restored)===JSON.stringify(original),'restored-native-generation');
      nativeWheel.reset();editedWheel.reset();cases++;
    }
    // The current player's native prize pool may contain no two-value item.
    // Known native fixtures additionally require this coverage in the CLI test.
    return {passedChecks:checks,cases,gearRewards,doubleValuesChecked,copyOnly:true};
  };
}
module.exports={createEquipmentWheelProbe};
