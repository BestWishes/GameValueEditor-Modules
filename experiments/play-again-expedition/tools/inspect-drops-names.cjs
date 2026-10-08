'use strict';
const fs = require('original-fs'), path = require('node:path'), crypto = require('node:crypto');
const archive = process.argv[2];
if (process.env.ELECTRON_RUN_AS_NODE !== '1' || process.versions.electron !== '44.2.0' || process.versions.node !== '24.20.0' ||
  crypto.createHash('sha256').update(fs.readFileSync(archive)).digest('hex') !== '5a4f4e414bd06fc248cfe2ad30e7d9ee3b110968db888fdb733cd57f78a6b294') throw Error('BUILD_NOT_VERIFIED');
Object.defineProperty(process, 'type', { value: 'browser', configurable: true });
const { DATA } = require(path.join(archive, 'runtime/src/engine.js'));
const { createProbabilityModel } = require('../src/probability-model.cjs');
console.log(JSON.stringify(Object.fromEntries(Object.values(createProbabilityModel().cowPools).flat().map(key => [key, DATA.gear[key]?.name ?? null]))));
