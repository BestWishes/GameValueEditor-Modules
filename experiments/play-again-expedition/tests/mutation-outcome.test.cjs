'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const { withMutationOutcome } = require('../src/mutation-outcome.cjs');
const assertNotExecuted = require('./assert-not-executed.cjs');
const run = (source, name = 'materials-set', mutating = true) => vm.runInNewContext(withMutationOutcome(source, name, mutating));

test('pre-dispatch rejection has a bounded versioned receipt, not a success', async () => {
  const receipt = await assertNotExecuted(run('(async()=>{throw Error("READ_RECEIPT_SCOPE_CHANGED")})()'), /READ_RECEIPT_SCOPE_CHANGED/);
  assert.equal(receipt.requestName, 'materials-set');
});
test('only codes, not native exception paths or arbitrary text, leave the renderer', async () => {
  const receipt = await assertNotExecuted(run('(async()=>{throw Error("secret/path and private save")})()'));
  assert.equal(receipt.reason, 'MUTATION_REJECTED_BEFORE_WRITE');
  assert.doesNotMatch(JSON.stringify(receipt), /secret|private/);
  assert.equal((await assertNotExecuted(run('(async()=>{throw Error("GAME_BUSY\\n")})()'))).reason, 'MUTATION_REJECTED_BEFORE_WRITE');
});
test('post-dispatch ordinary errors remain uncertain even if their code looks like a stale-read guard', async () => {
  await assert.rejects(run('(async()=>{beginMutation();throw Error("READ_RECEIPT_SCOPE_CHANGED")})()'), /READ_RECEIPT_SCOPE_CHANGED/);
});
test('an explicit renderer pre-write rejection can cross the dispatch boundary', async () => {
  await assertNotExecuted(run('(async()=>{beginMutation();const e=Error("MATERIAL_VALUE_CHANGED");e.name="GveMutationNotStarted";throw e;})()'));
});
test('readonly requests do not interpret mutation markers or install outcome wrappers', async () => {
  const source = '(async()=>{const e=Error("GAME_NOT_ENTERED");e.name="GveMutationNotStarted";throw e;})()';
  assert.equal(withMutationOutcome(source, 'materials-list', false), source);
  await assert.rejects(run(source, 'materials-list', false), /GAME_NOT_ENTERED/);
});
test('confirmed and unconfirmed business results are passed through unchanged and sent once', async () => {
  const context = { sends: 0 };
  const result = await vm.runInNewContext(withMutationOutcome('(async()=>{beginMutation();sends++;return {material:{status:"uncertain-after-save"}};})()', 'materials-set', true), context);
  assert.equal(result.material.status, 'uncertain-after-save'); assert.equal(context.sends, 1);
});
