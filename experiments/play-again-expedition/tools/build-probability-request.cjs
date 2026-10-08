'use strict';
const { buildProbabilityRequest } = require('../src/probability-controller.cjs');
const vm = require('node:vm');
const [operation, config, input = '{}'] = process.argv.slice(2);
const request = buildProbabilityRequest(JSON.parse(config), operation, JSON.parse(input));
new vm.Script(request.params.expression); // Syntax only. Never execute here.
console.log(JSON.stringify(request));
