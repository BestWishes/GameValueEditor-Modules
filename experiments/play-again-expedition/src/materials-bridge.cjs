'use strict';
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const crypto = require('node:crypto');
const { materialTransaction } = require('./material-transaction.cjs');

const ORIGINAL_ASAR_HASH = '5E5394C50B67A71CF0965617F850A6DA91484D5BE2D5FEA5A058E1F8D7C68ECB';
const EXE_HASH = '9A7BA0AF1AC230325A5EB7BAFDBB8B1794C6D61EBA7A805DD5DD38786D267E7C';
function hashFile(filename) { return crypto.createHash('sha256').update(fs.readFileSync(filename)).digest('hex').toUpperCase(); }
const safeError = error => {
  const allowed = ['GAME_NOT_READY','ENTER_EXISTING_SAVE','CONTRACT_CHANGED','INVALID_CURRENT_VALUE','SAVE_INSTANCE_CHANGED','VALUE_CHANGED','INVALID_TARGET_VALUE','INVALID_SERIALIZATION','SAVE_CONTRACT_CHANGED','SAVE_KEY_AMBIGUOUS','MODE_NOT_SUPPORTED','UNKNOWN_OPERATION','SNAPSHOT_CHANGED','SERIALIZATION_MISMATCH','UNRELATED_FIELDS_CHANGED','SAVE_FAILED_LIVE_RESTORED','SAVE_RESULT_UNCERTAIN','PERSISTENCE_UNCONFIRMED','REQUEST_INVALID','BRIDGE_DISABLED','BUSY'];
  return allowed.find(code => String(error?.message ?? '').includes(code)) ?? 'BRIDGE_OPERATION_FAILED';
};
function createTestServer({ token, invoke, backup, flush, operationTimeout = 8000 }) {
  let active = false, disabled = false;
  const requests = new Map();
  const send = (response, status, payload) => {
    if (response.writableEnded || response.destroyed) return;
    response.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' });
    response.end(JSON.stringify(payload));
  };
  const server = http.createServer(async (request, response) => {
    const address = server.address();
    if (request.socket.remoteAddress !== '127.0.0.1' || request.headers.host !== `127.0.0.1:${address.port}` || Object.hasOwn(request.headers, 'origin') || request.headers.authorization !== `Bearer ${token}`) return send(response, 403, { error: 'FORBIDDEN' });
    if (request.method === 'GET' && request.url === '/read') {
      const timeout = setTimeout(() => send(response, 504, { error: 'READ_TIMEOUT' }), operationTimeout);
      try { return send(response, 200, (await invoke('read')).result); }
      catch (error) { return send(response, 409, { error: safeError(error) }); }
      finally { clearTimeout(timeout); }
    }
    if (request.method !== 'POST' || request.url !== '/write') return send(response, 404, { error: 'NOT_FOUND' });
    if (request.headers['content-type'] !== 'application/json' || Number(request.headers['content-length'] || 0) > 4096) return send(response, 400, { error: 'REQUEST_INVALID' });
    if (disabled) return send(response, 409, { error: 'BRIDGE_DISABLED' });
    if (active) return send(response, 409, { error: 'BUSY' });
    let body = '';
    request.setTimeout(5000, () => request.destroy());
    try {
      for await (const chunk of request) {
        body += chunk.toString('utf8');
        if (Buffer.byteLength(body) > 4096) throw Error('REQUEST_INVALID');
      }
      let input;
      try { input = JSON.parse(body); } catch { throw Error('REQUEST_INVALID'); }
      if (!input || Object.keys(input).sort().join(',') !== 'expectedInstance,expectedValue,requestId,targetValue' || !/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/.test(input.requestId) || typeof input.expectedInstance !== 'string' || input.expectedInstance.length > 80 || !Number.isSafeInteger(input.expectedValue) || !Number.isSafeInteger(input.targetValue) || input.expectedValue < 0 || input.expectedValue > 1000000000 || input.targetValue < 0 || input.targetValue > 1000000000) throw Error('REQUEST_INVALID');
      // Body reads yield: reserve only after rechecking both guards atomically.
      if (disabled) return send(response, 409, { error: 'BRIDGE_DISABLED' });
      if (active) return send(response, 409, { error: 'BUSY' });
      if (requests.has(input.requestId)) return send(response, 409, { error: 'REQUEST_ALREADY_USED' });
      if (requests.size >= 100) return send(response, 409, { error: 'RESTART_TEST_INTERFACE' });
      active = true;
      requests.set(input.requestId, 'started');
      const timeout = setTimeout(() => { disabled = true; send(response, 504, { error: 'OPERATION_TIMEOUT_DO_NOT_RETRY' }); }, operationTimeout);
      try {
        const prepared = (await invoke('prepare', input)).result;
        if (disabled) throw Error('BRIDGE_DISABLED');
        if (prepared.unchanged) return send(response, 200, prepared);
        backup(input.requestId, prepared.beforeSave);
        const completed = await invoke('commit', { ...input, beforeSave: prepared.beforeSave });
        flush(completed);
        requests.set(input.requestId, 'completed');
        send(response, 200, completed.result);
      } catch (error) {
        const code = safeError(error);
        if (['SAVE_FAILED_LIVE_RESTORED','SAVE_RESULT_UNCERTAIN','PERSISTENCE_UNCONFIRMED','BRIDGE_OPERATION_FAILED'].includes(code)) disabled = true;
        requests.set(input.requestId, code);
        send(response, 409, { error: code });
      } finally { clearTimeout(timeout); active = false; }
    } catch (error) { send(response, 400, { error: safeError(error) }); }
  });
  server.maxConnections = 8;
  server.headersTimeout = 5000;
  server.requestTimeout = 5000;
  return server;
}
function start() {
  if (process.env.GVE_PROTECTION_SCROLL_TEST !== '1') return;
  const { app, BrowserWindow } = require('electron');
  if (process.type !== 'browser' || process.versions.electron !== '44.2.0' || process.arch !== 'x64') throw Error('Unsupported test runtime');
  const resources = process.resourcesPath;
  const original = path.join(resources, 'app.asar.original');
  const patchRecord = JSON.parse(fs.readFileSync(path.join(resources, 'gve-patch-record.json'), 'utf8'));
  if (hashFile(original) !== ORIGINAL_ASAR_HASH || hashFile(process.execPath) !== EXE_HASH || hashFile(path.join(resources, 'app.asar')) !== patchRecord.patchedSha256) throw Error('Test build fingerprint mismatch');
  const nonce = crypto.randomUUID();
  const token = crypto.randomBytes(32).toString('hex');
  let server, descriptor;
  const invoke = async (operation, argumentsObject) => {
    const windows = BrowserWindow.getAllWindows().filter(win => !win.isDestroyed() && /^expedition:\/\/game(?:\/|$)/.test(win.webContents.getURL()));
    if (windows.length !== 1) throw Error('GAME_NOT_READY');
    const contents = windows[0].webContents;
    const code = `(()=>{try{return (${materialTransaction.toString()})(${JSON.stringify(operation)},${JSON.stringify(argumentsObject ?? null)},${JSON.stringify(nonce)})}catch(error){return {gveError:String(error?.message || 'BRIDGE_OPERATION_FAILED')}}})()`;
    const result = await contents.executeJavaScriptInIsolatedWorld(999, [{ code }], false);
    if (!result || typeof result !== 'object') throw Error('CONTRACT_CHANGED');
    if (result.gveError) throw Error(result.gveError);
    return { contents, result };
  };
  app.whenReady().then(() => {
    const expectedProfile = path.resolve(path.dirname(process.execPath), '..', 'profile-test');
    if (path.resolve(app.getPath('userData')).toLowerCase() !== expectedProfile.toLowerCase()) throw Error('Test must use the isolated profile copy');
    const privateDirectory = path.join(app.getPath('userData'), 'gve-protection-scroll-test');
    fs.mkdirSync(privateDirectory, { recursive: true });
    const backupDirectory = path.join(privateDirectory, 'backups', nonce);
    fs.mkdirSync(backupDirectory, { recursive: true });
    descriptor = path.join(privateDirectory, 'connection.json');
    server = createTestServer({
      token, invoke,
      backup: (requestId, raw) => fs.writeFileSync(path.join(backupDirectory, requestId + '.json'), raw, { flag: 'wx', mode: 0o600 }),
      flush: completed => completed.contents.session.flushStorageData()
    });
    server.listen(0, '127.0.0.1', () => {
      const connection = { protocol: 1, pid: process.pid, exePath: process.execPath, session: nonce, port: server.address().port, token, gameVersion: '0.116.68', originalAsarSha256: ORIGINAL_ASAR_HASH };
      fs.writeFileSync(descriptor, JSON.stringify(connection), { mode: 0o600 });
    });
    server.on('error', () => { server.close(); });
  }).catch(error => { console.error('GVE test bridge failed: ' + safeError(error)); });
  app.on('will-quit', () => {
    if (server) server.close();
    try {
      if (descriptor && JSON.parse(fs.readFileSync(descriptor, 'utf8')).session === nonce) fs.unlinkSync(descriptor);
    } catch { /* A stale descriptor is rejected by the client process check. */ }
  });
}
module.exports = { start, createTestServer };
