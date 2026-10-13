'use strict';

// Build-time wrapper shared by all fixed module requests. No automatic retry.
function withMutationOutcome(expression, requestName, mutating) {
  if (!mutating) return expression;
  return `(async function () {
    let mutationDispatched = false;
    const beginMutation = () => { mutationDispatched = true; };
    try { return await (${expression}); }
    catch (error) {
      if (mutationDispatched && error?.name !== 'GveMutationNotStarted') throw error;
      const code = String(error?.message ?? '');
      return { protocol: 'gve-mutation-outcome-v1', outcome: 'not-executed',
        requestName: ${JSON.stringify(requestName)},
        reason: code.trim() === code && /^[A-Z][A-Z_]{0,80}$/.test(code) ? code : 'MUTATION_REJECTED_BEFORE_WRITE' };
    }
  })()`;
}

module.exports = { withMutationOutcome };
