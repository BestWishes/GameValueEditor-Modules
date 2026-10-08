'use strict';

// This function is evaluated in the game's existing isolated renderer world.
// No addresses, factories, arbitrary field names, or arbitrary caller code.
function materialTransaction(operation, argumentsObject, instanceNonce) {
  const fail = code => { throw new Error(code); };
  if (!['read', 'prepare', 'commit'].includes(operation)) fail('UNKNOWN_OPERATION');
  const game = window.expedition;
  if (!game || typeof game.getUi !== 'function') fail('GAME_NOT_READY');
  const ui = game.getUi();
  if (ui?.entered !== true) fail('ENTER_EXISTING_SAVE');
  if (game.platform?.storageBlocked === true) fail('SAVE_CONTRACT_CHANGED');
  const engine = game.engine;
  const state = engine?.state;
  const materials = state?.materials;
  if (!materials || typeof materials !== 'object' || Array.isArray(materials) || typeof engine.serialize !== 'function' || typeof game.platform?.save !== 'function' || typeof game.platform?.read !== 'function') fail('CONTRACT_CHANGED');
  if (ui.gameMode !== 'expedition') fail('MODE_NOT_SUPPORTED');
  const valid = value => Number.isSafeInteger(value) && value >= 0 && value <= 1000000000;
  if (!Object.hasOwn(materials, 'protectionScrolls') || !valid(materials.protectionScrolls)) fail('INVALID_CURRENT_VALUE');
  // Live inspection proved slotActive is not exported by getUi(). Resolve the
  // platform's selected save through its own read API and the origin storage.
  const stored = game.platform.read();
  if (typeof stored !== 'string' || stored.length > 4 * 1024 * 1024 || localStorage.length > 128) fail('SAVE_CONTRACT_CHANGED');
  const mainSaves = [];
  for (let index = 0; index < localStorage.length; index++) {
    const key = localStorage.key(index);
    if (/^zseb-expedition-v1(?:-slot-[1-4])?$/.test(key)) mainSaves.push({ key, raw: localStorage.getItem(key) });
  }
  const matches = mainSaves.filter(entry => entry.raw === stored);
  if (matches.length !== 1) fail('SAVE_KEY_AMBIGUOUS');
  const activeSaveKey = matches[0].key;
  const scope = JSON.stringify([engine.mode ?? null, ui.gameMode, activeSaveKey]);
  const identityKey = '__gveProtectionScrollIdentity';
  let identity = globalThis[identityKey];
  if (!identity || identity.nonce !== instanceNonce) identity = globalThis[identityKey] = { nonce: instanceNonce, revision: 0 };
  if (identity.engine !== engine || identity.state !== state || identity.scope !== scope) {
    identity.engine = engine;
    identity.state = state;
    identity.scope = scope;
    identity.revision++;
  }
  const instance = instanceNonce + ':' + identity.revision;
  const summary = () => ({ instance, value: materials.protectionScrolls, maximum: 1000000000, mode: ui.gameMode, slot: activeSaveKey });
  if (operation === 'read') return summary();
  if (!argumentsObject || argumentsObject.expectedInstance !== instance) fail('SAVE_INSTANCE_CHANGED');
  if (!valid(argumentsObject.expectedValue) || materials.protectionScrolls !== argumentsObject.expectedValue) fail('VALUE_CHANGED');
  if (!valid(argumentsObject.targetValue)) fail('INVALID_TARGET_VALUE');
  if (materials.protectionScrolls === argumentsObject.targetValue) return { ...summary(), unchanged: true };
  const serialized = engine.serialize();
  if (typeof serialized !== 'string' || serialized.length > 4 * 1024 * 1024) fail('INVALID_SERIALIZATION');
  const before = JSON.parse(serialized);
  if (before.version !== 38 || before.materials?.protectionScrolls !== materials.protectionScrolls) fail('SAVE_CONTRACT_CHANGED');
  if (operation === 'prepare') return { ...summary(), beforeSave: serialized };
  if (argumentsObject.beforeSave !== serialized) fail('SNAPSHOT_CHANGED');
  const original = materials.protectionScrolls;
  materials.protectionScrolls = argumentsObject.targetValue;
  let afterSave;
  try {
    afterSave = engine.serialize();
    const after = JSON.parse(afterSave);
    if (after.version !== 38 || after.materials?.protectionScrolls !== argumentsObject.targetValue) fail('SERIALIZATION_MISMATCH');
    after.materials.protectionScrolls = original;
    if (JSON.stringify(before) !== JSON.stringify(after)) fail('UNRELATED_FIELDS_CHANGED');
  } catch (error) {
    materials.protectionScrolls = original;
    throw error;
  }
  try {
    // The game's save API is synchronous in this verified build. Do not accept
    // a Promise merely because it is truthy, or retry a possibly started save.
    const saved = game.platform.save(afterSave);
    if (saved && typeof saved.then === 'function') fail('SAVE_RESULT_UNCERTAIN');
    if (saved !== true) fail('SAVE_FAILED_LIVE_RESTORED');
  } catch (error) {
    materials.protectionScrolls = original;
    throw error;
  }
  if (game.engine !== engine || engine.state !== state || materials.protectionScrolls !== argumentsObject.targetValue) fail('SAVE_RESULT_UNCERTAIN');
  // Check the actual origin storage, not a second read from our changed object.
  if (game.platform.read() !== afterSave || localStorage.getItem(activeSaveKey) !== afterSave || mainSaves.some(entry => entry.key !== activeSaveKey && localStorage.getItem(entry.key) !== entry.raw)) fail('PERSISTENCE_UNCONFIRMED');
  return { ...summary(), previousValue: original, storageVerified: true, onlyMaterialChanged: true };
}

module.exports = { materialTransaction };
