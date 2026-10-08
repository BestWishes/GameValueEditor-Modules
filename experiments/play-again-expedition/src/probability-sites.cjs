'use strict';

// Discover locations in the CURRENT native Engine. Only isolated in-memory
// objects receive actions; no original action, platform save or global RNG.
function discoverProbabilitySites(engine, model, archivePath) {
  const normalize = value => value.trim().replaceAll('\\', '/').toLowerCase();
  const archive = normalize(archivePath) + '/';
  const sites = {}, errors = {};
  const Engine = Object.getPrototypeOf(engine)?.constructor;
  if (typeof Engine !== 'function' || Engine.name !== 'Engine' || typeof engine.serialize !== 'function')
    return { sites, errors: { engine: 'PROBABILITY_CONSTRUCTOR_UNAVAILABLE' } };
  const snapshot = engine.serialize();
  function trace(key, action, selector, random = 0.5, first = false) {
    try {
      const frames = [], getters = [];
      const rng = function () {
        const error = new Error(); Error.captureStackTrace(error, rng);
        frames.push(normalize(error.stack.split('\n')[1] ?? '')); return random;
      };
      const copy = new Engine(snapshot, rng);
      if (copy === engine || copy.state === engine.state || copy.state.materials === engine.state.materials ||
          copy.state.inventory === engine.state.inventory || copy.state.luckyWheel === engine.state.luckyWheel ||
          copy.rng !== rng) throw Error('PROBABILITY_COPY_NOT_ISOLATED');
      // Prepare enough resources only on the copy; discovery is independent of
      // the player's current balances, progression and inventory capacity.
      copy.state.materials.wheelTickets = Math.max(10, copy.state.materials.wheelTickets || 0);
      copy.state.materials.cowTickets = Math.max(10, copy.state.materials.cowTickets || 0);
      const worn = new Set(copy.state.equipped.flatMap(Object.values));
      copy.state.inventory = copy.state.inventory.filter(item => worn.has(item.uid));
      for (const name of ['dropBonus', 'badgePartyBonus']) {
        const original = copy[name];
        if (typeof original !== 'function') continue;
        const wrapped = function (...args) {
          const error = new Error(); Error.captureStackTrace(error, wrapped);
          getters.push({ name, args, rngBefore: frames.length, frame: normalize(error.stack.split('\n')[1] ?? '') });
          return Reflect.apply(original, this, args);
        };
        copy[name] = wrapped;
      }
      frames.length = 0;
      action(copy);
      const selected = selector(frames, getters).filter(frame => frame.includes(archive));
      const unique = [...new Set(selected)];
      if (!unique.length || (!first && unique.length !== 1)) throw Error('PROBABILITY_CONSUMER_NOT_UNIQUE');
      sites[key] = first ? [selected[0]] : unique;
    } catch (error) { errors[key] = /^[A-Z_]+$/.test(error.message) ? error.message : 'PROBABILITY_CONSUMER_UNAVAILABLE'; }
  }
  const named = name => frames => frames.filter(frame => frame.includes('at ' + name.toLowerCase() + ' ('));
  const victory = mode => copy => {
    // Tickets are gated by journey progression, not just by winning a guardian.
    // Unlock that reward on the disposable copy even for early player saves.
    copy.state.cleared = Math.max(mode === 'guardian' ? 10000 : 400, copy.state.cleared);
    copy.state.selected = mode === 'guardian' ? 100 : 5; copy.state.guardianSelected = 1;
    copy.mode = mode; copy.start(); copy.enemy.hp = 0; copy.victory();
  };
  trace('wheelPrize', copy => { copy.prepareLuckyWheel(); copy.spinLuckyWheel(); }, named('chooseWheelIndex'));
  trace('wheelBonus', copy => { copy.prepareLuckyWheel(); copy.spinLuckyWheel(); }, named('rollWheelBonus'));
  trace('guardianTicket', victory('guardian'), frames => [...new Set(named('Engine.victory')(frames))].filter(frame => {
    const tickets = value => {
      const rng = function () { const error = new Error(); Error.captureStackTrace(error, rng);
        return normalize(error.stack.split('\n')[1] ?? '') === frame ? value : 0.99; };
      const copy = new Engine(snapshot, rng);
      if (copy.state === engine.state || copy.state.abyss === engine.state.abyss) throw Error('PROBABILITY_COPY_NOT_ISOLATED');
      copy.state.abyss.tickets = 10; const before = copy.state.abyss.tickets;
      victory('guardian')(copy); return copy.state.abyss.tickets - before;
    };
    return tickets(0) === 1 && tickets(0.999) === 0;
  }));
  trace('flame', copy => { copy.mode = 'flame'; copy.dropFlameImmortals(25400); }, named('Engine.dropFlameImmortals'));
  trace('ordinaryEquipment', victory('journey'), (_, getters) => getters.filter(row => row.name === 'dropBonus' &&
    row.frame.includes('at engine.victory (')).map(row => row.frame));
  trace('ordinaryRoll', victory('journey'), (frames, getters) => getters.filter(row => row.name === 'dropBonus' &&
    row.frame.includes('at engine.victory (')).map(row => frames[row.rngBefore - 1]).filter(Boolean));
  trace('equipmentUpgrade', copy => { copy.mode = 'journey'; copy.start(); copy.drop(copy.enemy, 100); },
    (_, getters) => getters.filter(row => row.name === 'badgePartyBonus' && row.args[0] === 'upgrade' &&
      row.frame.includes('at engine.drop (')).map(row => row.frame));
  trace('upgradeRoll', copy => { copy.mode = 'journey'; copy.start(); copy.drop(copy.enemy, 100); },
    (frames, getters) => getters.filter(row => row.name === 'badgePartyBonus' && row.args[0] === 'upgrade' &&
      row.frame.includes('at engine.drop (')).map(row => frames[row.rngBefore - 1]).filter(Boolean));
  const mine = action => copy => { copy.state.cleared = Math.max(400, copy.state.cleared); copy.startMine(150); action(copy); };
  trace('mineReward', mine(copy => copy.afterStarHit(2, 3766, false, false, {})),
    frames => frames.filter(frame => frame.includes('at engine.<anonymous> (') || frame.includes('at engine.afterstarhit (')));
  trace('mineDistribution', mine(copy => copy.dropMineReward(2)), named('Engine.dropMineReward'));
  trace('mineQuality', mine(copy => copy.dropOre()), named('Engine.dropOre'));
  trace('cowPool', copy => { copy.mode = 'cow'; copy.cowDrop86(model.cowPools.gear, 4); }, named('Engine.cowDrop86'), 0.5, true);
  trace('cowMythic', copy => {
    copy.state.cleared = Math.max(400, copy.state.cleared); copy.startCowDungeon(); copy.wave = 9;
    copy.spawn(); copy.enemy.hp = 0; copy.victory();
  }, frames => {
    const counts = new Map(); for (const frame of frames) counts.set(frame, (counts.get(frame) ?? 0) + 1);
    return frames.filter(frame => counts.get(frame) === model.cowPools.mythic.length);
  });
  if (engine.serialize() !== snapshot) throw Error('PROBABILITY_DISCOVERY_CHANGED_ORIGINAL');
  return { sites, errors };
}
module.exports = { discoverProbabilitySites };
