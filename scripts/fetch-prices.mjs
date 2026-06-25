// Fetch TaskBarHero (Steam appid 3678970) Community Market prices into prices.json.
// Paged via market/search/render (no login, no per-item priceoverview spam): each result gives
// the lowest sell price (cents) + live listing count. Run by the steam-prices GitHub Action every
// 30 min; the output is published to the `data` branch and served via jsDelivr to the plugin.
import { writeFile } from 'node:fs/promises';

const APPID = 3678970;
const PER = 100;            // Steam caps search/render at 100 per page
const UA = 'Mozilla/5.0 (tbh-prices-cron; +https://github.com/WarmBed/TBH-DPS-dashboard)';

// Route Steam through the Cloudflare Worker proxy (worker-tbh-proxy) when configured. GitHub's
// datacenter IP is heavily rate-limited by Steam — the search sweep stalls at ~131/740 and the
// priceoverview volume loop 429s and hangs for hours. CF egress is NOT throttled: a full 740 sweep
// and all volumes take seconds. Falls back to hitting Steam directly when PROXY_BASE is unset.
const PROXY_BASE = (process.env.PROXY_BASE || '').replace(/\/+$/, '');
const PROXY_KEY = process.env.PROXY_KEY || '';
const VIA_PROXY = !!PROXY_BASE;
const PROXY_H = PROXY_KEY ? { 'x-proxy-key': PROXY_KEY } : {};
function searchUrl(start) {
  const q = `appid=${APPID}&norender=1&count=${PER}&start=${start}&sort_column=name&sort_dir=asc`;
  return VIA_PROXY ? `${PROXY_BASE}/steam/search?${q}` : `https://steamcommunity.com/market/search/render/?${q}`;
}
function priceUrl(name) {
  const q = `appid=${APPID}&currency=1&market_hash_name=${encodeURIComponent(name)}`;
  return VIA_PROXY ? `${PROXY_BASE}/steam/price?${q}` : `https://steamcommunity.com/market/priceoverview/?${q}`;
}

async function page(start) {
  // sort_column=name keeps the order STABLE across calls so each start-window covers a disjoint slice.
  const res = await fetch(searchUrl(start), { headers: { 'User-Agent': UA, ...PROXY_H } });
  if (!res.ok) throw new Error(`steam HTTP ${res.status} at start=${start}`);
  return res.json();
}

const sleep = (ms) => new Promise(r => setTimeout(r, ms));
const items = {};
let start = 0, maxTotal = 0, currency = '', guard = 0, sinceNew = 0, lastCount = 0;
// Steam's market search is hostile to completeness: total_count is FLAKY+inflated (151 one call, 112 the
// next; counts delisted/0-listing entries it never actually returns), it caps the page at ~10 regardless
// of count=, the result ORDER shifts between calls (so a fixed start-window misses items that moved), and
// unauthenticated/CI bursts get rate-limited with short/empty pages. So we SWEEP with wrap-around and
// dedup-accumulate: page forward, wrap back to 0 at the end, and keep going until N consecutive pages add
// NOTHING new (the real "we've seen everything Steam will give us" signal). Generous jittered sleeps keep
// us under the throttle so CI catches the same ~full set a single home IP does.
const STALL = 14;          // stop after this many consecutive pages with no new item
const MAX_PAGES = 200;     // hard backstop
while (guard++ < MAX_PAGES) {
  let j = null, results = null;
  for (let attempt = 0; attempt < 6; attempt++) {
    try { j = await page(start); } catch { j = null; }
    if (j && j.success === true && Array.isArray(j.results)) {
      results = j.results;
      if (results.length > 0 || attempt >= 3) break;   // accept non-empty now; empty only after retries
    }
    await sleep(4000 * (attempt + 1) + Math.floor(Math.random() * 1500));   // back off + jitter, retry same start
    j = null; results = null;
  }
  if (!j) throw new Error(`steam kept failing at start=${start} (got ${Object.keys(items).length})`);
  if (typeof j.total_count === 'number' && j.total_count > 0) maxTotal = Math.max(maxTotal, j.total_count);
  results = results || [];
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
  const c = Object.keys(items).length;
  if (c > lastCount) { sinceNew = 0; lastCount = c; } else sinceNew++;
  if (sinceNew >= STALL) break;                                   // dry — exhausted what Steam returns
  start += results.length || 10;                                 // advance by what we got (Steam pages ~10)
  if (maxTotal > 0 && start >= maxTotal) start = 0;              // wrap to re-catch items that shifted order
  await sleep(VIA_PROXY ? 150 : 2500 + Math.floor(Math.random() * 1500));   // CF egress isn't throttled → no need to pace
}
// total_count is inflated and unreachable, so it's NOT the completeness signal — the STALL above is.
// Guard only against a clearly-truncated run (hard rate-limit) by refusing to ship a near-empty feed.
const gotCount = Object.keys(items).length;
console.log(`[prices] fetched ${gotCount} unique items in ${guard} pages (steam total_count reported up to ${maxTotal})`);
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

// ---- 24h volume + median sale price (priceoverview, per-item). Steam rate-limits this HARD even via
// the proxy, so refreshing all ~740 every run can exceed the job timeout. Instead each run refreshes the
// STALEST items first within a fixed time budget; values persist (per-item `at`) in history.vol and
// rotate fully in over a few runs. Direct (no proxy) keeps the gentle ~6h sequential refresh. ----
const VOL_REFRESH_MS = 6 * 3600 * 1000;
const VOL_BUDGET_MS = Number(process.env.VOL_BUDGET_MS || 5 * 60 * 1000);   // per-run time box for the proxy refresh
let vol = (history.vol && history.vol.items) ? history.vol : { at: 0, items: {} };
const volAge = vol.at ? now - vol.at : Infinity;

async function priceoverview(name, tries) {
  for (let attempt = 0; attempt < tries; attempt++) {
    try {
      const r = await fetch(priceUrl(name), { headers: { 'User-Agent': UA, ...PROXY_H } });
      if (r.status === 429) { await sleep(VIA_PROXY ? 800 * (attempt + 1) : 15000); continue; }   // escalating backoff
      if (r.ok) return await r.json();   // valid response — success may be true OR false (no market data)
    } catch { /* retry */ }
    await sleep(VIA_PROXY ? 600 * (attempt + 1) : 4000);
  }
  return null;   // never reached Steam → leave the persisted value, retry next run
}
function applyVol(name, pj) {
  if (!pj) return false;                                   // unreachable → keep stale, don't stamp `at`
  if (pj.success) {
    const vRaw = (pj.volume || '').replace(/[^0-9]/g, '');
    const mRaw = (pj.median_price || '').replace(/[^0-9.]/g, '');
    vol.items[name] = { vol: vRaw ? parseInt(vRaw, 10) : 0, medianCents: mRaw ? Math.round(parseFloat(mRaw) * 100) : 0, at: now };
  } else {
    // valid "no market data" answer → record 0 with a timestamp so we stop re-hitting it every run
    const prev = vol.items[name] || {};
    vol.items[name] = { vol: 0, medianCents: prev.medianCents || 0, at: now };
  }
  return true;
}

const volNames = Object.keys(items);
if (VIA_PROXY && !process.env.SKIP_VOL) {
  // stalest first (never-refreshed = at 0); among equally-stale, more-listed (active) items go first
  const order = volNames.slice().sort((a, b) => {
    const ta = (vol.items[a] && vol.items[a].at) || 0, tb = (vol.items[b] && vol.items[b].at) || 0;
    return ta !== tb ? ta - tb : (items[b].qty || 0) - (items[a].qty || 0);
  });
  const deadline = Date.now() + VOL_BUDGET_MS;   // budget from here, so a slow sweep doesn't starve volume
  let done = 0, idx = 0;
  const worker = async () => { while (idx < order.length && Date.now() < deadline) { const n = order[idx++]; if (applyVol(n, await priceoverview(n, 3))) done++; } };
  await Promise.all(Array.from({ length: 8 }, worker));
  vol.at = now;
  console.log(`refreshed volume/median for ${done}/${order.length} items this run (stalest-first, ${Math.round(VOL_BUDGET_MS / 1000)}s budget, via proxy)`);
} else if (!process.env.SKIP_VOL && volAge >= VOL_REFRESH_MS) {
  let done = 0;
  for (const name of volNames) {
    if (applyVol(name, await priceoverview(name, 3))) done++;
    await sleep(3500);   // ~17 req/min — stay under Steam's unauthenticated limit
  }
  vol.at = now;
  console.log(`refreshed volume/median for ${done}/${volNames.length} items (direct)`);
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
