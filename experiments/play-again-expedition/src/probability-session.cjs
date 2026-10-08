'use strict';

// Fixed per-engine hooks. Never change Math.random or invoke a game action/save.
function createProbabilitySession(engine, model, context) {
  const fail = code => { throw Error(code); };
  const names = ['spinLuckyWheel', 'victory', 'dropBonus', 'immortalDropChance'];
  if (!engine || typeof engine.rng !== 'function' || names.some(name => typeof engine[name] !== 'function') ||
    typeof context?.assertCurrent !== 'function' || typeof context.archivePath !== 'string') fail('PROBABILITY_ENGINE_CONTRACT_CHANGED');
  if (typeof engine.dropFlameImmortals === 'function') names.push('dropFlameImmortals');
  // Runtime consumers are discovered on the current Engine, not old bundle columns.
  const advancedLayout = true;
  if (advancedLayout) for (const name of ['goldBonus', 'badgePartyBonus', 'costumePartyBonus', 'partyBondBonus', 'drop', 'afterStarHit', 'dropMineReward', 'dropOre', 'cowDrop86'])
    if (typeof engine[name] === 'function') names.push(name);
  const originals = new Map(names.map(name => [name, { value: engine[name], own: Object.getOwnPropertyDescriptor(engine, name) }]));
  const originalRng = engine.rng, state = engine.state;
  let settings = {}, installed = false, activeDepth = 0, flameBoss = null, blocked = null, hits = {}, calls = {}, owned = new Map();
  let readingNative = false, mineHero = null, cowPool = null, cowMythicRoll = 0;
  const normalize = value => value.replaceAll('\\', '/').toLowerCase();
  const archive = normalize(context.archivePath);
  const current = () => {
    try { return engine.state === state && context.assertCurrent() === true; }
    catch { blocked = 'PROBABILITY_OWNER_UNCONFIRMED'; return false; }
  };
  const hooksOwned = () => [...owned].every(([name, fn]) => Object.getOwnPropertyDescriptor(engine, name)?.value === fn);
  const rngDescriptor = () => Object.getOwnPropertyDescriptor(engine, 'rng');
  const block = error => { blocked = /^[A-Z_]{1,80}$/.test(error?.message) ? error.message : 'PROBABILITY_MAPPING_UNCONFIRMED'; };
  const hit = key => { hits[key] = (hits[key] ?? 0) + 1; };
  const consumerNames = { wheelBonus: 'rollwheelbonus', wheelPrize: 'choosewheelindex', guardianTicket: 'engine.victory',
    flame: 'engine.dropflameimmortals', ordinaryEquipment: 'engine.victory', equipmentUpgrade: 'engine.drop',
    mineReward: 'engine.afterstarhit', mineDistribution: 'engine.dropminereward', mineQuality: 'engine.dropore', cowPool: 'engine.cowdrop86' };
  const site = (frame, key) => context.sites
    ? context.sites[key]?.some(location => frame.trim() === normalize(location)) === true
    : frame.includes('at ' + consumerNames[key] + ' (') && frame.includes(archive + '/');
  const caller = () => normalize(new Error().stack.split('\n')[3] ?? '');
  const clamp = value => Math.max(0, Math.min(1, value));
  function originalRate(name, args = []) {
    const original = originals.get(name)?.value;
    if (!original) fail('DROP_GETTER_NOT_SUPPORTED');
    const value = Reflect.apply(original, engine, args);
    if (typeof value !== 'number' || !Number.isFinite(value)) fail('DROP_BONUS_CONTRACT_CHANGED');
    return value;
  }
  function mineStoneChance() {
    if (!Number.isSafeInteger(mineHero) || mineHero < 0 || !Array.isArray(state.party) || mineHero >= state.party.length) fail('MINING_ATTACKER_UNCONFIRMED');
    const id = state.party[mineHero], star = state.characters?.[id]?.star;
    return id === 105 && star >= 5 ? 50 : 60;
  }
  function poolKind(pool, quality) {
    if (!Array.isArray(pool)) return null;
    for (const [kind, expectedQuality] of [['gear', 4], ['immortal', 5]]) {
      const expected = model.cowPools[kind];
      if (quality === expectedQuality && pool.length === expected.length && pool.every((key, index) => key === expected[index])) return kind;
    }
    return quality === 6 && pool.length === 1 && model.cowPools.mythic.includes(pool[0]) ? 'mythic' : null;
  }
  function rngProxy() {
    const value = Reflect.apply(originalRng, this, arguments);
    if (blocked || !current() || !hooksOwned()) return value;
    try {
      const error = new Error(); Error.captureStackTrace(error, rngProxy);
      const frame = normalize(error.stack.split('\n')[1] ?? '');
      if (engine.mode === 'mine') {
        if (site(frame, 'mineReward') && settings.drops?.mine?.rewardPercent !== undefined) {
          hit('mineReward'); return model.mapBernoulli(value, 1, settings.drops.mine.rewardPercent);
        }
        if (site(frame, 'mineDistribution') && settings.drops?.mine?.stonePercent !== undefined) {
          hit('mineDistribution'); return model.mapBernoulli(value, mineStoneChance(), settings.drops.mine.stonePercent);
        }
        if (site(frame, 'mineQuality') && settings.drops?.mine?.qualityPercent) {
          const quality = settings.drops.mine.qualityPercent; hit('mineQuality');
          return model.mapCategorical(value, [1, 4, 25, 70], [quality.mythic, quality.immortal, quality.purple, quality.blue]);
        }
      }
      if (engine.mode === 'cow') {
        if (site(frame, 'cowMythic') && settings.drops?.cow?.mythicPercent) {
          if (engine.enemy?.cowKing !== true) fail('COW_REWARD_CONTEXT_UNCONFIRMED');
          const key = model.cowPools.mythic[cowMythicRoll++];
          if (!key) fail('COW_MYTHIC_ROLL_CONTRACT_CHANGED');
          const percent = settings.drops?.cow?.mythicPercent?.[key];
          if (percent !== undefined) { hit('cowMythic.' + key); return model.mapBernoulli(value, 0.5, percent); }
        }
        if (site(frame, 'cowPool') && cowPool && cowPool.kind !== 'mythic') {
          const weights = settings.drops?.cow?.[cowPool.kind === 'gear' ? 'gearWeights' : 'immortalWeights'];
          if (weights) {
            hit('cowPool.' + cowPool.kind);
            return model.mapCategorical(value, cowPool.keys.map(() => 1), cowPool.keys.map(key => weights[key] ?? 1));
          }
        }
      }
      if (site(frame, 'wheelBonus')) {
        if (!settings.wheelBonus) return value;
        hits.wheelBonus = (hits.wheelBonus ?? 0) + 1;
        return model.mapCategorical(value, [0.02, 0.01, 0.97], [settings.wheelBonus.doublePercent / 100,
          settings.wheelBonus.marqueePercent / 100, 1 - (settings.wheelBonus.doublePercent + settings.wheelBonus.marqueePercent) / 100]);
      }
      if (site(frame, 'wheelPrize')) {
        if (!settings.wheel) return value;
        const weights = model.wheelWeights(state.luckyWheel.board, settings.wheel);
        hits.wheelPrize = (hits.wheelPrize ?? 0) + 1;
        return model.mapCategorical(value, weights.native, weights.desired);
      }
      if (engine.mode === 'guardian' && site(frame, 'guardianTicket') && settings.drops?.guardianTicketPercent !== undefined) {
        hits.guardianTicket = (hits.guardianTicket ?? 0) + 1;
        return model.mapBernoulli(value, 50, settings.drops.guardianTicketPercent);
      }
      if (engine.mode === 'flame' && site(frame, 'flame') && flameBoss !== null) {
        const mythic = flameBoss === 25404, key = mythic ? 'flameMythicPercent' : 'flameImmortalPercent';
        if (settings.drops?.[key] === undefined) return value;
        hits[key] = (hits[key] ?? 0) + 1;
        return model.mapBernoulli(value, mythic ? 0.5 : 5, settings.drops[key]);
      }
      return value;
    } catch (error) { block(error); return value; }
  }
  function around(name) {
    const original = originals.get(name).value;
    return function (...args) {
      if (name === 'spinLuckyWheel' || name === 'victory' || name === 'dropFlameImmortals') calls[name] = (calls[name] ?? 0) + 1;
      if (this !== engine || readingNative || blocked || !current() || !hooksOwned() ||
        !['journey', 'guardian', 'abyss', 'flame', ...(advancedLayout ? ['mine', 'cow'] : [])].includes(engine.mode) || !Object.keys(settings).length) return Reflect.apply(original, this, args);
      if (name === 'dropBonus') {
        const value = Reflect.apply(original, this, args);
        if (typeof value !== 'number' || !Number.isFinite(value)) { blocked = 'DROP_BONUS_CONTRACT_CHANGED'; return value; }
        if (engine.mode === 'journey' && settings.drops?.ordinaryEquipmentPercent !== undefined && site(caller(), 'ordinaryEquipment')) {
          try {
            // Native predicate also includes the party bond bonus. Cancel it at
            // this one consumer, not in the original aggregate or other rewards.
            const bond = originalRate('partyBondBonus', ['itemDrop']);
            hit('ordinaryEquipment'); return settings.drops.ordinaryEquipmentPercent / 100 - 0.38 - bond;
          } catch (error) { block(error); return value; }
        }
        return engine.mode === 'flame' || settings.drops?.equipmentBonusPercent === undefined ? value : value + settings.drops.equipmentBonusPercent / 100;
      }
      if (name === 'goldBonus') {
        const value = Reflect.apply(original, this, args);
        if (settings.drops?.goldBonusPercent === undefined) return value;
        if (typeof value !== 'number' || !Number.isFinite(value)) { blocked = 'GOLD_BONUS_CONTRACT_CHANGED'; return value; }
        const effective = Math.max(-1, value + settings.drops.goldBonusPercent / 100);
        if (!Number.isFinite(effective)) { blocked = 'GOLD_BONUS_CONTRACT_CHANGED'; return value; }
        hit('goldBonus'); return effective;
      }
      if (name === 'badgePartyBonus' || name === 'costumePartyBonus') {
        const value = Reflect.apply(original, this, args);
        if (name !== 'badgePartyBonus' || args[0] !== 'upgrade' || settings.drops?.upgradeBonusPercent === undefined || !site(caller(), 'equipmentUpgrade')) return value;
        try {
          const costume = originalRate('costumePartyBonus', ['upgrade']);
          if (typeof value !== 'number' || !Number.isFinite(value)) fail('UPGRADE_BONUS_CONTRACT_CHANGED');
          hit('equipmentUpgrade'); return clamp(value + costume + settings.drops.upgradeBonusPercent / 100) - costume;
        } catch (error) { block(error); return value; }
      }
      if (name === 'partyBondBonus') return Reflect.apply(original, this, args);
      if (name === 'immortalDropChance') {
        if (engine.mode === 'abyss' && settings.drops?.abyssImmortalPercent !== undefined) {
          hits.abyssImmortal = (hits.abyssImmortal ?? 0) + 1;
          return settings.drops.abyssImmortalPercent / 100;
        }
        return Reflect.apply(original, this, args);
      }
      if (name === 'dropFlameImmortals') {
        if (engine.mode !== 'flame' || !Number.isSafeInteger(args[0]) || args[0] < 25400 || args[0] > 25404) return Reflect.apply(original, this, args);
        const previous = flameBoss; flameBoss = args[0];
        try { return runWithRng(original, this, args); } finally { flameBoss = previous; }
      }
      if (name === 'dropMineReward') {
        if (engine.mode !== 'mine') return Reflect.apply(original, this, args);
        const previous = mineHero; mineHero = args[0];
        try { return runWithRng(original, this, args); } finally { mineHero = previous; }
      }
      if (name === 'cowDrop86') {
        if (engine.mode !== 'cow' || !settings.drops?.cow) return Reflect.apply(original, this, args);
        const kind = poolKind(args[0], args[1]);
        if (!kind) { if (settings.drops?.cow) blocked = 'COW_POOL_CONTRACT_CHANGED'; return Reflect.apply(original, this, args); }
        if (settings.drops?.cow && (engine.enemy?.cow !== true || typeof engine.enemy.cowElite !== 'boolean' || typeof engine.enemy.cowKing !== 'boolean' ||
          kind === 'immortal' && !engine.enemy.cowElite || kind === 'mythic' && !engine.enemy.cowKing || kind === 'gear' && engine.enemy.cowKing)) {
          blocked = 'COW_REWARD_CONTEXT_UNCONFIRMED'; return Reflect.apply(original, this, args);
        }
        const previous = cowPool; cowPool = { kind, keys: args[0] };
        try {
          if (kind !== 'mythic') {
            const key = kind === 'immortal' ? 'eliteImmortalPercent' : engine.enemy.cowElite ? 'eliteEquipmentPercent' : 'ordinaryEquipmentPercent';
            const percent = settings.drops?.cow?.[key];
            if (percent !== undefined) {
              hit('cowGate.' + key);
              if (percent === 0) return undefined;
              // Native rewards are guaranteed. Only a non-endpoint gate adds a draw.
              if (percent !== 100) {
                const value = Reflect.apply(originalRng, engine, []);
                if (typeof value !== 'number' || !Number.isFinite(value) || value < 0 || value >= 1) {
                  blocked = 'RNG_VALUE_INVALID'; return Reflect.apply(original, this, args);
                }
                if (value >= percent / 100) return undefined;
              }
            }
          }
          return runWithRng(original, this, args);
        } finally { cowPool = previous; }
      }
      if (name === 'victory') {
        const previous = cowMythicRoll; cowMythicRoll = 0;
        try { return runWithRng(original, this, args); } finally { cowMythicRoll = previous; }
      }
      return runWithRng(original, this, args);
    };
  }
  function runWithRng(original, receiver, args) {
      if (activeDepth > 0) return Reflect.apply(original, receiver, args);
      const descriptor = rngDescriptor();
      if (descriptor?.value !== originalRng || descriptor.writable !== true) { blocked = 'RNG_OWNER_CHANGED'; return Reflect.apply(original, receiver, args); }
      activeDepth++;
      try { engine.rng = rngProxy; return Reflect.apply(original, receiver, args); }
      finally {
        activeDepth--;
        if (rngDescriptor()?.value === rngProxy) {
          try { if (!Reflect.set(engine, 'rng', originalRng)) blocked = 'RNG_RESTORE_CONFLICT'; }
          catch { blocked = 'RNG_RESTORE_CONFLICT'; }
        }
        else blocked = 'RNG_OWNER_CHANGED';
      }
  }
  function inspect() {
    const nativeRate = name => {
      const previous = readingNative; readingNative = true;
      try {
        return originalRate(name);
      } catch { return null; } finally { readingNative = previous; }
    };
    let nativeUpgradeChance = null;
    if (advancedLayout) {
      const previous = readingNative; readingNative = true;
      try { nativeUpgradeChance = clamp(originalRate('badgePartyBonus', ['upgrade']) + originalRate('costumePartyBonus', ['upgrade'])); }
      catch { } finally { readingNative = previous; }
    }
    let wheel = [], wheelStatus = 'not-initialized';
    if (Array.isArray(state.luckyWheel?.board)) {
      try { wheel = model.inspectWheel(state.luckyWheel.board, settings.wheel); wheelStatus = 'available'; }
      catch (error) { wheelStatus = error.message; }
    }
    const ownerCurrent = current(), ownership = hooksOwned();
    const applied = installed && ownerCurrent && ownership && !blocked;
    const nativeDropBonus = nativeRate('dropBonus'), nativeGoldBonus = nativeRate('goldBonus');
    return { installed, settings: JSON.parse(JSON.stringify(settings)), blocked, ownerCurrent, hooksOwned: ownership,
      namespaces: {
        wheel: { enabled: !!(settings.wheel || settings.wheelBonus), settings: JSON.parse(JSON.stringify(Object.fromEntries(Object.entries(settings).filter(([key]) => key !== 'drops')))),
          hits: Object.fromEntries(Object.entries(hits).filter(([key]) => key.startsWith('wheel'))) },
        drops: { enabled: !!settings.drops, settings: settings.drops ? { drops: JSON.parse(JSON.stringify(settings.drops)) } : {},
          hits: Object.fromEntries(Object.entries(hits).filter(([key]) => !key.startsWith('wheel'))) },
      },
      layout: 'current-runtime', advancedDropsSupported: advancedLayout,
      consumerCapabilities: Object.keys(context.sites ?? consumerNames),
      hitsScope: 'session-cumulative',
      rngOriginal: rngDescriptor()?.value === originalRng, wheelStatus, wheel, hits: { ...hits }, calls: { ...calls }, sessionOnly: true,
      rngWritable: rngDescriptor()?.writable === true,
      hookCapabilities: names.map(name => {
        const descriptor = Object.getOwnPropertyDescriptor(engine, name);
        return { name, own: !!descriptor, canInstall: descriptor ? descriptor.configurable === true && descriptor.writable === true : Object.isExtensible(engine) };
      }),
      nativeDropBonus, nativeImmortalChance: nativeRate('immortalDropChance'), nativeGoldBonus, nativeUpgradeChance,
      nativeOrdinaryChance: advancedLayout && nativeDropBonus !== null ? clamp(0.38 + nativeDropBonus + nativeRateWithArgument('partyBondBonus', 'itemDrop')) : null,
      effectiveDropBonus: nativeDropBonus === null ? null : nativeDropBonus + (applied && engine.mode !== 'flame' ? settings.drops?.equipmentBonusPercent ?? 0 : 0) / 100,
      effectiveGoldBonus: nativeGoldBonus === null ? null : Math.max(-1, nativeGoldBonus + (applied ? settings.drops?.goldBonusPercent ?? 0 : 0) / 100),
      effectiveUpgradeChance: nativeUpgradeChance === null ? null : clamp(nativeUpgradeChance + (applied ? settings.drops?.upgradeBonusPercent ?? 0 : 0) / 100) };
  }
  function nativeRateWithArgument(name, argument) {
    const previous = readingNative; readingNative = true;
    try { return originalRate(name, [argument]); } catch { return NaN; } finally { readingNative = previous; }
  }
  function assertIdle() {
    if (!current() || !hooksOwned() || rngDescriptor()?.value !== originalRng || rngDescriptor()?.writable !== true) fail('PROBABILITY_SESSION_CHANGED');
    if (activeDepth || engine.phase !== 'idle' || state.luckyWheel?.pending) fail('PROBABILITY_OPERATION_NOT_IDLE');
  }
  function configure(input, namespace) {
    assertIdle(); if (blocked) fail('PROBABILITY_SESSION_BLOCKED');
    if (namespace !== undefined && !['wheel', 'drops'].includes(namespace)) fail('UNKNOWN_PROBABILITY_NAMESPACE');
    const profile = model.validate(input);
    if (namespace === 'wheel' && profile.drops !== undefined || namespace === 'drops' && Object.keys(profile).some(key => key !== 'drops')) fail('PROBABILITY_NAMESPACE_MISMATCH');
    const next = namespace === undefined ? profile : { ...Object.fromEntries(Object.entries(settings).filter(([key]) => namespace === 'wheel' ? key === 'drops' : key !== 'drops')), ...profile };
    const requiredSites = [];
    if (next.wheel) requiredSites.push('wheelPrize');
    if (next.wheelBonus) requiredSites.push('wheelBonus');
    if (next.drops?.guardianTicketPercent !== undefined) requiredSites.push('guardianTicket');
    if (next.drops?.flameImmortalPercent !== undefined || next.drops?.flameMythicPercent !== undefined) requiredSites.push('flame');
    if (next.drops?.ordinaryEquipmentPercent !== undefined) requiredSites.push('ordinaryEquipment');
    if (next.drops?.upgradeBonusPercent !== undefined) requiredSites.push('equipmentUpgrade');
    if (next.drops?.mine?.rewardPercent !== undefined) requiredSites.push('mineReward');
    if (next.drops?.mine?.stonePercent !== undefined) requiredSites.push('mineDistribution');
    if (next.drops?.mine?.qualityPercent !== undefined) requiredSites.push('mineQuality');
    if (next.drops?.cow?.gearWeights || next.drops?.cow?.immortalWeights) requiredSites.push('cowPool');
    if (next.drops?.cow?.mythicPercent) requiredSites.push('cowMythic');
    if (context.sites && requiredSites.some(key => !context.sites[key]?.length)) fail('PROBABILITY_CONSUMER_NOT_SUPPORTED');
    const requires = [];
    if (next.drops?.goldBonusPercent !== undefined) requires.push('goldBonus');
    if (next.drops?.ordinaryEquipmentPercent !== undefined) requires.push('partyBondBonus');
    if (next.drops?.upgradeBonusPercent !== undefined) requires.push('drop', 'badgePartyBonus', 'costumePartyBonus');
    if (next.drops?.mine) requires.push('afterStarHit', 'dropMineReward', 'dropOre');
    if (next.drops?.cow) requires.push('cowDrop86');
    if (requires.some(name => !names.includes(name))) fail('DROP_ENGINE_CONTRACT_CHANGED');
    if ((next.drops?.flameImmortalPercent !== undefined || next.drops?.flameMythicPercent !== undefined) && !names.includes('dropFlameImmortals')) fail('FLAME_PROBABILITY_NOT_SUPPORTED');
    if (next.wheel) model.wheelWeights(state.luckyWheel?.board, next.wheel);
    if (!installed) {
      for (const name of names) {
        if (engine[name] !== originals.get(name).value) fail('PROBABILITY_METHOD_CHANGED');
        const descriptor = Object.getOwnPropertyDescriptor(engine, name);
        if (descriptor && (!descriptor.configurable || descriptor.writable !== true)) fail('PROBABILITY_HOOK_NOT_WRITABLE');
        if (!descriptor && !Object.isExtensible(engine)) fail('PROBABILITY_ENGINE_NOT_EXTENSIBLE');
      }
      try {
        for (const name of names) {
          const fn = around(name); Object.defineProperty(engine, name, { value: fn, writable: true, configurable: true, enumerable: originals.get(name).own?.enumerable ?? false }); owned.set(name, fn);
        }
        installed = true;
      } catch (error) {
        for (const [name, fn] of owned) if (Object.getOwnPropertyDescriptor(engine, name)?.value === fn) {
          const previous = originals.get(name).own;
          if (previous) Object.defineProperty(engine, name, previous); else delete engine[name];
        }
        owned.clear(); throw error;
      }
    }
    settings = next; return inspect();
  }
  function reset(namespace) {
    if (namespace !== undefined && !['wheel', 'drops'].includes(namespace)) fail('UNKNOWN_PROBABILITY_NAMESPACE');
    if (activeDepth || engine.phase !== 'idle' || state.luckyWheel?.pending) fail('PROBABILITY_OPERATION_NOT_IDLE');
    if (namespace !== undefined) {
      const next = Object.fromEntries(Object.entries(settings).filter(([key]) => namespace === 'wheel' ? key === 'drops' : key !== 'drops'));
      if (Object.keys(next).length) {
        assertIdle(); if (blocked) fail('PROBABILITY_SESSION_BLOCKED');
        settings = next;
        return { restored: true, conflicts: [], namespace, rngOriginal: rngDescriptor()?.value === originalRng, sessionOnly: true };
      }
    }
    const conflicts = [];
    for (const [name, fn] of owned) {
      if (Object.getOwnPropertyDescriptor(engine, name)?.value !== fn) { conflicts.push(name); continue; }
      const previous = originals.get(name).own;
      try { if (previous) Object.defineProperty(engine, name, previous); else delete engine[name]; }
      catch { conflicts.push(name); }
    }
    owned.clear(); installed = false; settings = {}; blocked = null;
    return { restored: conflicts.length === 0, conflicts, namespace: namespace ?? null, rngOriginal: rngDescriptor()?.value === originalRng, sessionOnly: true };
  }
  return { inspect, configure, reset };
}
module.exports = { createProbabilitySession };
