'use strict';

// Constructor options are not all exposed by getLastWebPreferences in this
// observed runtime. Require the known original app path and isolation settings;
// if a preload path is exposed, it must also match the known package entry.
function assertReadonlyContext(preferences, actualAppPath, expectedAppPath) {
  const normalized = value => typeof value === 'string' && value.length > 0 ? value.replaceAll('\\', '/') : null;
  const expected = normalized(expectedAppPath);
  if (!expected || normalized(actualAppPath) !== expected) throw Error('ORIGINAL_APP_PATH_CHANGED');
  if (preferences?.contextIsolation !== true || preferences?.nodeIntegration !== false) throw Error('ISOLATION_CONTRACT_CHANGED');
  if (preferences.preload !== undefined && normalized(preferences.preload) !== expected + '/preload.cjs') throw Error('PRELOAD_PATH_CHANGED');
  return { originalAppPathVerified: true, contextIsolation: true, nodeIntegration: false, preloadPathExposed: preferences.preload !== undefined };
}

module.exports = { assertReadonlyContext };
