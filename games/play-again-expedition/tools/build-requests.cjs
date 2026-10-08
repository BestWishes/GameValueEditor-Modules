'use strict';
// Build-time only: package contains no Node runtime, executable helpers or saves.
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { buildMaterialsRequest } = require('../../../experiments/play-again-expedition/src/all-materials-controller.cjs');
const { buildProbabilityRequest } = require('../../../experiments/play-again-expedition/src/probability-controller.cjs');
const config = { processId: 1, executablePath: 'fixed-template-exe', archivePath: 'fixed-template-asar', profilePath: 'fixed-template-profile' };
const scope = 'a'.repeat(64);
const buildWheelRequest = (context, operation, input) => buildProbabilityRequest(context, operation, input, 'wheel');
const buildDropsRequest = (context, operation, input) => buildProbabilityRequest(context, operation, input, 'drops');
const samples = {
  'materials-list': [buildMaterialsRequest, 'list', {}],
  'materials-set': [buildMaterialsRequest, 'set', { materialId: 'protectionScrolls', expectedValue: 2, targetValue: 3, slot: 'zseb-expedition-v1', journeyMode: 'guardian', scopeHash: scope }],
  'wheel-inspect': [buildWheelRequest, 'inspect', {}],
  'wheel-configure': [buildWheelRequest, 'configure', { scopeHash: scope, revision: 0, profile: { wheel: { groupPercent: { ordinary: 92.5, immortal: 5, mythic: 1, protection: 1, sacredProtection: 0.5 } }, wheelBonus: { doublePercent: 2, marqueePercent: 1 } } }],
  'wheel-reset': [buildWheelRequest, 'reset', { scopeHash: scope, revision: 0 }],
  'drops-inspect': [buildDropsRequest, 'inspect', {}],
  'drops-configure': [buildDropsRequest, 'configure', { scopeHash: scope, revision: 0, profile: { drops: { guardianTicketPercent: 50 } } }],
  'drops-reset': [buildDropsRequest, 'reset', { scopeHash: scope, revision: 0 }],
};
function buildTemplates() {
  const templates = {};
  for (const [name, [builder, operation, input]] of Object.entries(samples)) {
    const request = builder(config, operation, input);
    new vm.Script(request.params.expression);
    const prefix = `const config = ${JSON.stringify(config)}, operation = ${JSON.stringify(operation)}, input = ${JSON.stringify(input)};`;
    if (request.params.expression.split(prefix).length !== 2) throw Error('Template prefix is not unique');
    request.params.expression = request.params.expression.replace(prefix, `const config = __GVE_CONFIG_JSON__, operation = ${JSON.stringify(operation)}, input = __GVE_INPUT_JSON__;`);
    templates[name] = request;
  }
  return templates;
}
if (require.main === module) {
  const output = path.resolve(process.argv[2]);
  fs.mkdirSync(path.dirname(output), { recursive: true });
  fs.writeFileSync(output, JSON.stringify(buildTemplates()), 'utf8');
  console.log('Embedded eight fixed requests; no game/profile access.');
}
module.exports = { buildTemplates, config, samples };
