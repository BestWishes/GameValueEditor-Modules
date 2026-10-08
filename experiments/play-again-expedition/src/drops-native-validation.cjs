'use strict';
// Pure native Engine copies only. Serialized into the read-only production probe.
function validateNativeDrops(Engine, serialized, sessionFactory, model, archivePath, layout) {
  let checks = 0;
  const ensure = (condition, label) => { if (!condition) throw Error('NATIVE_DROPS_' + label + '_FAILED'); checks++; };
  const make = (rng = () => 0.5) => {
    const copy = new Engine(serialized, rng);
    for (const rule of Object.values(copy.state.recycle ?? {})) if (rule && typeof rule === 'object') {
      rule.enabled = false;
      for (const option of Object.values(rule)) if (option && typeof option === 'object' && !Array.isArray(option)) option.enabled = false;
    }
    copy.state.cleared = 400; copy.state.selected = 5; copy.state.guardianSelected = 1;
    copy.state.abyss.tickets = 10;
    copy.state.ores = []; // Avoid the player's ore capacity blocking a test reward.
    const worn = new Set(copy.state.equipped.flatMap(Object.values));
    copy.state.inventory = copy.state.inventory.filter(item => worn.has(item.uid));
    return copy;
  };
  const beforeIds = e => new Set(e.state.inventory.map(item => item.uid));
  const newItems = (e, ids) => e.state.inventory.filter(item => !ids.has(item.uid));
  const atRoll = key => {
    const frames = new Error().stack.split('\n').map(line => line.trim().replaceAll('\\', '/').toLowerCase());
    return typeof layout === 'object' ? layout.sites?.[key]?.some(frame => frames.includes(frame)) === true
      : frames.some(frame => frame.includes(key === 'upgradeRoll' ? 'at engine.drop (' : 'at engine.victory ('));
  };
  const controlled = (profile, action, rng = () => 0.5) => {
    const copy = make(rng), manager = sessionFactory(copy, model, { archivePath, ...(typeof layout === 'object' ? layout : { layout }), assertCurrent: () => true });
    const before = copy.serialize(), originalRng = copy.rng;
    manager.configure({ drops: profile }, 'drops'); ensure(copy.serialize() === before, 'CONFIGURE_SAVE');
    const result = action(copy), report = manager.inspect();
    ensure(report.blocked === null && copy.rng === originalRng, 'OWNERSHIP');
    const after = copy.serialize();
    if (copy.phase === 'idle' && !copy.state.luckyWheel?.pending) ensure(manager.reset('drops').restored && copy.serialize() === after, 'RESTORE');
    // Active isolated copies are discarded, never relax the original idle guard.
    return { result, report };
  };
  const cowStart = (copy, wave) => {
    copy.state.cleared = 400; copy.state.materials.cowTickets = 10;
    const worn = new Set(copy.state.equipped.flatMap(Object.values));
    copy.state.inventory = copy.state.inventory.filter(item => worn.has(item.uid));
    ensure(copy.startCowDungeon(), 'COW_START'); copy.wave = wave; copy.spawn();
  };
  for (const percent of [0, 25, 80, 100]) for (const value of [0.05, 0.249, 0.251, 0.799, 0.801, 0.95]) {
    const tested = controlled({ ordinaryEquipmentPercent: percent, equipmentBonusPercent: 100 }, copy => {
      copy.mode = 'journey'; copy.state.cleared = 400; copy.state.selected = 5; ensure(copy.start() && copy.enemy, 'JOURNEY_START');
      const ids = beforeIds(copy); copy.enemy.hp = 0; copy.victory(); return newItems(copy, ids).length;
    }, function () {
      return atRoll('ordinaryRoll') ? value : 0.5;
    });
    ensure(tested.result === (value < percent / 100 ? 1 : 0) && tested.report.hits.ordinaryEquipment === 1, 'ORDINARY_CONSUMER');
  }
  for (const percent of [-100, 100]) {
    const tested = controlled({ upgradeBonusPercent: percent }, copy => {
      copy.mode = 'journey'; copy.state.selected = 5; copy.start(); const ids = beforeIds(copy); copy.drop(copy.enemy, 100);
      return newItems(copy, ids)[0]?.quality;
    }); ensure(tested.result === (percent === 100 ? 4 : 3) && tested.report.hits.equipmentUpgrade === 1, 'UPGRADE_CONSUMER');
  }
  for (const value of [0.249, 0.251]) {
    const baseline = sessionFactory(make(), model, { archivePath, ...(typeof layout === 'object' ? layout : { layout }), assertCurrent: () => true }).inspect().nativeUpgradeChance;
    const tested = controlled({ upgradeBonusPercent: 25 - baseline * 100 }, copy => {
      copy.mode = 'journey'; copy.state.selected = 5; copy.start(); const ids = beforeIds(copy); copy.drop(copy.enemy, 100);
      return newItems(copy, ids)[0]?.quality;
    }, function () {
      return atRoll('upgradeRoll') ? value : 0.5;
    }); ensure(tested.result === (value < 0.25 ? 4 : 3), 'UPGRADE_INTERMEDIATE');
  }
  for (const mode of ['journey', 'abyss', 'cow']) {
    const action = copy => {
      if (mode === 'cow') cowStart(copy, 0); else {
        if (mode === 'abyss') { copy.state.cleared = 10000; copy.state.selected = 100; }
        copy.mode = mode; copy.start();
      }
      copy.enemy.hp = 0; const before = copy.state.gold; copy.victory(); return copy.state.gold - before;
    };
    const base = controlled({ goldBonusPercent: 0 }, action), extra = controlled({ goldBonusPercent: 100 }, action);
    const native = base.report.nativeGoldBonus;
    ensure(base.result > 0 && Math.abs(extra.result / base.result - (2 + native) / (1 + native)) < 0.01 && extra.report.hits.goldBonus > 0, 'GOLD_CONSUMER');
  }
  for (const percent of [0, 100]) {
    const tested = controlled({ mine: { stonePercent: percent } }, copy => {
      copy.state.cleared = 400; copy.startMine(150); const before = copy.state.materials.stones;
      copy.dropMineReward(2); return copy.state.materials.stones - before;
    }); ensure((percent === 100 ? tested.result > 0 : tested.result === 0) && tested.report.hits.mineDistribution === 1, 'MINE_DISTRIBUTION');
  }
  for (const value of [0.2, 0.8]) {
    const tested = controlled({ mine: { stonePercent: 25 } }, copy => {
      copy.state.cleared = 400; copy.startMine(150); const before = copy.state.materials.stones; copy.dropMineReward(2);
      return copy.state.materials.stones - before;
    }, () => value); ensure(value < 0.25 ? tested.result > 0 : tested.result === 0, 'MINE_INTERMEDIATE');
  }
  const qualities = ['blue', 'purple', 'immortal', 'mythic'];
  for (let index = 0; index < qualities.length; index++) {
    const qualityPercent = Object.fromEntries(qualities.map((key, i) => [key, i === index ? 100 : 0]));
    const tested = controlled({ mine: { stonePercent: 0, qualityPercent } }, copy => {
      copy.state.cleared = 400; copy.startMine(150); const before = copy.state.ores.length; copy.dropMineReward(2); return copy.state.ores.slice(before);
    }); ensure(tested.result.length === 1 && tested.result[0].quality === index + 3 && tested.report.hits.mineQuality === 1, 'MINE_QUALITY');
  }
  for (const [value, quality] of [[0.125, 6], [0.375, 5], [0.625, 4], [0.875, 3]]) {
    const tested = controlled({ mine: { stonePercent: 0, qualityPercent: Object.fromEntries(qualities.map(key => [key, 25])) } }, copy => {
      copy.state.cleared = 400; copy.startMine(150); const before = copy.state.ores.length; copy.dropMineReward(2); return copy.state.ores.slice(before);
    }, () => value); ensure(tested.result.length === 1 && tested.result[0].quality === quality, 'MINE_QUALITY_INTERMEDIATE');
  }
  for (const percent of [0, 100]) {
    const tested = controlled({ mine: { rewardPercent: percent, stonePercent: 100 } }, copy => {
      copy.state.cleared = 400; copy.startMine(150); const before = copy.state.materials.stones;
      for (let i = 0; i < 80; i++) copy.tick(0.1); return copy.state.materials.stones - before;
    }); ensure((percent === 100 ? tested.result > 0 : tested.result === 0) && tested.report.hits.mineReward > 0, 'MINE_NATIVE_TICK');
  }
  for (const wave of [0, 2]) for (const percent of [0, 25, 100]) {
    const tested = controlled({ cow: { ordinaryEquipmentPercent: percent, eliteEquipmentPercent: percent, eliteImmortalPercent: percent } }, copy => {
      cowStart(copy, wave); const ids = beforeIds(copy); copy.enemy.hp = 0; copy.victory(); return newItems(copy, ids);
    }); ensure(tested.result.length === (percent === 100 ? wave === 2 ? 2 : 1 : 0), 'COW_GATES');
  }
  for (const target of [null, ...model.cowPools.mythic, 'all']) {
    const tested = controlled({ cow: { mythicPercent: Object.fromEntries(model.cowPools.mythic.map(key => [key, target === 'all' || target === key ? 100 : 0])) } }, copy => {
      cowStart(copy, 9); const ids = beforeIds(copy); copy.enemy.hp = 0; copy.victory(); return newItems(copy, ids);
    }); ensure(tested.result.length === (target === 'all' ? 3 : target ? 1 : 0) && (!target || target === 'all' || tested.result[0].key === target), 'COW_MYTHICS');
    ensure(model.cowPools.mythic.every(key => tested.report.hits['cowMythic.' + key] === 1), 'COW_MYTHIC_SITES');
  }
  for (const kind of ['gear', 'immortal']) for (const key of model.cowPools[kind]) {
    const tested = controlled({ cow: { [kind + 'Weights']: Object.fromEntries(model.cowPools[kind].map(item => [item, item === key ? 100 : 0])) } }, copy => {
      cowStart(copy, kind === 'gear' ? 0 : 2); const ids = beforeIds(copy); copy.enemy.hp = 0; copy.victory(); return newItems(copy, ids);
    }); ensure(tested.result.some(item => item.key === key) && tested.report.hits['cowPool.' + kind] > 0, 'COW_POOL');
  }
  for (const value of [0.2, 0.8]) {
    const tested = controlled({ cow: { ordinaryEquipmentPercent: 25 } }, copy => {
      cowStart(copy, 0); const ids = beforeIds(copy); copy.enemy.hp = 0; copy.victory(); return newItems(copy, ids).length;
    }, () => value); ensure(tested.result === (value < 0.25 ? 1 : 0), 'COW_INTERMEDIATE');
  }
  // Restoration remains namespace-scoped in the actual merged Engine.
  for (const first of ['wheel', 'drops']) {
    const copy = make(), manager = sessionFactory(copy, model, { archivePath, ...(typeof layout === 'object' ? layout : { layout }), assertCurrent: () => true });
    const before = copy.serialize();
    const wheel = { wheelBonus: { doublePercent: 0, marqueePercent: 0 } }, drops = { drops: { goldBonusPercent: 100 } };
    manager.configure(first === 'wheel' ? wheel : drops, first); manager.configure(first === 'wheel' ? drops : wheel, first === 'wheel' ? 'drops' : 'wheel');
    ensure(manager.inspect().namespaces.wheel.enabled && manager.inspect().namespaces.drops.enabled && copy.serialize() === before, 'NAMESPACE_CONFIGURE');
    ensure(manager.reset(first).restored && !manager.inspect().namespaces[first].enabled && manager.inspect().namespaces[first === 'wheel' ? 'drops' : 'wheel'].enabled, 'NAMESPACE_RESTORE');
    ensure(manager.reset(first === 'wheel' ? 'drops' : 'wheel').restored && !manager.inspect().installed && copy.serialize() === before, 'NAMESPACE_CLEAN');
  }
  return { passedChecks: checks, layout: typeof layout === 'object' ? 'current-runtime' : layout,
    originalGameActionCalled: false, originalSaveCalled: false, copyOnly: true };
}
module.exports = { validateNativeDrops };
