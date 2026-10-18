const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

function browser() {
  const nodes = new Map(), calls = [];
  const canvas = new Proxy({}, { get: (_, name) => (...args) => calls.push([name, ...args]), set: () => true });
  const document = {
    documentElement: {}, activeElement: null,
    querySelectorAll: () => [],
    getElementById(id) {
      if (!nodes.has(id)) nodes.set(id, {
        innerHTML: '', textContent: '', hidden: false, value: '', clientWidth: 800, clientHeight: 500,
        classList: { toggle() {} }, listeners: {},
        addEventListener(type, fn) { this.listeners[type] = fn; }, getContext: () => canvas,
      });
      return nodes.get(id);
    },
  };
  const context = vm.createContext({ document, console,
    localStorage: { getItem: () => null, setItem() {}, removeItem() {} },
    window: { addEventListener() {}, devicePixelRatio: 1 }, location: { host: '127.0.0.1' },
    getComputedStyle: () => ({ getPropertyValue: () => '#abcdef' }),
    WebSocket: class {}, setInterval() {}, setTimeout() {},
  });
  vm.runInContext(fs.readFileSync(path.join(__dirname, '../src/CubeRadar.Cli/wwwroot/app.js'), 'utf8'), context);
  const update = (players, ownFaction = 'Elyos') => {
    const state = { radar: { players, ownFaction, factionSetting: ownFaction, nowMs: 10000, selfId: 1,
      cubes: [], warnings: [], trail: [], zone: null, mapId: 1, fixFreshSeconds: 5,
      self: { x: 10000, y: 10000, z: 100, timeMs: 10000, ageMs: 0 }, ring: null,
      orientation: { north: '-y', northX: 0, northY: -1, eastX: 1, eastY: 0 } },
      capture: { mode: 'replay', streams: 1, packets: 10 }, wallMs: 10000 };
    context.input = state;
    vm.runInContext('msg = input; onState();', context);
  };
  return { document, calls, context, update };
}

const contact = (extra = {}) => ({ entityId: 11930, name: 'EnemyB', faction: 'Asmodian', isEnemy: true,
  rankCode: 17, rankName: 'Eighth-Class Soldier', rankVerified: true,
  x: 12000, y: 11000, z: 100, distanceM: 22, compass: 'SO', positionStale: false, positionAgeMs: 100, ...extra });

test('enemy list shows verified rank and excludes allies/unclassified players', () => {
  const b = browser();
  b.update([contact(), contact({ name: 'Ally', isEnemy: false }), contact({ name: 'Unknown', isEnemy: null })]);
  const html = b.document.getElementById('enemy-list').innerHTML;
  assert.match(html, /EnemyB/);
  assert.match(html, /Eighth-Class Soldier/);
  assert.doesNotMatch(html, /Ally|Unknown| \*/);
  assert.equal(b.document.getElementById('enemy-count').textContent, '(1)');
});

test('assumed ranks carry an asterisk and names are HTML-escaped', () => {
  const b = browser();
  b.update([contact({ name: '<script>x</script>', rankCode: 18, rankName: 'Ninth-Class Soldier', rankVerified: false })]);
  const html = b.document.getElementById('enemy-list').innerHTML;
  assert.match(html, /Ninth-Class Soldier \*/);
  assert.match(html, /&lt;script&gt;/);
  assert.doesNotMatch(html, /<script>/);
});

test('unknown own faction requests selection instead of showing alleged enemies', () => {
  const b = browser();
  b.update([contact({ isEnemy: null })], 'Unknown');
  assert.equal(b.document.getElementById('enemy-list').innerHTML, '');
  assert.match(b.document.getElementById('enemy-empty').textContent, /Eigene Fraktion noch unbekannt/);
});

test('stale contacts remain labelled in the list but get no live enemy map label', () => {
  const b = browser();
  b.update([contact({ positionStale: true, positionAgeMs: 12000 })]);
  assert.match(b.document.getElementById('enemy-list').innerHTML, /nicht frisch/);
  assert.equal(b.calls.filter(c => c[0] === 'fillText' && String(c[1]).includes('EnemyB')).length, 0);
});

test('fresh enemy positions get a labelled map marker', () => {
  const b = browser();
  b.update([contact()]);
  assert.ok(b.calls.some(c => c[0] === 'fillText' && String(c[1]).includes('EnemyB')));
});

test('estimated distances and crowded labels are handled explicitly', () => {
  const b = browser();
  b.update([contact({ distanceEstimated: true }), contact({ name: 'SecondEnemy' })]);
  assert.match(b.document.getElementById('enemy-list').innerHTML, /≈ 22 m/);
  assert.equal(b.calls.filter(c => c[0] === 'fillText' && /EnemyB|SecondEnemy/.test(String(c[1]))).length, 1);
});

test('enemy display can be disabled without disabling cubes', () => {
  const b = browser();
  b.update([contact()]);
  b.document.getElementById('enemies').listeners.click();
  assert.equal(b.document.getElementById('enemy-list').innerHTML, '');
  assert.match(b.document.getElementById('enemy-empty').textContent, /ausgeschaltet/);
  assert.equal(b.document.getElementById('count').textContent, '');
});
