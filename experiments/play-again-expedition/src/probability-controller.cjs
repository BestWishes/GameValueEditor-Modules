'use strict';
const { createProbabilityModel } = require('./probability-model.cjs');
const { createProbabilitySession } = require('./probability-session.cjs');
const { createProbabilityRuntime } = require('./probability-runtime.cjs');
const { assertReadonlyContext } = require('./read-context-contract.cjs');
const { validateNativeDrops } = require('./drops-native-validation.cjs');
const { discoverProbabilitySites } = require('./probability-sites.cjs');

function buildProbabilityRequest(config, operation, input = {}, namespace) {
  if (!['inspect', 'probe', 'configure', 'reset'].includes(operation)) throw Error('UNKNOWN_PROBABILITY_OPERATION');
  if (namespace !== undefined && !['wheel', 'drops'].includes(namespace)) throw Error('UNKNOWN_PROBABILITY_NAMESPACE');
  if (!Number.isSafeInteger(config?.processId) || config.processId <= 0 ||
    ['executablePath', 'archivePath', 'profilePath'].some(key => typeof config[key] !== 'string' || !config[key])) throw Error('INVALID_CONTROLLER_CONTEXT');
  const readonly = ['inspect', 'probe'].includes(operation);
  const allowed = readonly ? [] : operation === 'configure' ? ['profile', 'scopeHash', 'revision'] : ['scopeHash', 'revision'];
  if (!input || typeof input !== 'object' || Array.isArray(input) || Object.keys(input).some(key => !allowed.includes(key))) throw Error('UNKNOWN_PROBABILITY_ARGUMENT');
  if (!readonly && (!/^[a-f0-9]{64}$/.test(input.scopeHash) || !Number.isSafeInteger(input.revision) || input.revision < 0)) throw Error('INVALID_CONTROLLER_SCOPE');
  if (operation === 'configure') {
    const profile = createProbabilityModel().validate(input.profile);
    if (namespace === 'wheel' && profile.drops !== undefined || namespace === 'drops' && Object.keys(profile).some(key => key !== 'drops')) throw Error('PROBABILITY_NAMESPACE_MISMATCH');
  }
  const runtimeSource = `(${createProbabilityRuntime.toString()})(${createProbabilityModel.toString()},${createProbabilitySession.toString()},${validateNativeDrops.toString()},${discoverProbabilitySites.toString()})`;
  const expression = `(async function () {
    const config = ${JSON.stringify(config)}, operation = ${JSON.stringify(operation)}, input = ${JSON.stringify(input)};
    const namespace = ${JSON.stringify(namespace ?? null)};
    if (process.pid !== config.processId || process.execPath !== config.executablePath || !process.versions.electron ||
      !process.versions.node || process.arch !== 'x64') throw Error('SESSION_BUILD_CHANGED');
    const electron = process.mainModule.require('electron');
    if (electron.app.getPath('userData') !== config.profilePath) throw Error('ORIGINAL_PROFILE_CHANGED');
    const windows = electron.BrowserWindow.getAllWindows().filter(w => !w.isDestroyed() && w.webContents.getURL() === 'expedition://game/index.html');
    if (windows.length !== 1) throw Error('ORIGINAL_WINDOW_AMBIGUOUS');
    const contents = windows[0].webContents;
    const context = (${assertReadonlyContext.toString()})(contents.getLastWebPreferences(), electron.app.getAppPath(), config.archivePath);
    const hash = value => process.mainModule.require('node:crypto').createHash('sha256').update(value).digest('hex');
    const call = async (op, args) => {
      const code = "'use strict'; (() => { try { return { ok: true, result: (" + ${JSON.stringify(runtimeSource)} + ")(" + JSON.stringify(op) + "," + JSON.stringify(args) + "," + JSON.stringify(config.archivePath) + ") }; } catch (error) { return { ok: false, error: String(error.message) }; } })()";
      const packet = await contents.executeJavaScriptInIsolatedWorld(999, [{ code }], false);
      if (packet?.ok !== true) throw Error(packet?.error || 'RENDERER_RESULT_UNCONFIRMED');
      return packet.result;
    };
    const read = await call(${JSON.stringify(readonly ? operation : 'inspect')}, {}), scopeHash = hash(read.anchor);
    if (operation === 'inspect' || operation === 'probe') { delete read.anchor; return { probability: read, scopeHash, context }; }
    if (scopeHash !== input.scopeHash || read.revision !== input.revision) throw Error('READ_RECEIPT_SCOPE_CHANGED');
    const args = { expectedAnchor: read.anchor, expectedRevision: input.revision };
    if (namespace !== null) args.namespace = namespace;
    if (operation === 'configure') args.profile = input.profile;
    const result = await call(operation, args);
    if (hash(result.anchor) !== scopeHash || result.revision !== input.revision + 1) throw Error('PROBABILITY_REVISION_UNCONFIRMED');
    delete result.anchor; return { probability: result, scopeHash, context };
  })()`;
  return { id: 402, method: 'Runtime.evaluate', params: { expression, awaitPromise: true, returnByValue: true, timeout: 5000 } };
}
module.exports = { buildProbabilityRequest };
