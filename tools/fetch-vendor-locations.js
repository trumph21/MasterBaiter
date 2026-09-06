// Holt Verkaufs-NPCs samt Koordinaten aus der Eorzea-Datenbank des Lodestone.
//
// Hintergrund: Das Level-Sheet des Spiels kennt viele neuere NPCs nicht, die
// werden ueber die Kartendateien platziert. Deren Koordinaten stehen aber im
// Lodestone. Das Ergebnis wird als data/vendor-locations.json mitgeliefert und
// im Plugin nur dann benutzt, wenn die Spieldaten keinen Standort hergeben.
//
// Aufruf: node tools/fetch-vendor-locations.js [--limit N] [--only ID,ID]
//
// Geht bewusst langsam vor. Die Datenbank ist eine oeffentliche Webseite, kein
// Dienst mit Schnittstelle.

const fs = require('fs');
const path = require('path');

const BAITS = path.join(__dirname, '..', 'data', 'fish-baits.json');
const OUT = path.join(__dirname, '..', 'data', 'vendor-locations.json');
const LODESTONE = 'https://eu.finalfantasyxiv.com/lodestone/playguide/db/item/';
const XIVAPI = 'https://beta.xivapi.com/api/1/sheet/Item';
const DELAY_MS = 400;
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)';

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function get(url) {
  const res = await fetch(url, { headers: { 'User-Agent': UA, 'Accept-Language': 'en-US,en' } });
  if (!res.ok) throw new Error(`${res.status} ${url}`);
  return res.text();
}

async function itemName(id) {
  const res = await fetch(`${XIVAPI}/${id}?fields=Name`, { headers: { 'User-Agent': UA } });
  if (!res.ok) return null;
  const json = await res.json();
  return json?.fields?.Name || null;
}

/** Sucht den Lodestone-Eintrag zu einem Itemnamen und liefert dessen Kennung. */
async function findItemPage(name) {
  const html = await get(`${LODESTONE}?q=${encodeURIComponent(name)}`);
  const rx = /\/lodestone\/playguide\/db\/item\/([a-f0-9]+)\/"[^>]*>\s*<span[^>]*>([^<]+)</g;
  let m;
  while ((m = rx.exec(html))) {
    const found = m[2].replace(/&#39;/g, "'").replace(/&amp;/g, '&').trim();
    if (found.toLowerCase() === name.toLowerCase()) return m[1];
  }
  // Rueckfall: erster Treffer ueberhaupt
  const first = html.match(/\/lodestone\/playguide\/db\/item\/([a-f0-9]+)\//);
  return first ? first[1] : null;
}

/** Liest die Tabelle "Selling NPC" einer Item-Seite aus. */
function parseVendors(html) {
  const start = html.indexOf('Selling NPC');
  if (start < 0) return [];
  const table = html.slice(start, html.indexOf('</table>', start));
  const rx = /db\/shop\/[a-f0-9]+\/[^"]*"[^>]*>([^<]+)<\/a>\s*<\/td>\s*<td[^>]*>([^<(]+)\(X:([\d.]+)\s*Y:([\d.]+)\)/g;
  const out = [];
  let m;
  while ((m = rx.exec(table))) {
    out.push({
      npc: m[1].replace(/&#39;/g, "'").replace(/&amp;/g, '&').trim(),
      area: m[2].trim(),
      x: parseFloat(m[3]),
      y: parseFloat(m[4]),
    });
  }
  return out;
}

(async () => {
  const args = process.argv.slice(2);
  const limitArg = args.indexOf('--limit');
  const onlyArg = args.indexOf('--only');
  const limit = limitArg >= 0 ? parseInt(args[limitArg + 1], 10) : Infinity;
  const only = onlyArg >= 0 ? new Set(args[onlyArg + 1].split(',').map(Number)) : null;

  const baits = new Set(Object.values(JSON.parse(fs.readFileSync(BAITS, 'utf8'))).map((e) => e.bait));
  const ids = [...baits].filter((id) => !only || only.has(id)).slice(0, limit);

  const existing = fs.existsSync(OUT) ? JSON.parse(fs.readFileSync(OUT, 'utf8')) : {};
  const result = { ...existing };

  let done = 0;
  let withVendors = 0;
  for (const id of ids) {
    done++;
    if (result[String(id)]) continue; // schon vorhanden, nicht erneut holen

    try {
      const name = await itemName(id);
      if (!name) { console.log(`${done}/${ids.length}  ${id}  kein Name`); continue; }
      await sleep(DELAY_MS);

      const page = await findItemPage(name);
      if (!page) { console.log(`${done}/${ids.length}  ${id}  ${name}: nicht gefunden`); continue; }
      await sleep(DELAY_MS);

      const html = await get(`${LODESTONE}${page}/`);
      const vendors = parseVendors(html);
      result[String(id)] = { name, vendors };
      if (vendors.length) withVendors++;
      console.log(`${done}/${ids.length}  ${id}  ${name}: ${vendors.length} NPCs`);
      await sleep(DELAY_MS);
    } catch (e) {
      console.log(`${done}/${ids.length}  ${id}  Fehler: ${e.message}`);
      await sleep(2000);
    }

    if (done % 20 === 0)
      fs.writeFileSync(OUT, JSON.stringify(result));
  }

  fs.writeFileSync(OUT, JSON.stringify(result));
  const total = Object.values(result).reduce((n, e) => n + e.vendors.length, 0);
  console.log(`\nFertig. ${Object.keys(result).length} Koeder, ${total} Verkaufsstellen, in dieser Runde ${withVendors} mit NPCs.`);
  console.log('Geschrieben:', OUT);
})();
