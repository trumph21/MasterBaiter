// Holt die Waehrungspreise der Koeder aus der Eorzea-Datenbank des Lodestone.
//
// Hintergrund: Gil-Preise stehen am Item selbst und kommen aus den Spieldaten.
// Was ein Koeder am Scrip-Tausch kostet, findet das Plugin dort nicht — die
// Preisspalte der Item-Seite nennt es dagegen ausdruecklich ("Purple
// Gatherers' Scrip x 1"). Das Ergebnis wird als data/bait-prices.json
// mitgeliefert und nur benutzt, wenn die Spieldaten keinen Preis hergeben.
//
// Aufruf: node tools/fetch-bait-prices.js [--limit N] [--only ID,ID] [--force]
//
// Geht bewusst langsam vor. Die Datenbank ist eine oeffentliche Webseite, kein
// Dienst mit Schnittstelle.

const fs = require('fs');
const path = require('path');

const BAITS = path.join(__dirname, '..', 'data', 'fish-baits.json');
const OUT = path.join(__dirname, '..', 'data', 'bait-prices.json');
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

const unescapeHtml = (s) =>
  s.replace(/&#39;/g, "'").replace(/&amp;/g, '&').replace(/&quot;/g, '"').trim();

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
  while ((m = rx.exec(html))) if (unescapeHtml(m[2]).toLowerCase() === name.toLowerCase()) return m[1];
  const first = html.match(/\/lodestone\/playguide\/db\/item\/([a-f0-9]+)\//);
  return first ? first[1] : null;
}

/**
 * Liest die Waehrungspreise aus der Tabelle "Selling NPC".
 *
 * Gil-Laeden haben dort keine Preisspalte — ihr Preis steht am Item und ist
 * dem Plugin schon bekannt. Nur Sonderlaeden nennen Waehrung und Menge, und
 * genau die fehlen sonst.
 *
 * ALLE Zeilen, nicht nur die erste: Derselbe Koeder ist oft in zwei Waehrungen
 * zu haben — Dragonfly kostet 10 Cosmocredit bei der Cosmic Exploration und 5
 * Purple Gatherers' Scrip am Tausch. Wer nur den ersten Treffer nimmt, behaelt
 * je Koeder einen zufaelligen der beiden und verliert den anderen lautlos.
 *
 * Je Waehrung zaehlt der erste Preis; mehrere Haendler mit derselben Waehrung
 * verlangen dasselbe, das sind nur Wiederholungen.
 */
function parsePrices(html) {
  const start = html.indexOf('Selling NPC');
  if (start < 0) return [];

  const end = html.indexOf('</table>', start);
  const table = html.slice(start, end < 0 ? undefined : end);

  const rx = /<h4>([^<]+)<\/h4>\s*<span class=\"db-view__data__number\">([\d,]+)<\/span>/g;
  const found = new Map();

  let m;
  while ((m = rx.exec(table))) {
    const amount = parseInt(m[2].replace(/,/g, ''), 10);
    if (!Number.isFinite(amount) || amount <= 0) continue;

    const currency = unescapeHtml(m[1]);
    if (!found.has(currency)) found.set(currency, amount);
  }

  return [...found].map(([currency, amount]) => ({ amount, currency }));
}

(async () => {
  const args = process.argv.slice(2);
  const limitArg = args.indexOf('--limit');
  const onlyArg = args.indexOf('--only');
  const force = args.includes('--force');
  const limit = limitArg >= 0 ? parseInt(args[limitArg + 1], 10) : Infinity;
  const only = onlyArg >= 0 ? new Set(args[onlyArg + 1].split(',').map(Number)) : null;

  const baits = new Set(Object.values(JSON.parse(fs.readFileSync(BAITS, 'utf8'))).map((e) => e.bait));
  const ids = [...baits].filter((id) => !only || only.has(id)).slice(0, limit);

  const existing = fs.existsSync(OUT) ? JSON.parse(fs.readFileSync(OUT, 'utf8')) : {};
  const result = { ...existing };

  let done = 0;
  let found = 0;
  for (const id of ids) {
    done++;
    if (!force && result[String(id)]) continue;

    try {
      const name = await itemName(id);
      if (!name) { console.log(`${done}/${ids.length}  ${id}  kein Name`); continue; }
      await sleep(DELAY_MS);

      const page = await findItemPage(name);
      if (!page) { console.log(`${done}/${ids.length}  ${id}  ${name}: nicht gefunden`); continue; }
      await sleep(DELAY_MS);

      const prices = parsePrices(await get(`${LODESTONE}${page}/`));
      if (prices.length > 0) {
        result[String(id)] = { name, prices };
        found += prices.length;
        console.log(`${done}/${ids.length}  ${id}  ${name}: ` +
          prices.map((p) => `${p.amount} ${p.currency}`).join('  |  '));
      } else {
        console.log(`${done}/${ids.length}  ${id}  ${name}: nur Gil oder kein Laden`);
      }

      await sleep(DELAY_MS);
    } catch (e) {
      console.log(`${done}/${ids.length}  ${id}  Fehler: ${e.message}`);
      await sleep(2000);
    }

    if (done % 20 === 0)
      fs.writeFileSync(OUT, JSON.stringify(result, null, 1) + '\n');
  }

  fs.writeFileSync(OUT, JSON.stringify(result, null, 1) + '\n');
  console.log(`\nfertig: ${found} Waehrungspreise, ${Object.keys(result).length} in ${path.relative(process.cwd(), OUT)}`);
})();
