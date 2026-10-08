'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const crypto = require('node:crypto');
const { createTestServer } = require('../src/materials-bridge.cjs');
async function fixture(t, options = {}) {
  const calls = [], backups = [];
  const server = createTestServer({ token: 'test-only-token',
    invoke: async (operation, args) => { calls.push(operation); return { result: operation === 'prepare' ? { beforeSave: 'fixture-before' } : { value: args?.targetValue ?? 20 } }; },
    backup: (id, raw) => backups.push({ id, raw }), flush: () => calls.push('flush'), ...options });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const port = server.address().port;
  t.after(() => new Promise(resolve => { server.close(resolve); server.closeAllConnections(); }));
  function request(method = 'GET', body, headers = {}, url = '/read') {
    return new Promise((resolve, reject) => {
      const raw = body === undefined ? undefined : JSON.stringify(body);
      const req = http.request({ hostname: '127.0.0.1', port, method, path: url,
        headers: { Authorization: 'Bearer test-only-token', ...(raw === undefined ? {} : { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(raw) }), ...headers } }, res => {
        let data = ''; res.on('data', chunk => data += chunk); res.on('end', () => resolve({ status: res.statusCode, body: JSON.parse(data) }));
      });
      req.on('error', reject); req.end(raw);
    });
  }
  const input = () => ({ expectedInstance: 'session:1', expectedValue: 20, targetValue: 21, requestId: crypto.randomUUID() });
  return { server, port, calls, backups, request, input };
}
test('authentication, Origin and Host fail closed before touching the game', async t => {
  const f = await fixture(t);
  for (const headers of [{ Authorization: 'Bearer wrong' }, { Origin: 'http://example.com' }, { Origin: '' }, { Host: 'localhost' }]) assert.equal((await f.request('GET', undefined, headers)).status, 403);
  assert.deepEqual(f.calls, []);
});
test('read does not save; write backs up before commit; IDs cannot replay', async t => {
  const f = await fixture(t);
  assert.equal((await f.request()).body.value, 20);
  assert.deepEqual(f.calls, ['read']);
  const input = f.input();
  assert.equal((await f.request('POST', input, {}, '/write')).status, 200);
  assert.equal(f.backups[0].raw, 'fixture-before');
  assert.deepEqual(f.calls, ['read', 'prepare', 'commit', 'flush']);
  assert.equal((await f.request('POST', input, {}, '/write')).body.error, 'REQUEST_ALREADY_USED');
});
test('unknown endpoints, extra fields and bounds do not reach invoke', async t => {
  const f = await fixture(t);
  assert.equal((await f.request('GET', undefined, {}, '/eval')).status, 404);
  for (const body of [{ ...f.input(), field: 'gold' }, { ...f.input(), targetValue: 1000000001 }, { ...f.input(), requestId: '-'.repeat(36) }]) assert.equal((await f.request('POST', body, {}, '/write')).status, 400);
  assert.equal((await f.request('POST', f.input(), { 'Content-Type': 'text/plain' }, '/write')).status, 400);
  assert.deepEqual(f.calls, []);
});
test('same quantity creates no backup and no commit', async t => {
  const f = await fixture(t, { invoke: async () => ({ result: { value: 20, unchanged: true } }) });
  assert.equal((await f.request('POST', f.input(), {}, '/write')).status, 200);
  assert.equal(f.backups.length, 0);
  assert.deepEqual(f.calls, []);
});
test('concurrent requests cannot both reserve after body reads yield', async t => {
  let release, prepares = 0;
  const gate = new Promise(resolve => release = resolve);
  const f = await fixture(t, { invoke: async method => { if (method === 'prepare') { prepares++; await gate; return { result: { beforeSave: 'before' } }; } return { result: { value: 21 } }; } });
  const first = f.request('POST', f.input(), {}, '/write');
  while (!prepares) await new Promise(resolve => setImmediate(resolve));
  assert.equal((await f.request('POST', f.input(), {}, '/write')).body.error, 'BUSY');
  release();
  assert.equal((await first).status, 200);
  assert.equal(prepares, 1);
});
test('two incomplete request bodies must recheck the lock before reservation', async t => {
  let release, prepares = 0;
  const gate = new Promise(resolve => release = resolve);
  const f = await fixture(t, { invoke: async method => { if (method === 'prepare') { prepares++; await gate; return { result: { beforeSave: 'before' } }; } return { result: { value: 21 } }; } });
  let received = 0, ready;
  const bothBodiesStarted = new Promise(resolve => ready = resolve);
  f.server.on('request', () => { if (++received === 2) ready(); });
  function partialRequest() {
    const raw = JSON.stringify(f.input());
    let req;
    const result = new Promise((resolve, reject) => {
      req = http.request({ hostname: '127.0.0.1', port: f.port, method: 'POST', path: '/write', headers: { Authorization: 'Bearer test-only-token', 'Content-Type': 'application/json' } }, res => {
        let body = ''; res.on('data', chunk => body += chunk); res.on('end', () => resolve({ status: res.statusCode, body: JSON.parse(body) }));
      });
      req.on('error', reject); req.write(raw.slice(0, 10));
    });
    return { result, end: () => req.end(raw.slice(10)) };
  }
  const first = partialRequest(), second = partialRequest();
  await bothBodiesStarted;
  first.end(); second.end();
  const early = await Promise.race([first.result, second.result]);
  assert.equal(early.body.error, 'BUSY');
  release();
  const outcomes = await Promise.all([first.result, second.result]);
  assert.equal(outcomes.filter(r => r.status === 200).length, 1);
  assert.equal(prepares, 1);
});
test('late preparation after timeout cannot commit and disables further writes', async t => {
  let release, commits = 0;
  const gate = new Promise(resolve => release = resolve);
  const f = await fixture(t, { operationTimeout: 25, invoke: async method => { if (method === 'prepare') await gate; else commits++; return { result: { beforeSave: 'before' } }; } });
  assert.equal((await f.request('POST', f.input(), {}, '/write')).body.error, 'OPERATION_TIMEOUT_DO_NOT_RETRY');
  release(); await new Promise(resolve => setImmediate(resolve));
  assert.equal((await f.request('POST', f.input(), {}, '/write')).body.error, 'BRIDGE_DISABLED');
  assert.equal(commits, 0);
  assert.equal(f.backups.length, 0);
});
test('failed or ambiguous saves disable writes but permit diagnosis reads', async t => {
  const f = await fixture(t, { invoke: async method => {
    if (method === 'commit') throw Error('SAVE_FAILED_LIVE_RESTORED');
    return { result: method === 'prepare' ? { beforeSave: 'before' } : { value: 20 } };
  } });
  assert.equal((await f.request('POST', f.input(), {}, '/write')).body.error, 'SAVE_FAILED_LIVE_RESTORED');
  assert.equal((await f.request('POST', f.input(), {}, '/write')).body.error, 'BRIDGE_DISABLED');
  assert.equal((await f.request()).body.value, 20);
});
test('failure to back up prevents committing', async t => {
  let commits = 0;
  const f = await fixture(t, { backup: () => { throw Error('private backup unavailable'); }, invoke: async method => { if (method === 'commit') commits++; return { result: { beforeSave: 'before' } }; } });
  assert.equal((await f.request('POST', f.input(), {}, '/write')).status, 409);
  assert.equal(commits, 0);
});
test('read timeout is bounded and never starts a write', async t => {
  let release;
  const gate = new Promise(resolve => release = resolve);
  const f = await fixture(t, { operationTimeout: 25, invoke: async () => { await gate; return { result: { value: 20 } }; } });
  assert.equal((await f.request()).body.error, 'READ_TIMEOUT');
  release();
});
