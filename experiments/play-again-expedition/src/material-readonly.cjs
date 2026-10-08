'use strict';

// Fixed, standalone read-only function for the existing preload world.
// No operation selector, setter, serialization/save call, or global identity cache.
function inspectProtectionScroll(expectedDisplayedQuantity) {
  const fail = code => { throw new Error(code); };
  const validQuantity = value => Number.isSafeInteger(value) && value >= 0 && value <= 1_000_000_000;
  if (!validQuantity(expectedDisplayedQuantity)) fail('INVALID_DISPLAYED_QUANTITY');
  const game = window.expedition;
  if (!game || typeof game.getUi !== 'function') fail('GAME_NOT_READY');
  const ui = game.getUi();
  if (ui?.entered !== true) fail('ENTER_EXISTING_SAVE');
  if (ui.gameMode !== 'expedition') fail('MODE_NOT_SUPPORTED');
  if (game.platform?.storageBlocked === true || typeof game.platform?.read !== 'function') fail('SAVE_CONTRACT_CHANGED');
  const engine = game.engine;
  const state = engine?.state;
  const materials = state?.materials;
  if (!materials || typeof materials !== 'object' || Array.isArray(materials) || !Object.hasOwn(materials, 'protectionScrolls')) fail('MATERIAL_CONTRACT_CHANGED');
  const runtimeQuantity = materials.protectionScrolls;
  if (!validQuantity(runtimeQuantity)) fail('INVALID_RUNTIME_QUANTITY');
  const raw = game.platform.read();
  if (typeof raw !== 'string' || raw.length === 0 || raw.length > 4 * 1024 * 1024) fail('SAVE_CONTRACT_CHANGED');
  const saved = JSON.parse(raw);
  if (saved?.version !== 38 || !validQuantity(saved?.materials?.protectionScrolls)) fail('SAVE_FORMAT_CHANGED');
  if (localStorage.length > 128) fail('STORAGE_CONTRACT_CHANGED');
  const matchedKeys = [];
  for (let index = 0; index < localStorage.length; index++) {
    const key = localStorage.key(index);
    if (/^zseb-expedition-v1(?:-slot-[1-4])?$/.test(key) && localStorage.getItem(key) === raw) matchedKeys.push(key);
  }
  if (matchedKeys.length !== 1) fail('SAVE_KEY_AMBIGUOUS');
  if (runtimeQuantity !== saved.materials.protectionScrolls || runtimeQuantity !== expectedDisplayedQuantity) fail('QUANTITY_MISMATCH');
  if (game.engine !== engine || engine.state !== state || game.platform.read() !== raw) fail('SAVE_CHANGED_DURING_READ');
  return {
    field: 'materials.protectionScrolls', mode: ui.gameMode,
    journeyMode: engine.mode ?? null, activeSaveKey: matchedKeys[0], saveFormat: saved.version,
    runtimeQuantity, storedQuantity: saved.materials.protectionScrolls,
    expectedDisplayedQuantity, quantitiesMatch: true, readOnly: true,
  };
}

module.exports = { inspectProtectionScroll };
