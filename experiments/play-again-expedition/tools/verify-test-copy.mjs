import fs from 'node:fs';
import crypto from 'node:crypto';
import path from 'node:path';
import assert from 'node:assert/strict';
const [runtime] = process.argv.slice(2);
const expectedDirectory = 'D:/MyOtherProjects/GameValueEditor-Modules/artifacts/play-again-expedition/20261007-protection-scroll-test/runtime';
assert.equal(path.resolve(runtime).toLowerCase(), path.resolve(expectedDirectory).toLowerCase());
function archive(filename) {
  const bytes = fs.readFileSync(filename);
  const size = bytes.readUInt32LE(4), length = bytes.readUInt32LE(12);
  return { bytes, header: JSON.parse(bytes.subarray(16, 16 + length)), data: bytes.subarray(8 + size) };
}
const hash = bytes => crypto.createHash('sha256').update(bytes).digest('hex').toUpperCase();
const resources = path.join(runtime, 'resources');
const before = archive(path.join(resources, 'app.asar.original'));
const after = archive(path.join(resources, 'app.asar'));
assert.equal(hash(before.bytes), '5E5394C50B67A71CF0965617F850A6DA91484D5BE2D5FEA5A058E1F8D7C68ECB');
assert.ok(after.data.subarray(0, before.data.length).equals(before.data));
const changedMain = after.header.files['main.cjs'];
const originalMain = before.header.files['main.cjs'];
const text = after.data.subarray(Number(changedMain.offset), Number(changedMain.offset) + changedMain.size).toString('utf8');
assert.ok(text.startsWith(before.data.subarray(Number(originalMain.offset), Number(originalMain.offset) + originalMain.size).toString('utf8')));
assert.ok(text.endsWith("require(require('node:path').join(process.resourcesPath,'materials-bridge.cjs')).start();\n"));
after.header.files['main.cjs'] = originalMain;
assert.deepEqual(after.header, before.header);
const record = JSON.parse(fs.readFileSync(path.join(resources, 'gve-patch-record.json')));
assert.equal(record.patchedSha256, hash(after.bytes));
for (const source of ['materials-bridge.cjs', 'material-transaction.cjs']) {
  const sourcePath = path.resolve(import.meta.dirname, '..', 'src', source);
  assert.equal(hash(fs.readFileSync(path.join(resources, source))), hash(fs.readFileSync(sourcePath)));
}
assert.equal(hash(fs.readFileSync(path.join(runtime, 'ZsebExpedition.exe'))), '9A7BA0AF1AC230325A5EB7BAFDBB8B1794C6D61EBA7A805DD5DD38786D267E7C');
console.log(JSON.stringify({ result: 'PASS', originalArchivePreserved: true, onlyMainLoaderChanged: true, sourceCopiesMatch: true, bytecodeDataUnchanged: true }));
