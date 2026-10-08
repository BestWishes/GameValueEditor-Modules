'use strict';

// Fixed operations in original preload world 999. No game action or save calls.
function createProbabilityRuntime(modelFactory, sessionFactory, dropsValidator, siteFactory) {
  const registryKey = Symbol.for('GameValueEditor.PlayAgainExpedition.ProbabilitySession.v1');
  const fail = code => { throw Error(code); };
  const parse = raw => {
    if (typeof raw !== 'string' || raw.length > 4 * 1024 * 1024) fail('SAVE_INVALID');
    const save = JSON.parse(raw);
    if (!Number.isSafeInteger(save?.version) || save.version <= 0 || typeof save.rewardClockId !== 'string' || !save.rewardClockId.length ||
      save.rewardClockId.length > 256 || !save.materials || !Array.isArray(save.inventory)) fail('SAVE_IDENTITY_INVALID');
    return save;
  };
  return function operate(operation, input, archivePath) {
    if (!['inspect', 'probe', 'configure', 'reset'].includes(operation)) fail('UNKNOWN_PROBABILITY_OPERATION');
    if (!input || typeof input !== 'object' || Array.isArray(input) ||
      Object.keys(input).some(key => !['expectedAnchor', 'expectedRevision', 'profile', 'namespace'].includes(key))) fail('UNKNOWN_PROBABILITY_ARGUMENT');
    if (input.namespace !== undefined && (!['wheel', 'drops'].includes(input.namespace) || ['inspect', 'probe'].includes(operation))) fail('UNKNOWN_PROBABILITY_NAMESPACE');
    if (operation !== 'configure' && input.profile !== undefined) fail('UNKNOWN_PROBABILITY_ARGUMENT');
    const model = modelFactory();
    if (operation === 'configure') model.validate(input.profile);
    const game = window.expedition, engine = game?.engine, state = engine?.state, platform = game?.platform;
    if (!game || typeof game.getUi !== 'function' || game.getUi()?.entered !== true) fail('GAME_NOT_ENTERED');
    if (game.getUi()?.gameMode !== 'expedition' || !['journey', 'guardian', 'abyss', 'flame', 'mine', 'cow'].includes(engine?.mode)) fail('GAME_MODE_NOT_SUPPORTED');
    if (!state || typeof engine.serialize !== 'function' || typeof platform?.read !== 'function') fail('PROBABILITY_CONTRACT_CHANGED');
    const storedRaw = platform.read(), stored = parse(storedRaw);
    if (state.rewardClockId !== stored.rewardClockId || localStorage.length > 128) fail('LIVE_SAVE_IDENTITY_CHANGED');
    const matches = [];
    for (let index = 0; index < localStorage.length; index++) {
      const key = localStorage.key(index);
      if (/^zseb-expedition-v1(?:-slot-[1-4])?$/.test(key) && localStorage.getItem(key) === storedRaw) matches.push(key);
    }
    if (matches.length !== 1) fail('ACTIVE_SAVE_AMBIGUOUS');
    const slot = matches[0], rewardClockId = stored.rewardClockId;
    const isCurrent = () => {
      if (window.expedition !== game || game.engine !== engine || engine.state !== state || game.platform !== platform ||
        game.getUi()?.entered !== true || game.getUi()?.gameMode !== 'expedition' || state.rewardClockId !== rewardClockId) return false;
      // UID/material totals legitimately change after loot. Bind slot identity, not old progress.
      return localStorage.getItem(slot) === platform.read();
    };
    const descriptor = Object.getOwnPropertyDescriptor(window, registryKey);
    if (descriptor && (!Object.hasOwn(descriptor, 'value') || descriptor.configurable !== true || descriptor.writable !== true)) fail('PROBABILITY_REGISTRY_CONFLICT');
    let registry = descriptor?.value;
    if (registry && (registry.brand !== 'gve-probability-v1' || !Number.isSafeInteger(registry.generation) ||
      !Number.isSafeInteger(registry.revision) || !registry.control || typeof registry.control.inspect !== 'function')) fail('PROBABILITY_REGISTRY_CONFLICT');
    const previousGeneration = registry?.generation ?? 0;
    const sameOwner = registry && registry.engine === engine && registry.state === state && registry.platform === platform &&
      registry.slot === slot && registry.rewardClockId === rewardClockId;
    if (registry && !sameOwner) {
      if (operation !== 'inspect') fail('PROBABILITY_SESSION_CHANGED');
      registry = null; // Old owner will pass through, never retarget its hooks.
    }
    if (registry && registry.implementationVersion !== 14) {
      if (operation !== 'inspect' || registry.control.inspect().installed) fail('PROBABILITY_IMPLEMENTATION_CHANGED_RESTART_REQUIRED');
      registry = null; // Upgrade only an uninstalled read-only controller; invalidate old receipts.
    }
    const generation = registry?.generation ?? previousGeneration + 1;
    if (!Number.isFinite(window.performance?.timeOrigin)) fail('RENDERER_IDENTITY_UNCONFIRMED');
    const anchor = JSON.stringify(['expedition', slot, rewardClockId, window.performance.timeOrigin, generation]);
    if (!registry) {
      const discovery = typeof siteFactory === 'function' ? siteFactory(engine, model, archivePath) : null;
      const control = sessionFactory(engine, model, { archivePath, ...(discovery ? { sites: discovery.sites } : {}), assertCurrent: isCurrent });
      registry = { brand: 'gve-probability-v1', implementationVersion: 14, discovery, engine, state, platform, slot, rewardClockId, anchor, generation, revision: 0, control };
      // Inspection only caches module-owned identity. It does not install hooks or change a save.
      Object.defineProperty(window, registryKey, { value: registry, configurable: true, writable: true });
    }
    const control = registry.control;
    let revision = registry.revision, outcome;
    let diagnostic = null;
    if (operation === 'probe') {
      if (control.inspect().installed || engine.phase !== 'idle' || state.luckyWheel?.pending) fail('PROBABILITY_PROBE_NOT_SAFE');
      const original = engine.serialize(), storageBefore = platform.read();
      const Engine = Object.getPrototypeOf(engine)?.constructor;
      if (typeof Engine !== 'function' || Engine.name !== 'Engine') fail('PROBABILITY_CONSTRUCTOR_NOT_VERIFIED');
      const prepareCopy = copy => {
        // Observe generated rewards before the player's recycling preferences
        // can immediately turn test equipment into materials. Copy only.
        for (const rule of Object.values(copy.state.recycle ?? {})) if (rule && typeof rule === 'object') {
          rule.enabled = false;
          for (const option of Object.values(rule)) if (option && typeof option === 'object' && !Array.isArray(option)) option.enabled = false;
        }
        copy.state.materials.wheelTickets = 10; copy.state.materials.cowTickets = 10;
        copy.state.abyss.tickets = 10;
        copy.state.ores = [];
        const worn = new Set(copy.state.equipped.flatMap(Object.values));
        copy.state.inventory = copy.state.inventory.filter(item => worn.has(item.uid));
        if (!Array.isArray(copy.state.luckyWheel?.board)) copy.prepareLuckyWheel();
      };
      const traced = (action, random = 0.5) => {
        const frames = [];
        const traceRng = function () {
        const error = new Error(); Error.captureStackTrace(error, traceRng);
        const frame = (error.stack.split('\n')[1] ?? '').replaceAll('\\', '/');
        const site = frame.match(/([a-z0-9_-]+\.(?:js|cjs)(?:\.jsc)?):(\d+):(\d+)/i);
        const name = frame.match(/^\s*at ([a-z0-9_.$<>]+) \(/i)?.[1] ?? null;
        if (frames.length < 48) frames.push({ name, file: site?.[1] ?? null, line: site ? Number(site[2]) : null,
          column: site ? Number(site[3]) : null, archivePathMatches: frame.toLowerCase().includes(archivePath.replaceAll('\\', '/').toLowerCase()) });
        return random;
        };
        const copy = new Engine(original, traceRng);
        if (copy === engine || copy.state === state || copy.state.materials === state.materials || copy.state.inventory === state.inventory ||
          copy.state.luckyWheel === state.luckyWheel || copy.rng !== traceRng) fail('PROBABILITY_COPY_NOT_ISOLATED');
        prepareCopy(copy); const result = action(copy);
        return { frames, result };
      };
      const wheel = traced(copy => { const before = copy.state.materials.wheelTickets; copy.spinLuckyWheel(); return { ticketsConsumed: before - copy.state.materials.wheelTickets }; });
      const victories = {};
      for (const mode of ['journey', 'guardian', 'abyss']) victories[mode] = traced(copy => {
        copy.state.cleared = 400; copy.state.selected = 5; copy.state.guardianSelected = 1;
        if (mode === 'guardian' || mode === 'abyss') { copy.state.cleared = 10000; copy.state.selected = 100; }
        copy.mode = mode; copy.start(); if (!copy.enemy) fail('PROBABILITY_PROBE_ENEMY_UNAVAILABLE');
        copy.enemy.hp = 0; copy.victory(); return { mode };
      });
      const flame = traced(copy => { copy.mode = 'flame'; copy.dropFlameImmortals(25400); return { mode: 'flame' }; });
      // Trace only fresh native copies. These observations do not enable a rule.
      const getterFrames = [];
      const getters = traced(copy => {
        for (const name of ['dropBonus', 'badgePartyBonus', 'costumePartyBonus']) {
          const originalGetter = copy[name];
          copy[name] = function (...args) {
            const error = new Error(); Error.captureStackTrace(error, copy[name]);
            if (getterFrames.length < 20) getterFrames.push({ name, args, frame: error.stack.split('\n')[1] });
            return Reflect.apply(originalGetter, this, args);
          };
        }
        copy.state.cleared = 400; copy.state.selected = 5;
        copy.mode = 'journey'; copy.start(); copy.enemy.hp = 0; copy.victory(); copy.drop(copy.enemy, 100);
        return { mode: 'journey' };
      });
      const mining = traced(copy => {
        copy.state.cleared = 400; copy.startMine(150); copy.dropMineReward(2);
        for (let index = 0; index < 80; index++) copy.tick(0.1);
        return { mode: copy.mode };
      });
      const miningOre = traced(copy => {
        copy.state.cleared = 400; copy.startMine(150); copy.dropMineReward(2);
        return { mode: copy.mode };
      }, 0.99);
      const cows = {};
      for (const wave of [0, 2, 9]) cows[wave] = traced(copy => {
        copy.state.cleared = 400; copy.state.materials.cowTickets = 10;
        const worn = new Set(copy.state.equipped.flatMap(Object.values));
        copy.state.inventory = copy.state.inventory.filter(item => worn.has(item.uid));
        if (!copy.startCowDungeon()) fail('COW_PROBE_START_FAILED');
        copy.wave = wave; copy.spawn(); copy.enemy.hp = 0; copy.victory();
        return { mode: copy.mode };
      });
      let passedCases = 0;
      const ensure = condition => { if (!condition) fail('NATIVE_BUNDLE_PROBABILITY_VALIDATION_FAILED'); passedCases++; };
      const controlled = (profile, setup, action) => {
        const copy = new Engine(original, () => 0.5); prepareCopy(copy); if (setup) setup(copy);
        // A player's untouched wheel need not have a board yet. Initialize only
        // this isolated test object, not the live game's wheel or its balance.
        if (typeof copy.prepareLuckyWheel === 'function' && !Array.isArray(copy.state.luckyWheel?.board)) copy.prepareLuckyWheel();
        const manager = sessionFactory(copy, model, { archivePath, ...(registry.discovery ? { sites: registry.discovery.sites } : {}), assertCurrent: () => true }), before = copy.serialize(), rng = copy.rng;
        manager.configure(profile); ensure(copy.serialize() === before);
        const result = action(copy), report = manager.inspect();
        ensure(report.blocked === null && copy.rng === rng);
        // Discard the isolated copy; a wheel action intentionally still has pending animation.
        // Do not bypass the real manager's pending guard just to clean up a fixture.
        return { result, report };
      };
      const groups = ['ordinary', 'immortal', 'mythic', 'protection', 'sacredProtection'];
      for (const group of groups) {
        const tested = controlled({ wheel: { groupPercent: Object.fromEntries(groups.map(key => [key, key === group ? 100 : 0])) },
          wheelBonus: { doublePercent: 0, marqueePercent: 0 } }, null, copy => {
          const coins = copy.state.materials.wheelTickets; copy.spinLuckyWheel();
          ensure(copy.state.materials.wheelTickets === coins - 1);
          return copy.luckyWheelResults().map(row => model.groupOf(row.prize));
        }); ensure(tested.result.includes(group) && tested.report.hits.wheelPrize === 1 && tested.report.hits.wheelBonus === 1);
      }
      for (const [doublePercent, marqueePercent, bonus] of [[100, 0, 'double'], [0, 100, 'marquee'], [0, 0, null]]) {
        const tested = controlled({ wheelBonus: { doublePercent, marqueePercent } }, null, copy => {
          copy.spinLuckyWheel(); return copy.state.luckyWheel.pending.bonus;
        }); ensure(tested.result === bonus && tested.report.hits.wheelBonus === 1);
      }
      for (const percent of [0, 100]) {
        const guard = controlled({ drops: { guardianTicketPercent: percent } }, copy => {
          copy.mode = 'guardian'; copy.state.cleared = 10000; copy.state.selected = 100; copy.state.guardianSelected = 1;
        }, copy => {
          copy.start(); copy.enemy.hp = 0; const tickets = copy.state.abyss.tickets; copy.victory(); return copy.state.abyss.tickets - tickets;
        }); ensure(guard.result === (percent === 100 ? 1 : 0) && guard.report.hits.guardianTicket === 1);
        const abyss = controlled({ drops: { abyssImmortalPercent: percent } }, copy => {
          copy.mode = 'abyss'; copy.state.cleared = 10000; copy.state.selected = 100;
          copy.state.abyss.progress[copy.abyssConfig().legacyIndex].pity = 0;
        }, copy => {
          copy.start(); copy.wave = copy.waveCount() - 1; copy.spawn();
          copy.enemy.hp = 0; const uids = new Set(copy.state.inventory.map(item => item.uid)); copy.victory();
          return copy.state.inventory.filter(item => !uids.has(item.uid) && item.quality === 5).length;
        }); ensure(abyss.result === (percent === 100 ? 1 : 0) && abyss.report.hits.abyssImmortal > 0);
        const gear = controlled({ drops: { equipmentBonusPercent: percent === 100 ? 100 : -100 } }, copy => {
          copy.mode = 'journey'; copy.state.cleared = 400; copy.state.selected = 5;
        }, copy => {
          copy.start(); copy.enemy.hp = 0; const uids = new Set(copy.state.inventory.map(item => item.uid)); copy.victory();
          return copy.state.inventory.filter(item => !uids.has(item.uid)).length;
        }); ensure(gear.result === (percent === 100 ? 1 : 0));
        for (let id = 25400; id <= 25404; id++) {
          const fire = controlled({ drops: { flameImmortalPercent: percent, flameMythicPercent: percent } }, copy => { copy.mode = 'flame'; }, copy => {
            const uids = new Set(copy.state.inventory.map(item => item.uid)); copy.dropFlameImmortals(id);
            return copy.state.inventory.filter(item => !uids.has(item.uid)).map(item => item.quality);
          }); const size = id === 25404 ? 3 : 2;
          ensure(fire.result.length === (percent === 100 ? size : 0) &&
            fire.report.hits[id === 25404 ? 'flameMythicPercent' : 'flameImmortalPercent'] === size);
        }
      }
      diagnostic = { wheel, victories, flame, getters, getterFrames, mining, miningOre, cows, nativeBundlePassedCases: passedCases,
        dropsValidation: typeof dropsValidator === 'function' ? dropsValidator(Engine, original, sessionFactory, model, archivePath,
          { sites: registry.discovery?.sites }) : null,
        originalTicketsConsumed: state.materials.wheelTickets - JSON.parse(original).materials.wheelTickets,
        originalGameActionCalled: false, originalSaveCalled: false, copyOnly: true };
      if (engine.serialize() !== original || platform.read() !== storageBefore || !isCurrent()) fail('PROBABILITY_PROBE_CHANGED_ORIGINAL');
    }
    if (!['inspect', 'probe'].includes(operation)) {
      if (input.expectedAnchor !== anchor || input.expectedRevision !== revision) fail('READ_RECEIPT_SCOPE_CHANGED');
      const before = engine.serialize(), storedBefore = platform.read();
      if (operation === 'configure') {
        outcome = control.configure(input.profile, input.namespace);
      } else {
        outcome = control.reset(input.namespace);
      }
      revision++; registry.revision = revision;
      if (engine.serialize() !== before || platform.read() !== storedBefore || !isCurrent()) fail('PROBABILITY_MANAGEMENT_CHANGED_SAVE');
    }
    if (!isCurrent()) fail('READ_INSTANCE_CHANGED');
    return { report: control.inspect(), outcome: outcome ?? null, diagnostic, anchor, slot, revision,
      phase: engine.phase, pendingWheel: !!state.luckyWheel?.pending,
      discoveryErrors: registry.discovery?.errors ?? {},
      sessionOnly: true, nativeSaveCalled: false, gameActionCalled: false, readOnly: ['inspect', 'probe'].includes(operation) };
  };
}

module.exports = { createProbabilityRuntime };
