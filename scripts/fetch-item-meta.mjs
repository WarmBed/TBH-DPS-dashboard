// Regenerate DpsMeter/item_meta.json from the wiki's full item table.
//
// Source: https://taskbarhero.wiki/data/items.json — with a browser User-Agent it returns the
// COMPLETE array (5944 entries) keyed by `id` (= in-game ItemKey), each with grade/type/gear/level/icon.
// Without a UA the same URL returns a paginated market view with no ItemKey, so the UA is required.
//
// Strategy: APPEND-ONLY. Existing gear entries (5760) are kept byte-for-byte so GearScore / box / icon
// consumers are untouched; only the 184 non-gear entries (MATERIAL / STAGEBOX) are added so the F8
// item-stats panel can group every owned item by grade and category.
//
// Per-entry shape: { g: grade, l: level, i: iconPathRelativeToGameRoot, t: gearSubtypeOrCategory, c?: category }
//   - gear:     t = gear subtype (SWORD/HELMET/RING...), no `c`  (unchanged from before)
//   - non-gear: t = category (MATERIAL/STAGEBOX), c = category    (c marks "not gear")
//
// Run: node scripts/fetch-item-meta.mjs

import { readFileSync, writeFileSync } from 'node:fs';

const SRC = 'https://taskbarhero.wiki/data/items.json';
const META = new URL('../DpsMeter/item_meta.json', import.meta.url);

const res = await fetch(SRC, { headers: { 'User-Agent': 'Mozilla/5.0' } });
if (!res.ok) throw new Error(`fetch ${SRC} -> ${res.status}`);
const all = await res.json();
if (!Array.isArray(all)) throw new Error('expected an array (is the User-Agent set?)');

const meta = JSON.parse(readFileSync(META, 'utf8'));
const before = Object.keys(meta).length;

const strip = (p) => (p || '').replace(/^\/+/, '').replace(/^game\//, ''); // "/game/items/.." -> "items/.."

let added = 0;
for (const e of all) {
  const id = String(e.id);
  if (meta[id]) continue;              // keep existing (gear) entries exactly as they are
  const isGear = e.type === 'GEAR';
  const entry = {
    g: e.grade || '',
    l: e.level || 0,
    i: strip(e.icon),
    t: isGear ? (e.gear || '') : (e.type || ''),
  };
  if (!isGear) entry.c = e.type || '';
  meta[id] = entry;
  added++;
}

writeFileSync(META, JSON.stringify(meta), 'utf8');
console.log(`item_meta: ${before} -> ${Object.keys(meta).length} (+${added} non-gear)`);
