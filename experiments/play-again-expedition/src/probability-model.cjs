'use strict';

// Standalone fixed model, also serialized into the original isolated world.
function createProbabilityModel() {
  const groups = ['ordinary', 'immortal', 'mythic', 'protection', 'sacredProtection'];
  const budgets = { ordinary: 0.925, immortal: 0.05, mythic: 0.01, protection: 0.01, sacredProtection: 0.005 };
  const selectors = ['gear', 'costume', 'badge', 'gem', 'material', 'q3', 'q4', 'q5', 'q6'];
  const cowPools = {
    gear: ['w:14000', 'w:14001', 'w:14002', 'w:14003', 'a:14000', 'a:14001', 'a:14002', 'a:14003'],
    immortal: ['w:903', 'w:895', 'a:1464', 'a:1465', 'w:883', 'w:916', 'a:1460', 'a:1455', 'w:906', 'w:913', 'a:1453', 'a:1467'],
    mythic: ['w:10007', 'a:10008', 'w:10008'],
  };
  const fail = code => { throw Error(code); };
  const record = (value, keys) => {
    if (!value || typeof value !== 'object' || Array.isArray(value)) fail('INVALID_PROBABILITY_RECORD');
    if (![Object.prototype, null].includes(Object.getPrototypeOf(value))) fail('INVALID_PROBABILITY_RECORD');
    for (const key of Object.keys(value)) {
      const descriptor = Object.getOwnPropertyDescriptor(value, key);
      if (!keys.includes(key) || !Object.hasOwn(descriptor, 'value')) fail('UNKNOWN_PROBABILITY_KEY');
      if (descriptor.value === undefined) fail('PROBABILITY_VALUE_MISSING');
    }
  };
  const number = (value, low, high) => {
    if (typeof value !== 'number' || !Number.isFinite(value) || value < low || value > high) fail('PROBABILITY_RANGE_INVALID');
    return value;
  };
  const completePercent = (value, keys) => {
    record(value, keys);
    if (Object.keys(value).length !== keys.length || Math.abs(keys.reduce((sum, key) => sum + number(value[key], 0, 100), 0) - 100) > 1e-8)
      fail('DROP_DISTRIBUTION_TOTAL_INVALID');
  };
  const poolWeights = (value, keys) => {
    record(value, keys);
    if (!Object.keys(value).length || !(keys.reduce((sum, key) => sum + number(Object.hasOwn(value, key) ? value[key] : 1, 0, 100), 0) > 0)) fail('DROP_POOL_EMPTY');
  };
  function validate(input) {
    record(input, ['wheel', 'wheelBonus', 'drops']);
    if (!Object.keys(input).length) fail('EMPTY_PROBABILITY_PROFILE');
    if (input.wheel !== undefined) {
      record(input.wheel, ['groupPercent', 'typeWeights']); record(input.wheel.groupPercent, groups);
      if (Object.keys(input.wheel.groupPercent).length !== groups.length) fail('WHEEL_GROUPS_INCOMPLETE');
      const total = groups.reduce((sum, group) => sum + number(input.wheel.groupPercent[group], 0, 100), 0);
      if (Math.abs(total - 100) > 1e-8) fail('WHEEL_PERCENT_TOTAL_INVALID');
      if (input.wheel.typeWeights !== undefined) {
        record(input.wheel.typeWeights, selectors);
        for (const weight of Object.values(input.wheel.typeWeights)) number(weight, 0, 100);
      }
    }
    if (input.wheelBonus !== undefined) {
      record(input.wheelBonus, ['doublePercent', 'marqueePercent']);
      number(input.wheelBonus.doublePercent, 0, 100); number(input.wheelBonus.marqueePercent, 0, 100);
      if (input.wheelBonus.doublePercent + input.wheelBonus.marqueePercent > 100) fail('WHEEL_BONUS_TOTAL_INVALID');
    }
    if (input.drops !== undefined) {
      const numeric = ['equipmentBonusPercent', 'upgradeBonusPercent', 'goldBonusPercent', 'ordinaryEquipmentPercent',
        'abyssImmortalPercent', 'guardianTicketPercent', 'flameImmortalPercent', 'flameMythicPercent'];
      record(input.drops, [...numeric, 'mine', 'cow']);
      if (!Object.keys(input.drops).length) fail('EMPTY_PROBABILITY_PROFILE');
      for (const key of Object.keys(input.drops).filter(key => numeric.includes(key))) number(input.drops[key],
        key.endsWith('BonusPercent') ? -100 : 0, key === 'goldBonusPercent' ? 1000 : 100);
      if (input.drops.mine !== undefined) {
        const mine = input.drops.mine; record(mine, ['rewardPercent', 'stonePercent', 'qualityPercent']);
        if (!Object.keys(mine).length) fail('EMPTY_PROBABILITY_PROFILE');
        for (const key of ['rewardPercent', 'stonePercent']) if (mine[key] !== undefined) number(mine[key], 0, 100);
        if (mine.qualityPercent !== undefined) completePercent(mine.qualityPercent, ['blue', 'purple', 'immortal', 'mythic']);
      }
      if (input.drops.cow !== undefined) {
        const cow = input.drops.cow;
        record(cow, ['ordinaryEquipmentPercent', 'eliteEquipmentPercent', 'eliteImmortalPercent', 'mythicPercent', 'gearWeights', 'immortalWeights']);
        if (!Object.keys(cow).length) fail('EMPTY_PROBABILITY_PROFILE');
        for (const key of ['ordinaryEquipmentPercent', 'eliteEquipmentPercent', 'eliteImmortalPercent']) if (cow[key] !== undefined) number(cow[key], 0, 100);
        if (cow.mythicPercent !== undefined) {
          record(cow.mythicPercent, cowPools.mythic);
          if (!Object.keys(cow.mythicPercent).length) fail('EMPTY_PROBABILITY_PROFILE');
          for (const value of Object.values(cow.mythicPercent)) number(value, 0, 100);
        }
        for (const [key, pool] of [['gearWeights', cowPools.gear], ['immortalWeights', cowPools.immortal]])
          if (cow[key] !== undefined) poolWeights(cow[key], pool);
      }
    }
    return JSON.parse(JSON.stringify(input));
  }
  function groupOf(prize) {
    if (!prize || !['gear', 'costume', 'badge', 'gem', 'material'].includes(prize.kind) ||
      !Number.isInteger(prize.quality) || prize.quality < 3 || prize.quality > 6) fail('WHEEL_PRIZE_CONTRACT_CHANGED');
    if (prize.kind === 'material') {
      if (prize.key === 'protectionScrolls' && prize.quality === 5) return 'protection';
      if (prize.key === 'sacredProtectionScrolls' && prize.quality === 6) return 'sacredProtection';
      fail('WHEEL_MATERIAL_NOT_VERIFIED');
    }
    if (prize.kind === 'badge' && prize.quality === 6) fail('WHEEL_PRIZE_CONTRACT_CHANGED');
    return prize.quality >= 6 ? 'mythic' : prize.quality === 5 ? 'immortal' : 'ordinary';
  }
  function wheelWeights(board, profile) {
    if (!Array.isArray(board) || board.length !== 16) fail('WHEEL_BOARD_CHANGED');
    const classifications = board.map(groupOf), counts = {};
    for (const group of groups) counts[group] = classifications.filter(value => value === group).length;
    const native = classifications.map(group => budgets[group] / counts[group]);
    if (!profile) return { native, desired: [...native] };
    const factors = board.map(prize => (profile.typeWeights?.[prize.kind] ?? 1) * (profile.typeWeights?.['q' + prize.quality] ?? 1));
    const totals = {};
    for (const group of groups) totals[group] = factors.reduce((sum, factor, index) => sum + (classifications[index] === group ? factor : 0), 0);
    for (const group of groups) if (profile.groupPercent[group] > 0 && !(totals[group] > 0)) fail('WHEEL_GROUP_UNAVAILABLE');
    const desired = classifications.map((group, index) => profile.groupPercent[group] === 0 ? 0 :
      profile.groupPercent[group] / 100 * factors[index] / totals[group]);
    return { native, desired };
  }
  function mapCategorical(value, native, desired) {
    number(value, 0, 1); if (value === 1) fail('RNG_VALUE_INVALID');
    if (!Array.isArray(native) || !Array.isArray(desired) || !native.length || native.length !== desired.length) fail('INVALID_DISTRIBUTION');
    for (let index = 0; index < native.length; index++) {
      number(native[index], 0, 10000); number(desired[index], 0, 10000);
      if (native[index] === 0 && desired[index] > 0) fail('NATIVE_OUTCOME_UNAVAILABLE');
    }
    const nativeTotal = native.reduce((a, b) => a + b, 0), desiredTotal = desired.reduce((a, b) => a + b, 0);
    if (!(nativeTotal > 0) || !(desiredTotal > 0)) fail('EMPTY_DISTRIBUTION');
    if (native.every((weight, index) => weight === desired[index])) return value;
    let targetBefore = 0, nativeBefore = 0;
    const target = value * desiredTotal;
    for (let index = 0; index < desired.length; index++) {
      if (desired[index] > 0 && target < targetBefore + desired[index]) {
        // Stay strictly inside the native interval: its sampler uses <= boundaries.
        const fraction = Math.min(1 - 1e-9, Math.max(1e-9, (target - targetBefore) / desired[index]));
        return Math.min(1 - Number.EPSILON, (nativeBefore + fraction * native[index]) / nativeTotal);
      }
      targetBefore += desired[index]; nativeBefore += native[index];
    }
    fail('DISTRIBUTION_ROUNDING_UNCONFIRMED');
  }
  const mapBernoulli = (value, originalPercent, targetPercent) => mapCategorical(value,
    [number(originalPercent, 0, 100) / 100, 1 - originalPercent / 100], [number(targetPercent, 0, 100) / 100, 1 - targetPercent / 100]);
  function inspectWheel(board, profile) {
    const { native, desired } = wheelWeights(board, profile), total = desired.reduce((a, b) => a + b, 0), nativeTotal = native.reduce((a, b) => a + b, 0);
    return board.map((prize, index) => ({ index, kind: prize.kind, key: prize.key ?? null, quality: prize.quality,
      group: groupOf(prize), originalPercent: native[index] / nativeTotal * 100, percent: desired[index] / total * 100 }));
  }
  return { validate, groupOf, wheelWeights, mapCategorical, mapBernoulli, inspectWheel, cowPools };
}
module.exports = { createProbabilityModel };
