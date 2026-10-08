'use strict';

// Fixed standalone factory, executed only in the original isolated world 999.
// No imported game code, globals, arbitrary paths or heap addresses.
function createMaterialsRuntime() {
  const definitions = [
    ['stones', '强化石', '强化'], ['immortalShards', '不朽碎片', '碎片'],
    ['adsorptionStones', '吸附石', '合成'], ['synthesisProtectionScrolls', '合成保护卷', '合成'],
    ['advancedRecruitTickets', '高级招募卷', '票券'], ['costumeTickets', '时装券', '票券'],
    ['flameKeys', '火焰王座钥匙', '门票'], ['cowTickets', '奶牛关入场券', '门票'],
    ['tkRicePass', '五斗米道通行证', '通行证'], ['tkHeishanPass', '黑山通行证', '通行证'],
    ['tkLiangzhouPass', '凉州通行证', '通行证'], ['tkChangshaPass', '长沙通行证', '通行证'],
    ['tkJiaozhouPass', '交州通行证', '通行证'], ['tkLiaodongPass', '辽东通行证', '通行证'],
    ['tkShuhanPass', '蜀汉通行证', '通行证'], ['tkDongwuPass', '东吴通行证', '通行证'],
    ['tkSoulPass', '魂冢通行证', '通行证'], ['tkHulaoPass', '虎牢关通行证', '通行证'],
    ['tkTongquePass', '铜雀台通行证', '通行证'], ['tkHuarongPass', '华容道通行证', '通行证'],
    ['mythicGems', '神话宝石', '宝石'], ['protectionScrolls', '装备保护卷', '强化'],
    ['sacredProtectionScrolls', '神圣装备保护卷', '强化'], ['fusion', '合成石', '合成'],
    ['soulGems', '灵魂宝石', '强化', 9999], ['creationGems', '创造宝石', '强化', 9999],
    ['wheelTickets', '转盘币', '票券'],
  ].map(([id, name, category, maximum = 1000000000]) => ({ id, name, category, maximum, minimum: 0, path: ['materials', id] }));
  definitions.push(
    { id: 'recruitTickets', name: '招募券', category: '票券', minimum: 0, maximum: 1000000000, path: ['tickets'] },
    { id: 'abyssTickets', name: '深渊挑战券', category: '门票', minimum: 0, maximum: 1000000000, path: ['abyss', 'tickets'] },
  );
  const fail = code => { throw Error(code); };
  const ownData = (object, key) => {
    if (!object || typeof object !== 'object' || Array.isArray(object)) return null;
    const descriptor = Object.getOwnPropertyDescriptor(object, key);
    return descriptor && Object.hasOwn(descriptor, 'value') ? descriptor : null;
  };
  const locate = (root, definition) => {
    let parent = root;
    for (const key of definition.path.slice(0, -1)) {
      const descriptor = ownData(parent, key);
      if (!descriptor) return null;
      parent = descriptor.value;
    }
    const key = definition.path.at(-1), descriptor = ownData(parent, key);
    return descriptor ? { parent, key, descriptor, value: descriptor.value } : null;
  };
  const valid = (value, definition) => Number.isSafeInteger(value) && value >= definition.minimum && value <= definition.maximum;
  const parseSave = raw => {
    if (typeof raw !== 'string' || !raw.length || raw.length > 4 * 1024 * 1024) fail('SAVE_INVALID');
    const save = JSON.parse(raw);
    if (!Number.isSafeInteger(save?.version) || save.version <= 0 || !save.materials || Array.isArray(save.materials) || typeof save.materials !== 'object') fail('SAVE_FORMAT_CHANGED');
    if (typeof save.rewardClockId !== 'string' || !save.rewardClockId.length || save.rewardClockId.length > 256 ||
      !Array.isArray(save.inventory) || save.inventory.some(item => !Number.isSafeInteger(item?.uid)) ||
      !save.characters || typeof save.characters !== 'object' || Array.isArray(save.characters) ||
      !Array.isArray(save.owned) || save.owned.some(id => !Number.isSafeInteger(id))) fail('SAVE_IDENTITY_INVALID');
    return save;
  };
  const mainSaves = () => {
    if (localStorage.length > 128) fail('STORAGE_CONTRACT_CHANGED');
    const saves = [];
    for (let index = 0; index < localStorage.length; index++) {
      const key = localStorage.key(index);
      if (/^zseb-expedition-v1(?:-slot-[1-4])?$/.test(key)) saves.push({ key, raw: localStorage.getItem(key) });
    }
    return saves.sort((a, b) => a.key.localeCompare(b.key));
  };
  const anchorOf = (save, slot, journey) => JSON.stringify([
    'expedition', slot, journey, save.rewardClockId,
    save.inventory.map(item => item.uid).sort((a, b) => a - b),
    Object.keys(save.characters).sort(), [...save.owned].sort((a, b) => a - b),
  ]);
  return function materialsRuntime(operation, input = {}) {
    if (!['catalog', 'list', 'set'].includes(operation)) fail('UNKNOWN_MATERIAL_OPERATION');
    if (!input || typeof input !== 'object' || Array.isArray(input)) fail('INVALID_MATERIAL_INPUT');
    const allowedKeys = operation === 'set' ? ['materialId', 'expectedValue', 'targetValue', 'slot', 'journeyMode', 'anchor'] : [];
    if (Object.keys(input).some(key => !allowedKeys.includes(key))) fail('UNKNOWN_MATERIAL_ARGUMENT');
    if (operation === 'catalog') return definitions.map(({ path, ...definition }) => ({ ...definition }));
    const definition = operation === 'set' ? definitions.find(item => item.id === input.materialId) : null;
    if (operation === 'set') {
      if (!definition) fail('MATERIAL_NOT_ALLOWED');
      if (!valid(input.expectedValue, definition) || !valid(input.targetValue, definition)) fail('MATERIAL_RANGE_INVALID');
      if (typeof input.anchor !== 'string' || input.anchor.length > 65536) fail('EXPECTED_IDENTITY_INVALID');
    }
    const game = window.expedition, engine = game?.engine, state = engine?.state, platform = game?.platform;
    if (!game || typeof game.getUi !== 'function') fail('GAME_NOT_READY');
    const ui = game.getUi();
    if (ui?.entered !== true) fail('GAME_NOT_ENTERED');
    if (ui.gameMode !== 'expedition') fail('GAME_MODE_NOT_SUPPORTED');
    // This is an opaque receipt anchor, not a player-facing activity allowlist.
    // Normal stage selection changes it while keeping the same materials/save.
    const activity = ownData(engine, 'mode');
    if (typeof activity?.value !== 'string' || !activity.value.length || activity.value.length > 128) fail('MATERIAL_CONTRACT_CHANGED');
    if (!ownData(state, 'materials') || !state.materials || Array.isArray(state.materials) ||
      typeof platform?.read !== 'function' || typeof platform?.save !== 'function' ||
      typeof engine.serialize !== 'function') fail('MATERIAL_CONTRACT_CHANGED');
    const materials = state.materials, journeyMode = activity.value;
    const wheelIdle = () => {
      const wheel = ownData(state, 'luckyWheel');
      if (!wheel?.value || typeof wheel.value !== 'object' || Array.isArray(wheel.value)) return false;
      const pending = Object.getOwnPropertyDescriptor(wheel.value, 'pending');
      return !pending || (Object.hasOwn(pending, 'value') && !pending.value);
    };
    const storedBefore = platform.read(), stored = parseSave(storedBefore), saves = mainSaves();
    const matches = saves.filter(save => save.raw === storedBefore);
    if (matches.length !== 1) fail('ACTIVE_SAVE_AMBIGUOUS');
    const slot = matches[0].key, anchor = anchorOf(stored, slot, journeyMode);
    const stillCurrent = () => window.expedition === game && game.engine === engine && engine.state === state &&
      state.materials === materials && game.platform === platform && game.getUi()?.entered === true &&
      game.getUi()?.gameMode === 'expedition' && engine.mode === journeyMode;
    const assess = item => {
      const live = locate(state, item), saved = locate(stored, item);
      let reason = null;
      if (!live || !saved) reason = ['MATERIAL_NOT_DEFINED', '当前构建/存档未定义此材料'];
      else if (!valid(live.value, item) || !valid(saved.value, item)) reason = ['MATERIAL_RANGE_INVALID', '数量不符合已验证范围'];
      else if (live.value !== saved.value) reason = ['MATERIAL_SAVE_PENDING', '实时数量和保存数量不同，请正常保存后重新读取'];
      else if (live.descriptor.writable !== true) reason = ['MATERIAL_READ_ONLY', '当前材料只读'];
      else if (platform.storageBlocked === true) reason = ['MATERIAL_SAVE_BLOCKED', '游戏保存受阻，当前不能修改材料'];
      else if (engine.phase === 'fighting') reason = engine.paused === true
        ? ['MATERIAL_BATTLE_PAUSED', '战斗已暂停但尚未结束，请先退出当前战斗再修改材料']
        : ['MATERIAL_BATTLE_ACTIVE', '当前战斗尚未结束，请先停止战斗再修改材料'];
      else if (engine.phase !== 'idle') reason = ['MATERIAL_OPERATION_PENDING', '游戏正在处理当前操作，结束后可修改材料'];
      else if (!wheelIdle()) reason = ['MATERIAL_WHEEL_PENDING', '请先完成大转盘的动画与奖励结算'];
      return { id: item.id, name: item.name, category: item.category, minimum: item.minimum, maximum: item.maximum,
        value: Number.isSafeInteger(live?.value) ? live.value : null,
        storedValue: Number.isSafeInteger(saved?.value) ? saved.value : null, canWrite: reason === null,
        status: reason?.[1] ?? '可修改', reason: reason?.[0] ?? null };
    };
    if (operation === 'list') {
      const rows = definitions.map(assess);
      const unknown = Object.keys(materials).filter(key => !definitions.some(item => item.path[0] === 'materials' && item.id === key));
      if (unknown.length > 128) fail('MATERIAL_SCHEMA_TOO_LARGE');
      for (const key of unknown) {
        const descriptor = ownData(materials, key);
        rows.push({ id: 'unrecognized.' + key, name: key, category: '未适配',
          value: Number.isSafeInteger(descriptor?.value) ? descriptor.value : null,
          canWrite: false, status: '未适配字段，仅显示，不允许修改' });
      }
      if (!stillCurrent() || platform.read() !== storedBefore || JSON.stringify(mainSaves()) !== JSON.stringify(saves) || !stillCurrent()) fail('READ_INSTANCE_CHANGED');
      return { rows, slot, journeyMode, anchor, saveFormat: stored.version, readOnly: true, nativeSaveCalled: false };
    }
    if (input.slot !== slot || input.journeyMode !== journeyMode || input.anchor !== anchor) fail('ACTIVE_SAVE_CHANGED');
    const row = assess(definition);
    if (!row.canWrite) fail(row.reason);
    if (row.value !== input.expectedValue) fail('MATERIAL_VALUE_CHANGED');
    // Do not serialize, save or flush an unchanged quantity.
    if (input.targetValue === input.expectedValue) return { status: 'unchanged', materialId: definition.id,
      value: row.value, previousValue: row.value, slot, journeyMode, nativeSaveCalled: false, retryAllowed: false };
    const leaf = locate(state, definition);
    const beforeSave = engine.serialize(), before = parseSave(beforeSave);
    if (locate(before, definition)?.value !== input.expectedValue || anchorOf(before, slot, journeyMode) !== anchor) fail('LIVE_SNAPSHOT_CHANGED');
    const leafStillCurrent = target => {
      const current = locate(state, definition);
      return stillCurrent() && current?.parent === leaf.parent && current.key === leaf.key &&
        current.descriptor.writable === true && current.value === target;
    };
    const storageStillCurrent = () => platform.read() === storedBefore && JSON.stringify(mainSaves()) === JSON.stringify(saves);
    if (!leafStillCurrent(input.expectedValue) || !storageStillCurrent() || engine.phase !== 'idle' || !wheelIdle() || platform.storageBlocked === true || !leafStillCurrent(input.expectedValue)) fail('CHANGED_BEFORE_ASSIGNMENT');
    let afterSave;
    try {
      leaf.parent[leaf.key] = input.targetValue;
      afterSave = engine.serialize();
      const after = parseSave(afterSave), afterLeaf = locate(after, definition);
      if (afterLeaf?.value !== input.targetValue) fail('MATERIAL_SERIALIZATION_MISMATCH');
      afterLeaf.parent[afterLeaf.key] = input.expectedValue;
      if (JSON.stringify(after) !== JSON.stringify(before)) fail('UNRELATED_SAVE_FIELDS_CHANGED');
      if (!leafStillCurrent(input.targetValue) || !storageStillCurrent() || engine.phase !== 'idle' || !wheelIdle() || platform.storageBlocked === true || !leafStillCurrent(input.targetValue)) fail('CHANGED_BEFORE_SAVE');
    } catch (error) {
      let liveRestored = false;
      if (leafStillCurrent(input.targetValue)) {
        leaf.parent[leaf.key] = input.expectedValue;
        liveRestored = leafStillCurrent(input.expectedValue);
      }
      return { status: 'rejected-before-save', error: String(error.message), materialId: definition.id,
        nativeSaveCalled: false, liveRestored, retryAllowed: false };
    }
    try {
      if (platform.save(afterSave) !== true) fail('SAVE_RESULT_UNCERTAIN');
      const expectedSaves = saves.map(save => ({ key: save.key, raw: save.key === slot ? afterSave : save.raw }));
      if (!leafStillCurrent(input.targetValue) || platform.read() !== afterSave || localStorage.getItem(slot) !== afterSave ||
        JSON.stringify(mainSaves()) !== JSON.stringify(expectedSaves) || engine.serialize() !== afterSave || !stillCurrent()) fail('SAVED_STATE_UNCONFIRMED');
      return { status: 'storage-verified', materialId: definition.id, previousValue: input.expectedValue,
        value: input.targetValue, slot, journeyMode, saveFormat: 38, nativeSaveCalled: true,
        storageVerified: true, onlySerializedMaterialChanged: true, retryAllowed: false, beforeSave, afterSave };
    } catch (error) {
      // Native save may already have persisted; never undo or automatically retry.
      return { status: 'uncertain-after-save', materialId: definition.id, error: String(error.message),
        nativeSaveCalled: true, retryAllowed: false };
    }
  };
}

module.exports = { createMaterialsRuntime };
