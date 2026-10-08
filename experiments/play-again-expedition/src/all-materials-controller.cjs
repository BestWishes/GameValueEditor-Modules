'use strict';
const { createMaterialsRuntime } = require('./all-materials-runtime.cjs');
const { assertReadonlyContext } = require('./read-context-contract.cjs');

// Assemble only fixed operations. User input is JSON data, never source code.
function buildMaterialsRequest(config, operation, input = {}) {
  if (!['list', 'set'].includes(operation)) throw Error('UNKNOWN_CONTROLLER_OPERATION');
  if (!Number.isSafeInteger(config?.processId) || config.processId <= 0 ||
    typeof config.executablePath !== 'string' || !config.executablePath ||
    typeof config.archivePath !== 'string' || !config.archivePath ||
    typeof config.profilePath !== 'string' || !config.profilePath) throw Error('INVALID_CONTROLLER_CONTEXT');
  if (!input || typeof input !== 'object' || Array.isArray(input)) throw Error('INVALID_CONTROLLER_INPUT');
  const allowed = operation === 'set' ? ['materialId', 'expectedValue', 'targetValue', 'slot', 'journeyMode', 'scopeHash'] : [];
  if (Object.keys(input).some(key => !allowed.includes(key))) throw Error('UNKNOWN_CONTROLLER_ARGUMENT');
  if (operation === 'set') {
    const item = createMaterialsRuntime()('catalog').find(item => item.id === input.materialId);
    if (!item || !Number.isSafeInteger(input.expectedValue) || !Number.isSafeInteger(input.targetValue) ||
      input.expectedValue < item.minimum || input.expectedValue > item.maximum ||
      input.targetValue < item.minimum || input.targetValue > item.maximum) throw Error('INVALID_CONTROLLER_MATERIAL');
    if (!/^zseb-expedition-v1(?:-slot-[1-4])?$/.test(input.slot) || typeof input.journeyMode !== 'string' ||
      !input.journeyMode.length || input.journeyMode.length > 128 ||
      !/^[a-f0-9]{64}$/.test(input.scopeHash)) throw Error('INVALID_CONTROLLER_SCOPE');
  }
  const factorySource = createMaterialsRuntime.toString();
  const guardSource = assertReadonlyContext.toString();
  const expression = `(async function () {
    const config = ${JSON.stringify(config)}, operation = ${JSON.stringify(operation)}, input = ${JSON.stringify(input)};
    if (process.pid !== config.processId || process.execPath !== config.executablePath ||
        !process.versions.electron || !process.versions.node || process.arch !== 'x64') throw Error('SESSION_BUILD_CHANGED');
    const electron = process.mainModule.require('electron');
    if (electron.app.getPath('userData') !== config.profilePath) throw Error('ORIGINAL_PROFILE_CHANGED');
    const windows = electron.BrowserWindow.getAllWindows().filter(w => !w.isDestroyed() && w.webContents.getURL() === 'expedition://game/index.html');
    if (windows.length !== 1) throw Error('ORIGINAL_WINDOW_AMBIGUOUS');
    const contents = windows[0].webContents;
    const context = (${guardSource})(contents.getLastWebPreferences(), electron.app.getAppPath(), config.archivePath);
    const hash = value => process.mainModule.require('node:crypto').createHash('sha256').update(value).digest('hex');
    const call = async (op, args) => {
      const code = "'use strict'; (() => { try { return { ok: true, result: (" + ${JSON.stringify(factorySource)} + ")()(" + JSON.stringify(op) + "," + JSON.stringify(args) + ") }; } catch (error) { return { ok: false, error: String(error.message) }; } })()";
      const packet = await contents.executeJavaScriptInIsolatedWorld(999, [{ code }], false);
      if (packet?.ok !== true) throw Error(packet?.error || 'RENDERER_RESULT_UNCONFIRMED');
      return packet.result;
    };
    const read = await call('list', {});
    const scopeHash = hash(read.anchor);
    if (operation === 'list') {
      delete read.anchor;
      return { material: read, scopeHash, context, flushRequested: false };
    }
    if (scopeHash !== input.scopeHash || read.slot !== input.slot || read.journeyMode !== input.journeyMode) throw Error('READ_RECEIPT_SCOPE_CHANGED');
    const args = { ...input, anchor: read.anchor };
    delete args.scopeHash;
    // Full snapshot capture, assignment, save and comparison are synchronous in ONE renderer dispatch.
    const material = await call('set', args);
    const beforeHash = typeof material.beforeSave === 'string' ? hash(material.beforeSave) : null;
    const afterHash = typeof material.afterSave === 'string' ? hash(material.afterSave) : null;
    delete material.beforeSave; delete material.afterSave;
    let flushRequested = false, flushError = null;
    if (material.status === 'storage-verified') {
      try { contents.session.flushStorageData(); flushRequested = true; }
      catch { flushError = 'FLUSH_REQUEST_FAILED'; }
    }
    return { material, scopeHash, beforeHash, afterHash, context, flushRequested, flushError };
  })()`;
  return { id: 401, method: 'Runtime.evaluate', params: { expression, awaitPromise: true, returnByValue: true, timeout: 5000 } };
}

module.exports = { buildMaterialsRequest };
