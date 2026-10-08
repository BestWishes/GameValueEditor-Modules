'use strict';

// A fixed, synchronous single-field trial in the existing isolated world.
// Complete save strings exist only inside this call and its controller memory.
// No global state, factory, arbitrary fields, or automatic retry/rollback after save.
function protectionScrollTrial(stage, input) {
  const fail = code => { throw Error(code); };
  if (!['prepare', 'commit'].includes(stage)) fail('UNKNOWN_TRIAL_STAGE');
  if (!input || !((input.expectedValue === 2 && input.targetValue === 3) ||
    (input.expectedValue === 3 && input.targetValue === 2))) fail('TRIAL_VALUES_NOT_ALLOWED');
  if (input.slot !== 'zseb-expedition-v1' || input.journeyMode !== 'guardian') fail('TRIAL_SCOPE_CHANGED');
  const game = window.expedition;
  if (!game || typeof game.getUi !== 'function') fail('GAME_NOT_READY');
  const ui = game.getUi();
  if (ui?.entered !== true || ui.gameMode !== 'expedition') fail('TRIAL_MODE_CHANGED');
  const engine = game.engine, state = engine?.state, materials = state?.materials;
  const platform = game.platform;
  if (!materials || typeof materials !== 'object' || Array.isArray(materials) ||
    engine.mode !== input.journeyMode || typeof engine.serialize !== 'function' ||
    platform?.storageBlocked === true || typeof platform?.read !== 'function' ||
    typeof platform?.save !== 'function') fail('TRIAL_CONTRACT_CHANGED');
  const descriptor = Object.getOwnPropertyDescriptor(materials, 'protectionScrolls');
  if (!descriptor || !Object.hasOwn(descriptor, 'value') || descriptor.writable !== true) fail('MATERIAL_NOT_WRITABLE_DATA');
  if (descriptor.value !== input.expectedValue) fail('TRIAL_VALUE_CHANGED');
  const parseSave = raw => {
    if (typeof raw !== 'string' || raw.length === 0 || raw.length > 4 * 1024 * 1024) fail('TRIAL_SAVE_INVALID');
    const save = JSON.parse(raw);
    if (save?.version !== 38 || save.materials?.protectionScrolls !== input.expectedValue) fail('TRIAL_SAVE_CHANGED');
    if (typeof save.rewardClockId !== 'string' || !save.rewardClockId.length || save.rewardClockId.length > 256 ||
      !Array.isArray(save.inventory) || save.inventory.some(item => !Number.isSafeInteger(item?.uid)) ||
      !save.characters || typeof save.characters !== 'object' || Array.isArray(save.characters) ||
      !Array.isArray(save.owned) || save.owned.some(id => !Number.isSafeInteger(id))) fail('TRIAL_ANCHOR_CONTRACT_CHANGED');
    return save;
  };
  const enumerateMainSaves = () => {
    if (localStorage.length > 128) fail('TRIAL_STORAGE_CONTRACT_CHANGED');
    const saves = [];
    for (let index = 0; index < localStorage.length; index++) {
      const key = localStorage.key(index);
      if (/^zseb-expedition-v1(?:-slot-[1-4])?$/.test(key)) saves.push({ key, raw: localStorage.getItem(key) });
    }
    return saves.sort((a, b) => a.key.localeCompare(b.key));
  };
  const storedBefore = platform.read();
  parseSave(storedBefore);
  const mainSaves = enumerateMainSaves();
  const matches = mainSaves.filter(save => save.raw === storedBefore);
  if (matches.length !== 1 || matches[0].key !== input.slot) fail('TRIAL_SAVE_KEY_CHANGED');
  const beforeSave = engine.serialize();
  const before = parseSave(beforeSave);
  const anchorOf = save => JSON.stringify([
    ui.gameMode, input.slot, engine.mode, save.rewardClockId,
    save.inventory.map(item => item.uid).sort((a, b) => a - b),
    Object.keys(save.characters).sort(), [...save.owned].sort((a, b) => a - b),
  ]);
  const anchor = anchorOf(before);
  if (anchorOf(parseSave(storedBefore)) !== anchor) fail('TRIAL_ANCHOR_CHANGED');
  const stillCurrent = () => game.engine === engine && engine.state === state && state.materials === materials &&
    game.platform === platform && game.getUi()?.entered === true && game.getUi()?.gameMode === 'expedition' &&
    engine.mode === input.journeyMode;
  if (!stillCurrent() || materials.protectionScrolls !== input.expectedValue || platform.read() !== storedBefore ||
    JSON.stringify(enumerateMainSaves()) !== JSON.stringify(mainSaves)) fail('TRIAL_CHANGED_DURING_PREPARE');
  if (stage === 'prepare') return {
    beforeSave, storedBefore, mainSaves, anchor, slot: input.slot, mode: ui.gameMode,
    journeyMode: engine.mode, value: input.expectedValue, targetValue: input.targetValue, nativeSaveCalled: false,
  };
  if (input.beforeSave !== beforeSave || input.storedBefore !== storedBefore || input.anchor !== anchor ||
    JSON.stringify(input.mainSaves) !== JSON.stringify(mainSaves)) fail('TRIAL_SNAPSHOT_CHANGED');

  let afterSave;
  try {
    materials.protectionScrolls = input.targetValue;
    afterSave = engine.serialize();
    if (typeof afterSave !== 'string' || !afterSave.length || afterSave.length > 4 * 1024 * 1024) fail('TRIAL_SERIALIZATION_INVALID');
    const after = JSON.parse(afterSave);
    if (after.version !== 38 || after.materials?.protectionScrolls !== input.targetValue) fail('TRIAL_SERIALIZATION_MISMATCH');
    after.materials.protectionScrolls = input.expectedValue;
    if (JSON.stringify(after) !== JSON.stringify(before)) fail('TRIAL_UNRELATED_FIELDS_CHANGED');
    if (!stillCurrent() || materials.protectionScrolls !== input.targetValue || platform.read() !== storedBefore ||
      JSON.stringify(enumerateMainSaves()) !== JSON.stringify(mainSaves)) fail('TRIAL_CHANGED_BEFORE_SAVE');
  } catch (error) {
    // The native save has NOT been called; restore only our own unchanged field.
    let liveRestored = false;
    if (stillCurrent() && materials.protectionScrolls === input.targetValue) {
      materials.protectionScrolls = input.expectedValue;
      liveRestored = materials.protectionScrolls === input.expectedValue;
    }
    return { status: 'rejected-before-save', error: String(error.message), nativeSaveCalled: false, liveRestored, retryAllowed: false };
  }
  try {
    const saveResult = platform.save(afterSave);
    if (saveResult !== true) fail('TRIAL_SAVE_RESULT_UNCERTAIN');
    if (!stillCurrent() || materials.protectionScrolls !== input.targetValue) fail('TRIAL_INSTANCE_CHANGED_AFTER_SAVE');
    const expectedSaves = mainSaves.map(save => ({ key: save.key, raw: save.key === input.slot ? afterSave : save.raw }));
    if (platform.read() !== afterSave || localStorage.getItem(input.slot) !== afterSave ||
      JSON.stringify(enumerateMainSaves()) !== JSON.stringify(expectedSaves)) fail('TRIAL_STORAGE_UNCONFIRMED');
    if (engine.serialize() !== afterSave || !stillCurrent()) fail('TRIAL_LIVE_CHANGED_AFTER_SAVE');
    return {
      status: 'storage-verified', previousValue: input.expectedValue, value: input.targetValue,
      slot: input.slot, mode: ui.gameMode, journeyMode: engine.mode, saveFormat: 38,
      nativeSaveCalled: true, storageVerified: true, onlySerializedMaterialChanged: true,
      retryAllowed: false, afterSave,
    };
  } catch (error) {
    // Save may already have persisted. Never undo or reapply it automatically.
    return { status: 'uncertain-after-save', error: String(error.message), nativeSaveCalled: true, retryAllowed: false };
  }
}

// Keep fresh preparation and commit inside ONE synchronous renderer dispatch.
// A prior preflight is only an identity anchor, never a stale full snapshot.
function executeProtectionTrialAtomically(trial, input, expectedAnchor) {
  const game = window.expedition;
  const engine = game?.engine, state = engine?.state, platform = game?.platform;
  const prepared = trial('prepare', input);
  if (prepared.anchor !== expectedAnchor) throw Error('TRIAL_ORIGINAL_ANCHOR_CHANGED');
  if (window.expedition !== game || game.engine !== engine || engine.state !== state || game.platform !== platform) throw Error('TRIAL_ATOMIC_INSTANCE_CHANGED');
  const outcome = trial('commit', { ...input, ...prepared, expectedValue: input.expectedValue });
  return { beforeSave: prepared.beforeSave, outcome };
}

module.exports = { protectionScrollTrial, executeProtectionTrialAtomically };
