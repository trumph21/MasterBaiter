// Liest GatherBuddy Reborns Fischdaten (GatherBuddy.GameData/Data/Fish/Data*.cs)
// und erzeugt daraus die Zuordnung Fisch -> Startkoeder.
//
// Das Format dort ist eine Fluent-DSL:
//   data.Apply(<fischId>, Patch.X) // Name
//       .Bait (data, <koederId>)                       direkt am Koeder
//       .Mooch(data, <koederId>, <zwischenfisch>...)   Kette, beginnt am Koeder
//       .Mooch(data, <zwischenfisch>)                  Kette, Koeder steht beim Zwischenfisch
//
// Der Startkoeder ist der erste Eintrag der Kette. Bei der einarmigen
// Mooch-Form wird rekursiv beim Zwischenfisch weitergesucht.
//
// Aufruf: node tools/extract-baits.js <verzeichnis-mit-Data*.cs> [ziel.json]

const fs = require('fs');
const path = require('path');

const SRC = process.argv[2];
const OUT = process.argv[3] || path.join(__dirname, '..', 'data', 'fish-baits.json');

if (!SRC || !fs.existsSync(SRC)) {
  console.error('Quellverzeichnis mit Data*.cs angeben.');
  process.exit(1);
}

const entries = new Map(); // fischId -> { name, kind: 'bait'|'mooch', chain: [id, ...] }

const APPLY = /data\.Apply\s*\(\s*(\d+)\s*,[^)]*\)\s*(?:\/\/\s*(.*))?/;
const BAIT = /^\s*\.(Bait|Mooch)\s*\(\s*data\s*,\s*([\d\s,]+?)\s*\)/;

for (const file of fs.readdirSync(SRC).filter((f) => /^Data.*\.cs$/.test(f))) {
  const lines = fs.readFileSync(path.join(SRC, file), 'utf8').split('\n');
  let current = null;
  for (const line of lines) {
    const a = line.match(APPLY);
    if (a) {
      current = { id: +a[1], name: (a[2] || '').trim() };
      continue;
    }
    if (!current) continue;
    const b = line.match(BAIT);
    if (b) {
      const ids = b[2].split(',').map((s) => +s.trim()).filter((n) => n > 0);
      entries.set(current.id, { name: current.name, kind: b[1].toLowerCase(), chain: ids });
      current = null;
    }
  }
}

// Startkoeder aufloesen. Bei .Mooch(data, <fisch>) ohne Koeder weitersuchen.
function resolve(fishId, seen = new Set()) {
  if (seen.has(fishId)) return null; // Zyklus
  seen.add(fishId);
  const e = entries.get(fishId);
  if (!e || !e.chain.length) return null;
  const first = e.chain[0];
  if (e.kind === 'bait') return { bait: first, chain: e.chain };
  // Mooch: ist das erste Glied selbst ein Fisch mit eigenem Eintrag, dann tiefer
  if (entries.has(first)) {
    const deeper = resolve(first, seen);
    return deeper ? { bait: deeper.bait, chain: [...deeper.chain, ...e.chain] } : null;
  }
  return { bait: first, chain: e.chain };
}

const out = {};
let unresolved = 0;
for (const [id, e] of entries) {
  const r = resolve(id);
  if (!r) { unresolved++; continue; }
  out[id] = { name: e.name, bait: r.bait, chain: r.chain };
}

fs.mkdirSync(path.dirname(OUT), { recursive: true });
fs.writeFileSync(OUT, JSON.stringify(out));

const baits = new Map();
for (const v of Object.values(out)) baits.set(v.bait, (baits.get(v.bait) || 0) + 1);

console.log('Fische mit Eintrag   :', entries.size);
console.log('Davon aufgeloest     :', Object.keys(out).length, '| ohne Koeder:', unresolved);
console.log('Verschiedene Koeder  :', baits.size);
console.log('Haeufigste Koeder    :', [...baits.entries()].sort((a, b) => b[1] - a[1]).slice(0, 8).map(([id, n]) => `${id} (${n}x)`).join(', '));
console.log('Geschrieben nach     :', path.relative(process.cwd(), OUT), '-', (fs.statSync(OUT).size / 1024).toFixed(0), 'KB');
