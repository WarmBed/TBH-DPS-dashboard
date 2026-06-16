// Fetch TaskBarHero (Steam appid 3678970) Community Market prices into prices.json.
// Paged via market/search/render (no login, no per-item priceoverview spam): each result gives
// the lowest sell price (cents) + live listing count. Run by the steam-prices GitHub Action every
// 30 min; the output is published to the `data` branch and served via jsDelivr to the plugin.
import { writeFile } from 'node:fs/promises';

const APPID = 3678970;
const PER = 100;            // Steam caps search/render at 100 per page
const UA = 'Mozilla/5.0 (tbh-prices-cron; +https://github.com/WarmBed/TBH-DPS-dashboard)';

async function page(start) {
  const url = `https://steamcommunity.com/market/search/render/?appid=${APPID}&norender=1&count=${PER}&start=${start}`;
  const res = await fetch(url, { headers: { 'User-Agent': UA } });
  if (!res.ok) throw new Error(`steam HTTP ${res.status} at start=${start}`);
  return res.json();
}

const sleep = (ms) => new Promise(r => setTimeout(r, ms));
const items = {};
let start = 0, maxTotal = 0, currency = '', guard = 0;
// Steam's total_count is FLAKY (the same query returns 151 one call, 112 the next) and unauthenticated
// bursts get rate-limited with short/empty pages. So: (1) never trust a single total_count as the stop
// bound — page until a page is CONFIRMED empty after retries; (2) retry empty/short pages a few times
// before accepting them as the end; (3) track the MAX total_count seen for a completeness check.
while (guard++ < 80) {
  let j = null, results = null;
  for (let attempt = 0; attempt < 6; attempt++) {
    try { j = await page(start); } catch { j = null; }
    if (j && j.success === true && Array.isArray(j.results)) {
      results = j.results;
      // accept a non-empty page immediately; accept an empty one only after a few confirming retries
      if (results.length > 0 || attempt >= 3) break;
    }
    await sleep(4000 * (attempt + 1) + Math.floor(Math.random() * 1500));   // back off + jitter, retry same start
    j = null; results = null;
  }
  if (!j) throw new Error(`steam kept failing at start=${start} (got ${Object.keys(items).length})`);
  if (typeof j.total_count === 'number' && j.total_count > 0) maxTotal = Math.max(maxTotal, j.total_count);
  results = results || [];
  if (results.length === 0) break;       // confirmed empty after retries -> genuine end of listings
  for (const r of results) {
    const name = r.hash_name || r.name;
    if (!name) continue;
    if (!currency && r.sell_price_text) currency = r.sell_price_text.replace(/[0-9.,\s]/g, ''); // e.g. "$"
    // asset_description carries the display name, rarity color, icon and type — captured so the web
    // terminal can build its catalog from Steam directly (no tbh-market for anything but the order book).
    const ad = r.asset_description || {};
    items[name] = {
      lowestCents: r.sell_price ?? 0, qty: r.sell_listings ?? 0,
      dispName: ad.name || name, color: ad.name_color || null, icon: ad.icon_url || null, type: ad.type || null,
    };
  }
  start += results.length;               // advance by what we actually got, not a fixed page size
  await sleep(2500 + Math.floor(Math.random() * 1500));  // be gentle to Steam between pages
}
// Steam's total_count is inflated (counts delisted / 0-listing entries), so search only ever returns a
// smaller set of real hash_names — reaching total_count is impossible and is NOT the completeness signal.
// The real signal is the confirmed-empty page above. Guard only against a clearly-truncated run (hard
// rate-limit) by refusing to ship a near-empty feed.
const gotCount = Object.keys(items).length;
console.log(`[prices] fetched ${gotCount} unique items (steam total_count reported up to ${maxTotal})`);
if (gotCount < 50) { console.error(`[prices] ABORT: only ${gotCount} items — Steam rate-limited the run; keeping the previous feed.`); process.exit(1); }

// ---- price history (item-major). Each item keeps a rolling [[tMs, cents], ...] series in history.json,
// read back each run from raw.githubusercontent (the data branch is force-pushed as a single orphan
// commit, so we persist the window inside the file). On first sight of an item we BACKFILL its real
// history from tbh-market's public API (Steam data, no login); afterwards our own 30-min cron appends
// forward. Sampled ~2h, kept ~8 days. Item-major also avoids repeating item names every snapshot.
const now = Date.now();
const DAY = 86400000;
const HISTORY_DAYS = 8;
const SAMPLE_GAP_MS = 25 * 60 * 1000;      // ~30min spacing (one point per cron run) for intraday timeframes
const BUCKET_MS = 2 * 3600 * 1000;         // downsample backfilled points to ~2h
const HIST_WINDOW_MS = 7 * DAY;            // window shipped to the plugin
const MAX_BACKFILL = 400;                  // per-run cap on tbh-market fetches (one-time courtesy burst)
const REPO = process.env.GITHUB_REPOSITORY || 'WarmBed/TBH-DPS-dashboard';
const HISTORY_URL = `https://raw.githubusercontent.com/${REPO}/data/history.json`;
const TBH_ITEM = 'https://tbh-market.com/api/item/';

let history = { items: {}, vol: { at: 0, items: {} } };
try {
  const r = await fetch(HISTORY_URL, { headers: { 'User-Agent': UA } });
  if (r.ok) { const j = await r.json(); if (j && typeof j === 'object') history = j; }
} catch { /* first run / 404 -> empty history */ }
if (!history.items || Array.isArray(history.items)) history.items = {};   // ignore old snapshot-major format
if (!history.vol) history.vol = { at: 0, items: {} };

function downsample(points, bucketMs) {
  const out = []; let lastB = -2;
  for (const p of points) { const b = Math.floor(p[0] / bucketMs); if (b !== lastB) { out.push(p); lastB = b; } }
  return out;
}
async function tbhBackfill(name) {
  try {
    const r = await fetch(TBH_ITEM + encodeURIComponent(name), { headers: { 'User-Agent': UA } });
    if (!r.ok) return null;
    const j = await r.json();
    const h = Array.isArray(j?.history) ? j.history : null;
    if (!h) return null;
    const pts = h.filter(p => p && p.recorded_at && p.sell_price != null).map(p => [p.recorded_at * 1000, p.sell_price]);
    return downsample(pts, BUCKET_MS);
  } catch { return null; }
}

const cutoff = now - HISTORY_DAYS * DAY;
let backfilled = 0;
for (const [name, v] of Object.entries(items)) {
  let series = Array.isArray(history.items[name]) ? history.items[name] : null;
  if ((!series || series.length < 4) && backfilled < MAX_BACKFILL) {   // one-time real-history seed
    const seed = await tbhBackfill(name);
    if (seed && seed.length) { series = seed; backfilled++; await sleep(1500); }   // be gentle to tbh-market
  }
  if (!series) series = [];
  const lastPt = series[series.length - 1];
  // forward point: [tMs, cents, 24h-volume] — the volume is the last-known (≤6h) priceoverview figure,
  // so the web terminal can build a volume series (Steam ask-history has no per-trade volume).
  if (!lastPt || now - lastPt[0] >= SAMPLE_GAP_MS) series.push([now, v.lowestCents, (history.vol.items[name] || {}).vol || 0]);
  history.items[name] = series.filter(p => p[0] >= cutoff);                             // prune window
}

// prevCents (~24h ago) + the last-7-day series shipped to the plugin (timestamps in seconds to stay small)
let stamped = 0;
for (const [name, v] of Object.entries(items)) {
  const series = history.items[name] || [];
  let ref = null, rd = Infinity;
  for (const p of series) { const d = Math.abs(p[0] - (now - DAY)); if (d < rd) { rd = d; ref = p; } }
  if (ref && rd <= 18 * 3600 * 1000) { v.prevCents = ref[1]; v.prevAt = ref[0]; stamped++; }
  const win = series.filter(p => p[0] >= now - HIST_WINDOW_MS);
  if (win.length) v.hist = win.map(p => [Math.floor(p[0] / 1000), p[1], p[2] || 0]);
}

// ---- 24h volume + median sale price (priceoverview is per-item & rate-limited, so refresh only every
// ~6h and persist between the 30-min price runs inside history.vol). ----
const VOL_REFRESH_MS = 6 * 3600 * 1000;
let vol = (history.vol && history.vol.items) ? history.vol : { at: 0, items: {} };
const volAge = vol.at ? now - vol.at : Infinity;
if (volAge >= VOL_REFRESH_MS) {
  const names = Object.keys(items);
  let done = 0;
  for (const name of names) {
    let pj = null;
    for (let attempt = 0; attempt < 3; attempt++) {
      try {
        const r = await fetch(`https://steamcommunity.com/market/priceoverview/?appid=${APPID}&currency=1&market_hash_name=${encodeURIComponent(name)}`,
          { headers: { 'User-Agent': UA } });
        if (r.status === 429) { await sleep(15000); continue; }   // rate-limited: wait and retry
        if (r.ok) { pj = await r.json(); break; }
      } catch { /* retry */ }
      await sleep(4000);
    }
    if (pj && pj.success) {
      const vRaw = (pj.volume || '').replace(/[^0-9]/g, '');
      const mRaw = (pj.median_price || '').replace(/[^0-9.]/g, '');
      vol.items[name] = { vol: vRaw ? parseInt(vRaw, 10) : 0, medianCents: mRaw ? Math.round(parseFloat(mRaw) * 100) : 0 };
      done++;
    }
    await sleep(3500);   // ~17 req/min — stay under Steam's unauthenticated limit
  }
  vol.at = now;
  console.log(`refreshed volume/median for ${done}/${names.length} items`);
}
history.vol = vol;
for (const [name, v] of Object.entries(items)) {
  const vr = vol.items[name];
  if (vr) { v.vol = vr.vol; v.medianCents = vr.medianCents; }
}

const out = { cachedAt: now, appid: APPID, currency: currency || '$', count: Object.keys(items).length, items };
await writeFile('prices.json', JSON.stringify(out));
await writeFile('history.json', JSON.stringify(history));
console.log(`wrote prices.json: ${out.count} items; backfilled ${backfilled} from tbh-market, prevCents on ${stamped}, hist series for ${Object.keys(history.items).length} items`);
