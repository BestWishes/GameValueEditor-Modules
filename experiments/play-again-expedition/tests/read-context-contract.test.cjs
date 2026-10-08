'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const { assertReadonlyContext } = require('../src/read-context-contract.cjs');
const archive = 'C:\\Temp\\actual-game\\resources\\app.asar';
test('observed missing preload field is accepted only for the exact app and isolation', () => {
  const preferences = Object.freeze({ contextIsolation: true, nodeIntegration: false });
  const context = vm.createContext({ preferences });
  const result = vm.runInContext(`'use strict'; (${assertReadonlyContext.toString()})(preferences,${JSON.stringify(archive)},${JSON.stringify(archive)})`, context);
  assert.equal(result.originalAppPathVerified, true); assert.equal(result.preloadPathExposed, false);
});
test('Windows path separator variants identify the same exact package', () => {
  const result = assertReadonlyContext({ contextIsolation: true, nodeIntegration: false, preload: archive.replaceAll('\\', '/') + '/preload.cjs' }, archive.replaceAll('\\', '/'), archive);
  assert.equal(result.preloadPathExposed, true);
});
test('missing preload never permits another app path or another isolated world', () => {
  for (const actual of [null, '', 'C:/Temp/other/resources/app.asar', archive + '-copy']) {
    assert.throws(() => assertReadonlyContext({ contextIsolation: true, nodeIntegration: false }, actual, archive), /ORIGINAL_APP_PATH_CHANGED/);
  }
  for (const preferences of [{ contextIsolation: false, nodeIntegration: false }, { contextIsolation: true, nodeIntegration: true }, { contextIsolation: true }, null]) {
    assert.throws(() => assertReadonlyContext(preferences, archive, archive), /ISOLATION_CONTRACT_CHANGED/);
  }
});
test('an exposed changed or null preload path still fails closed', () => {
  for (const preload of [null, '', 'C:/Temp/other/preload.cjs']) {
    assert.throws(() => assertReadonlyContext({ contextIsolation: true, nodeIntegration: false, preload }, archive, archive), /PRELOAD_PATH_CHANGED/);
  }
});
