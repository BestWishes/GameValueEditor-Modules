'use strict';
// Read-only contract inspection in a matching, headless Electron RunAsNode.
// Only pure Engine/config modules. Never import main/preload or open a profile.
const path = require('node:path');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const archive = process.argv[2];
assert.equal(process.env.ELECTRON_RUN_AS_NODE, '1');
assert.equal(process.versions.electron, '44.2.0');
assert.equal(process.versions.node, '24.20.0');
assert.equal(process.arch, 'x64');
Object.defineProperty(process, 'type', { value: 'browser', configurable: true });
const { Engine, DATA } = require(path.join(archive, 'runtime/src/engine.js'));
const supplies = require(path.join(archive, 'runtime/src/crafting-supplies.js'));
const enhance = require(path.join(archive, 'runtime/src/enhance-gems.js'));
const dungeons = require(path.join(archive, 'runtime/src/three-dungeons-data.js'));
const lateDungeons = require(path.join(archive, 'runtime/src/three-late-dungeons-data.js'));
const raids = require(path.join(archive, 'runtime/src/three-raid-data.js'));
const engine = new Engine(null, () => 0.5);
const save = JSON.parse(engine.serialize());
console.log(JSON.stringify({
  defaultMaterials: save.materials, stateMaterials: engine.state.materials,
  ores: save.ores, gems: save.gems, dataKeys: Object.keys(DATA),
  dataMaterialRelated: Object.fromEntries(Object.entries(DATA).filter(([key]) => /ore|gem|material|ticket/i.test(key))),
  synthesisDefinitions: supplies.SYNTHESIS_SCROLLS,
  enhanceDefinitions: enhance.ENHANCE_GEMS,
  dungeonTickets: dungeons.TICKETS, lateDungeonTickets: lateDungeons.TICKETS,
  raidTickets: raids.TICKETS,
}, null, 2));
const initialized = { ...engine.state.materials };
supplies.initCraftingMaterials({ materials: initialized });
console.log(JSON.stringify({ initializedMaterials: initialized, craftingValid: supplies.validCraftingMaterials(initialized) }));
if (process.argv[3]) {
  // Previously preserved fixture only, never the current profile or database.
  const raw = fs.readFileSync(process.argv[3], 'utf8');
  const fixture = new Engine(raw, () => 0.5);
  const baseline = JSON.parse(fixture.serialize());
  const paths = Object.keys(baseline.materials).map(key => ['materials', key]);
  paths.push(['tickets'], ['abyss', 'tickets']);
  const probe = (parts, value) => {
    const input = structuredClone(baseline);
    let parent = input;
    for (const key of parts.slice(0, -1)) parent = parent[key];
    parent[parts.at(-1)] = value;
    try {
      const restored = JSON.parse(new Engine(JSON.stringify(input), () => 0.5).serialize());
      let quantity = restored;
      for (const key of parts) quantity = quantity?.[key];
      return quantity === value;
    } catch { return false; }
  };
  console.log(JSON.stringify({ fixtureMaterialKeys: Object.keys(baseline.materials),
    roundtripProbes: paths.map(parts => ({ path: parts.join('.'),
      accepted: [0, 1, 1000000000, 1000000001, -1, 1.5].map(value => [value, probe(parts, value)]) })) }, null, 2));
}
