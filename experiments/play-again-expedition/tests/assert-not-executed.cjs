'use strict';
const assert = require('node:assert/strict');
module.exports = async function assertNotExecuted(request, reason) {
  const result = await request;
  assert.equal(result.protocol, 'gve-mutation-outcome-v1');
  assert.equal(result.outcome, 'not-executed');
  assert.match(result.requestName, /^(?:materials|wheel|drops|probability|fate|equipment)-(?:set|configure|reset)$/);
  assert.equal(Object.keys(result).length, 4);
  assert.match(result.reason, reason ?? /^[A-Z][A-Z_]{0,80}$/);
  return result;
};
