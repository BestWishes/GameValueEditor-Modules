'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const { buildTemplates, samples } = require('../tools/build-requests.cjs');
const templates = buildTemplates();
test('package exposes exactly material read/write and two isolated probability namespaces', () => {
  assert.deepEqual(Object.keys(templates), ['materials-list', 'materials-set', 'wheel-inspect', 'wheel-configure', 'wheel-reset', 'drops-inspect', 'drops-configure', 'drops-reset']);
});
for (const [name, [builder, operation, input]] of Object.entries(samples)) {
  test(name + ' embeds the verified backend exactly, including formal bundled callsites', () => {
    const context = { processId: 100, executablePath: 'D:\\游戏\\原版.exe', archivePath: 'D:\\游戏\\resources\\app.asar', profilePath: 'C:\\用户\\原资料' };
    const expected = builder(context, operation, input);
    const actual = JSON.parse(JSON.stringify(templates[name]));
    actual.params.expression = actual.params.expression.replace('__GVE_CONFIG_JSON__', JSON.stringify(context)).replace('__GVE_INPUT_JSON__', JSON.stringify(input));
    assert.deepEqual(actual, expected);
    new vm.Script(actual.params.expression);
    assert.equal(actual.method, 'Runtime.evaluate');
    assert.equal(actual.params.timeout, 5000);
    assert.doesNotMatch(actual.params.expression, /_debugProcess|inspector\.close\s*\(|process\.kill\s*\(/);
    assert.doesNotMatch(actual.params.expression, /3116971|3117159|formal-verified-0\.116\.70|versions\.electron !== '44\.2\.0'/);
    if (name.startsWith('wheel-')) { assert.match(actual.params.expression, /discoverProbabilitySites/); assert.match(actual.params.expression, /const namespace = "wheel"/); }
    if (name.startsWith('drops-')) assert.match(actual.params.expression, /const namespace = "drops"/);
  });
}
