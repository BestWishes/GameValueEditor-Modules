'use strict';

function validateFateProfile(profile) {
  const keys = ['blue', 'purple', 'immortal', 'mythic'];
  if (!profile || typeof profile !== 'object' || Array.isArray(profile) || Object.keys(profile).length !== 4 ||
    Object.keys(profile).some(key => !keys.includes(key)) || keys.some(key => !Number.isFinite(profile[key]) || profile[key] < 0 || profile[key] > 100) ||
    Math.abs(keys.reduce((sum, key) => sum + profile[key], 0) - 100) > 1e-8) throw Error('FATE_PROFILE_INVALID');
  return Object.fromEntries(keys.map(key => [key, profile[key]]));
}

// Ownership is per Engine. Does not change its prototype, definitions or save.
function createFateSession(engine, qualities, context, validate) {
  const fail = code => { throw Error(code); };
  const normalize = value => value.replaceAll('\\', '/').toLowerCase();
  if (!engine || typeof engine.sampleFate !== 'function' || typeof engine.rng !== 'function' || typeof engine.serialize !== 'function' ||
    typeof engine.fatePool !== 'function' || typeof context?.assertCurrent !== 'function' ||
    !Array.isArray(qualities) || qualities.length !== 4 || qualities.some((row, i) => row.quality !== i + 3 ||
      typeof row.name !== 'string' || !Number.isFinite(row.chance) || row.chance <= 0) ||
    Math.abs(qualities.reduce((sum, row) => sum + row.chance, 0) - 1) > 1e-10) fail('FATE_CONTRACT_CHANGED');
  const original = engine.sampleFate, own = Object.getOwnPropertyDescriptor(engine, 'sampleFate'), state = engine.state;
  const Engine = Object.getPrototypeOf(engine)?.constructor;
  if (typeof Engine !== 'function' || Engine.name !== 'Engine') fail('FATE_CONTRACT_CHANGED');
  const archive = normalize(context.archivePath) + '/';
  const id = state.owned?.find(id => Number.isSafeInteger(id) && engine.fatePool(id).some(row => row.quality === 6));
  if (!Number.isSafeInteger(id)) fail('FATE_POOL_UNAVAILABLE');
  // Discover the exact CURRENT native tier consumer on four fresh copies.
  // Never use old bundle offsets, and never sample the player's live character.
  let cursor = 0, tierSite;
  for (const row of qualities) {
    const frames = [], value = cursor + row.chance / 2;
    cursor += row.chance;
    const rng = function () {
      const error = new Error(); Error.captureStackTrace(error, rng);
      frames.push(normalize(error.stack.split('\n')[1] ?? '')); return value;
    };
    const copy = new Engine(engine.serialize(), rng);
    if (copy.state === state || copy === engine) fail('FATE_COPY_NOT_ISOLATED');
    frames.length = 0;
    const sampled = copy.sampleFate(id, false);
    if (sampled?.quality !== row.quality || frames.length !== 2 || !frames[0].includes('engine.samplefate') ||
      !frames[0].includes(archive) || frames[0] === frames[1] || tierSite && tierSite !== frames[0]) fail('FATE_CONSUMER_CHANGED');
    tierSite = frames[0];
  }
  let profile = null, hits = 0, installed = false, active = false, blocked = null;
  const current = () => {
    try { return engine.state === state && context.assertCurrent() === true; } catch { return false; }
  };
  const owned = () => !installed || Object.getOwnPropertyDescriptor(engine, 'sampleFate')?.value === sampler;
  const ready = () => current() && owned() && !active && engine.phase === 'idle' && !state.luckyWheel?.pending;
  function sampler(...args) {
    // Native second argument is the hundred-draw forced mythic roll. Preserve it.
    if (this !== engine || !profile || !current() || !owned() || blocked || args[1] === true || active)
      return Reflect.apply(original, this, args);
    const callerError = new Error(); Error.captureStackTrace(callerError, sampler);
    const paidCaller = normalize(callerError.stack.split('\n')[1] ?? '');
    // Do not alter free initial fates, recruitment or unrelated combat sampling.
    if (!paidCaller.includes('engine.samplepaidfates') || !paidCaller.includes(archive)) return Reflect.apply(original, this, args);
    const rng = engine.rng;
    const descriptor = Object.getOwnPropertyDescriptor(engine, 'rng');
    if (typeof rng !== 'function' || descriptor?.writable !== true) { blocked = 'FATE_RNG_CHANGED'; return Reflect.apply(original, this, args); }
    let seen = 0;
    const mapped = function (...rngArgs) {
      const value = Reflect.apply(rng, this, rngArgs);
      const error = new Error(); Error.captureStackTrace(error, mapped);
      const frame = normalize(error.stack.split('\n')[1] ?? '');
      if (frame !== tierSite || seen++) return value;
      if (!Number.isFinite(value) || value < 0 || value >= 1) { blocked = 'FATE_RNG_CHANGED'; return value; }
      const desired = ['blue','purple','immortal','mythic'].map(key => profile[key] / 100);
      let start = 0, nativeStart = 0;
      for (let index = 0; index < 4; index++) {
        const end = start + desired[index];
        if (desired[index] > 0 && (value < end || index === 3)) {
          hits++;
          const position = Math.max(0, Math.min(1 - Number.EPSILON, (value - start) / desired[index]));
          return Math.min(1 - Number.EPSILON, nativeStart + position * qualities[index].chance);
        }
        start = end; nativeStart += qualities[index].chance;
      }
      blocked = 'FATE_PROFILE_INVALID'; return value;
    };
    active = true; engine.rng = mapped;
    try { return Reflect.apply(original, this, args); }
    finally { if (engine.rng === mapped) engine.rng = rng; else blocked = 'FATE_RNG_CHANGED'; if (seen !== 1) blocked = 'FATE_CONSUMER_CHANGED'; active = false; }
  }
  function inspect() {
    return { installed, profile, hits, blocked, ready: ready() && !blocked, canRestore: ready(), ownerCurrent: current(), hooksOwned: owned(),
      original: Object.fromEntries(['blue','purple','immortal','mythic'].map((key, i) => [key, qualities[i].chance * 100])),
      preservesPity: true, singleAndTen: true };
  }
  return {
    inspect,
    configure(value) {
      const next = validate(value);
      if (!ready() || blocked) fail('FATE_BUSY_OR_CONFLICT');
      if (!installed) {
        if (engine.sampleFate !== original || own && own.configurable !== true) fail('FATE_HOOK_CONFLICT');
        Object.defineProperty(engine, 'sampleFate', { value: sampler, configurable: true, writable: true }); installed = true;
      }
      profile = next; return inspect();
    },
    reset() {
      if (!ready()) fail('FATE_BUSY_OR_CONFLICT');
      if (installed) { if (own) Object.defineProperty(engine, 'sampleFate', own); else delete engine.sampleFate; }
      installed = false; profile = null; blocked = null; return inspect();
    }
  };
}
module.exports = { validateFateProfile, createFateSession };
