'use strict';
const { createMaterialsRuntime } = require('../src/all-materials-runtime.cjs');
const { buildMaterialsRequest } = require('../src/all-materials-controller.cjs');
const vm = require('node:vm');
const [operation, config, input = '{}'] = process.argv.slice(2);
if (operation === 'catalog') console.log(JSON.stringify(createMaterialsRuntime()('catalog')));
else {
  const request = buildMaterialsRequest(JSON.parse(config), operation, JSON.parse(input));
  new vm.Script(request.params.expression); // Syntax only; never evaluate here.
  console.log(JSON.stringify(request));
}
