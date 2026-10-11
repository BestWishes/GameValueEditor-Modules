// Intentionally non-strict: current native chooseWheelIndex exposes its actual
// local board through caller arguments. This FIXED function is compiled as a
// non-strict literal, not with eval/Function (the game forbids unsafe-eval).
function createWheelRngBridge(dispatch) {
  return function wheelAwareRng() {
    let board;
    try {
      const consumer = wheelAwareRng.caller;
      if (consumer && consumer.name === 'chooseWheelIndex') {
        const args = consumer.arguments;
        if (args && args[1] === wheelAwareRng) board = args[0];
      }
    } catch (_) { /* A strict consumer has no accessible caller arguments. */ }
    return Reflect.apply(dispatch, this, [board, arguments]);
  };
}
module.exports = { createWheelRngBridge };
