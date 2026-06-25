// TBH price-feed pipeline as a long-running Zeabur service (Volcano Engine egress reaches Steam +
// tbh-market + GitHub directly, all unthrottled on search, so NO Cloudflare proxy needed).
//
// Two concurrent loops:
//   • priceLoop  — every PRICE_INTERVAL_MS: sweep the full catalog (search/render, ~740, fast),
//                  fold in the latest volumes, build prices.json + market.json + detail, push to `data`.
//   • volumeLoop — every VOL_INTERVAL_MS: ONE priceoverview for the stalest item (gentle rotation),
//                  so 24h volume/median fills the whole catalog over ~ (740 * VOL_INTERVAL) and Steam's
//                  per-IP rate limit is never tripped.
//
// State (price history + per-item volume + `at` timestamps) persists in history.json on the `data`
// branch and is reloaded on startup, so restarts don't lose rotation progress.
import { writeFile, mkdir, rm } from 'node:fs/promises';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { homedir, tmpdir } from 'node:os';
import { writeFileSync, mkdirSync } from 'node:fs';
const execFileP = promisify(execFile);

const APPID = 3678970;
const UA = 'tbh-zeabur-pipeline';
const REPO = process.env.REPO || 'WarmBed/TBH-DPS-dashboard';
const PRICE_INTERVAL_MS = Number(process.env.PRICE_INTERVAL_MS || 5 * 60 * 1000);
const VOL_INTERVAL_MS = Number(process.env.VOL_INTERVAL_MS || 10000);   // one priceoverview per 10s
const MAX_DETAIL = Number(process.env.MAX_DETAIL || 140);
const PER = 100;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const log = (...a) => console.log(new Date().toISOString(), ...a);

// ---------- generic fetch ----------
async function jget(url, { tries = 4, label = '' } = {}) {
  for (let i = 0; i < tries; i++) {
    try {
      const r = await fetch(url, { headers: { 'User-Agent': UA } });
      if (r.status === 429) { await sleep(2000 * (i + 1)); continue; }
      if (r.ok) return await r.json();
    } catch { /* retry */ }
    await sleep(1000 * (i + 1));
  }
  throw new Error(`fetch failed ${label || url}`);
}

// ---------- Steam (direct) ----------
function searchUrl(start) {
  return `https://steamcommunity.com/market/search/render/?appid=${APPID}&norender=1&count=${PER}&start=${start}&sort_column=name&sort_dir=asc`;
}
async function getPage(start) {
  for (let a = 0; a < 6; a++) {
    try { const r = await fetch(searchUrl(start), { headers: { 'User-Agent': UA } }); if (r.ok) return await r.json(); if (r.status === 429) await sleep(2500 * (a + 1)); else await sleep(800 * (a + 1)); }
    catch { await sleep(1000 * (a + 1)); }
  }
  return null;
}
async function sweepCatalog() {
  const items = {};
  let currency = '$', total = 0;
  const absorb = (j) => {
    if (!j || !Array.isArray(j.results)) return;
    for (const r of j.results) {
      const name = r.hash_name || r.name; if (!name) continue;
      if (currency === '$' && r.sell_price_text) currency = r.sell_price_text.replace(/[0-9.,\s]/g, '') || '$';
      const ad = r.asset_description || {};
      items[name] = { lowestCents: r.sell_price ?? 0, qty: r.sell_listings ?? 0, dispName: ad.name || name, color: ad.name_color || null, icon: ad.icon_url || null, type: ad.type || null };
    }
  };
  // deterministic sweep 0..total (step 10, Steam caps the page at ~10). CF/Volcano egress isn't throttled
  // on search, so one full pass gets the catalog; repeat (≤3) only if a few items shifted order between calls.
  const first = await getPage(0);
  if (first && typeof first.total_count === 'number') total = first.total_count;
  absorb(first);
  const end = total > 0 ? total : 1000;
  for (let pass = 0; pass < 3; pass++) {
    for (let start = pass === 0 ? 10 : 0; start <= end; start += 10) { absorb(await getPage(start)); await sleep(100); }
    if (total && Object.keys(items).length >= total) break;   // full catalog captured
  }
  return { items, total: total || Object.keys(items).length, currency };
}
async function priceoverview(name) {
  for (let a = 0; a < 3; a++) {
    try {
      const r = await fetch(`https://steamcommunity.com/market/priceoverview/?appid=${APPID}&currency=1&market_hash_name=${encodeURIComponent(name)}`, { headers: { 'User-Agent': UA } });
      if (r.status === 429) { await sleep(8000); continue; }
      if (r.ok) return await r.json();   // success may be true or false
    } catch { /* retry */ }
    await sleep(3000);
  }
  return null;
}

// ---------- tbh-market (direct) ----------
const TBH_ITEM = 'https://tbh-market.com/api/item/';
const TBH_OB = 'https://tbh-market.com/api/orderbook/';
const BUCKET_MS = 2 * 3600 * 1000;
function downsample(points, bucketMs) { const out = []; let lastB = -2; for (const p of points) { const b = Math.floor(p[0] / bucketMs); if (b !== lastB) { out.push(p); lastB = b; } } return out; }
async function tbhBackfill(name) {
  try {
    const j = await jget(TBH_ITEM + encodeURIComponent(name), { tries: 2, label: 'tbh item' });
    const h = Array.isArray(j?.history) ? j.history : null;
    if (!h) return null;
    const pts = h.filter((p) => p && p.recorded_at && p.sell_price != null).map((p) => [p.recorded_at * 1000, p.sell_price]);
    return downsample(pts, BUCKET_MS);
  } catch { return null; }
}

// ---------- localized names (item_names.json on main) ----------
const RARITY = {
  common: { 'zh-Hant': '普通', 'zh-Hans': '普通', ja: 'コモン', es: 'Común' }, uncommon: { 'zh-Hant': '罕見', 'zh-Hans': '罕见', ja: 'アンコモン', es: 'Infrecuente' },
  rare: { 'zh-Hant': '稀有', 'zh-Hans': '稀有', ja: 'レア', es: 'Raro' }, legendary: { 'zh-Hant': '傳奇', 'zh-Hans': '传奇', ja: 'レジェンダリー', es: 'Legendario' },
  immortal: { 'zh-Hant': '不朽', 'zh-Hans': '不朽', ja: 'イモータル', es: 'Inmortal' }, arcana: { 'zh-Hant': '至寶', 'zh-Hans': '至宝', ja: '至宝', es: 'Tesoro' },
  beyond: { 'zh-Hant': '超凡', 'zh-Hans': '超凡', ja: '超凡', es: 'Trascendente' }, celestial: { 'zh-Hant': '天界', 'zh-Hans': '天界', ja: 'セレスティアル', es: 'Celestial' },
  divine: { 'zh-Hant': '神聖', 'zh-Hans': '神圣', ja: 'ディヴァイン', es: 'Divino' }, cosmic: { 'zh-Hant': '宇宙', 'zh-Hans': '宇宙', ja: 'コズミック', es: 'Cósmico' },
};
let byEn = {};
async function loadNames() {
  try {
    const nm = await jget(`https://raw.githubusercontent.com/${REPO}/main/DpsMeter/item_names.json`, { tries: 3, label: 'item_names' });
    byEn = {}; for (const k in nm) { const e = nm[k]['en-US']; if (e && !byEn[e]) byEn[e] = nm[k]; }
    log(`item_names: ${Object.keys(byEn).length}`);
  } catch (e) { log('item_names load failed:', e.message); }
}
function localized(hash) {
  const d = byEn[hash];
  if (d) return { 'zh-Hant': d['zh-Hant'], 'zh-Hans': d['zh-Hans'], ja: d['ja-JP'], es: d['es-ES'] };
  const m = /^(.+) \(([^)]+)\) (\S+)$/.exec(hash);
  if (m) { const base = byEn[m[1]], rar = RARITY[m[2].toLowerCase()], suf = m[3]; if (base && rar) return { 'zh-Hant': base['zh-Hant'] + ' (' + rar['zh-Hant'] + ') ' + suf, 'zh-Hans': base['zh-Hans'] + ' (' + rar['zh-Hans'] + ') ' + suf, ja: base['ja-JP'] + ' (' + rar.ja + ') ' + suf, es: base['es-ES'] + ' (' + rar.es + ') ' + suf }; }
  return null;
}

// ---------- helpers ----------
function slug(s) { let h = 5381; for (let i = 0; i < s.length; i++) h = ((h << 5) + h + s.charCodeAt(i)) >>> 0; return h.toString(36); }
function clampSpike(v) { const s = v.slice().sort((a, b) => a - b); const cap = s[Math.floor(s.length * 0.95)] || s[s.length - 1] || 1; return v.map((x) => Math.min(x, cap)); }
function sparkOf(hist) { if (!hist || hist.length < 2) return null; const v = clampSpike(hist.map((p) => p[1])); const out = []; const N = 24; for (let i = 0; i < N; i++) out.push(v[Math.round(i / (N - 1) * (v.length - 1))]); return out; }

// ---------- state ----------
const DAY = 86400000, HISTORY_DAYS = 8, SAMPLE_GAP_MS = 25 * 60 * 1000, HIST_WINDOW_MS = 7 * DAY;
let history = { items: {}, vol: { at: 0, items: {} } };
let catalog = {};   // hash -> sweep info (price/qty/disp/color/icon/type)
let backfilledOnce = new Set();

async function loadState() {
  try {
    const j = await jget(`https://raw.githubusercontent.com/${REPO}/data/history.json?cb=${Date.now()}`, { tries: 3, label: 'history' });
    if (j && typeof j === 'object') history = j;
    if (!history.items || Array.isArray(history.items)) history.items = {};
    if (!history.vol) history.vol = { at: 0, items: {} };
    log(`state: ${Object.keys(history.items).length} hist series, ${Object.keys(history.vol.items).length} vol entries`);
  } catch (e) { log('no prior state:', e.message); }
}

// ---------- build prices.json + market.json + detail, then publish ----------
async function buildAndPublish() {
  const names = Object.keys(catalog);
  if (names.length < 50) { log(`skip publish: only ${names.length} items swept`); return; }
  const now = Date.now();
  const cutoff = now - HISTORY_DAYS * DAY;

  // price history forward points + first-sight backfill (gentle, capped per cycle)
  let backfills = 0;
  for (const name of names) {
    let series = Array.isArray(history.items[name]) ? history.items[name] : null;
    if ((!series || series.length < 4) && !backfilledOnce.has(name) && backfills < 30) {
      backfilledOnce.add(name);
      const seed = await tbhBackfill(name);
      if (seed && seed.length) { series = seed; backfills++; await sleep(300); }
    }
    if (!series) series = [];
    const last = series[series.length - 1];
    const v = catalog[name];
    const vol = (history.vol.items[name] || {}).vol || 0;
    if (!last || now - last[0] >= SAMPLE_GAP_MS) series.push([now, v.lowestCents, vol]);
    history.items[name] = series.filter((p) => p[0] >= cutoff);
  }

  // assemble prices.json items (price/listings from sweep, vol/median from rotation store, hist + prevCents)
  const items = {};
  for (const name of names) {
    const c = catalog[name];
    const vr = history.vol.items[name] || {};
    const series = history.items[name] || [];
    let ref = null, rd = Infinity;
    for (const p of series) { const d = Math.abs(p[0] - (now - DAY)); if (d < rd) { rd = d; ref = p; } }
    const win = series.filter((p) => p[0] >= now - HIST_WINDOW_MS);
    items[name] = {
      lowestCents: c.lowestCents, qty: c.qty, dispName: c.dispName, color: c.color, icon: c.icon, type: c.type,
      vol: vr.vol || 0, medianCents: vr.medianCents || 0,
      prevCents: (ref && rd <= 18 * 3600 * 1000) ? ref[1] : undefined,
      hist: win.length ? win.map((p) => [Math.floor(p[0] / 1000), p[1], p[2] || 0]) : undefined,
    };
  }

  // market.json list + detail (orderbooks from tbh-market for top-N by volume)
  const list = Object.keys(items).map((hash) => {
    const v = items[hash];
    const prev = (v.prevCents != null && v.prevCents > 0) ? v.prevCents : null;
    return { hash, slug: slug(hash), name: v.dispName || hash, names: localized(hash) || undefined, color: v.color || null, icon: v.icon || null, type: v.type || null, price: v.lowestCents || 0, median: v.medianCents || 0, listings: v.qty || 0, vol: v.vol || 0, chg: prev ? Math.round((v.lowestCents - prev) / prev * 1000) / 10 : null, spark: sparkOf(v.hist) };
  });

  const pub = `${tmpdir()}/tbh-pub`;
  await rm(pub, { recursive: true, force: true });
  await mkdir(pub + '/detail', { recursive: true });
  const byVol = list.slice().sort((a, b) => b.vol - a.vol).slice(0, MAX_DETAIL);
  let obDone = 0;
  for (const it of byVol) {
    const v = items[it.hash];
    const h = (v.hist || []).slice();
    const nowSec = Math.floor(now / 1000);
    if (!h.length || h[h.length - 1][1] !== v.lowestCents) h.push([nowSec, v.lowestCents, v.vol || 0]);
    let ob = null;
    try { ob = await jget(TBH_OB + encodeURIComponent(it.hash), { tries: 2, label: 'ob' }); } catch { /* ok */ }
    if (ob) obDone++;
    await writeFile(`${pub}/detail/${it.slug}.json`, JSON.stringify({ hash: it.hash, slug: it.slug, builtAt: now, hist: h, orderbook: ob }));
    await sleep(120);
  }

  const prices = { cachedAt: now, appid: APPID, currency: '$', count: Object.keys(items).length, items };
  const market = { builtAt: now, total: list.length, count: list.length, currency: '$', list };
  await writeFile(`${pub}/prices.json`, JSON.stringify(prices));
  await writeFile(`${pub}/history.json`, JSON.stringify(history));
  await writeFile(`${pub}/market.json`, JSON.stringify(market));

  await publish(pub);
  log(`published: ${list.length} items, ${list.filter((x) => x.vol > 0).length} with vol, ${obDone}/${byVol.length} orderbooks, ${backfills} backfilled`);
}

// ---------- git push (orphan force-push, single commit, via deploy key over SSH) ----------
async function git(args, cwd) { return execFileP('git', args, { cwd, env: process.env }); }
async function publish(pub) {
  const ts = new Date().toISOString();
  await git(['init', '-q'], pub);
  await git(['checkout', '-q', '--orphan', 'data'], pub);
  await git(['config', 'user.name', 'zeabur-pipeline'], pub);
  await git(['config', 'user.email', 'pipeline@users.noreply.github.com'], pub);
  await git(['add', '-A'], pub);
  await git(['commit', '-q', '-m', `data ${ts} (zeabur pipeline)`], pub);
  await git(['push', '-q', '--force', `git@github.com:${REPO}.git`, 'HEAD:data'], pub);
}

// ---------- ssh deploy key setup ----------
function setupSsh() {
  const b64 = process.env.DEPLOY_KEY_B64;
  if (!b64) { log('WARN: no DEPLOY_KEY_B64 — pushes will fail'); return; }
  const ssh = `${homedir()}/.ssh`;
  mkdirSync(ssh, { recursive: true });
  writeFileSync(`${ssh}/id_ed25519`, Buffer.from(b64, 'base64').toString('utf8'), { mode: 0o600 });
  writeFileSync(`${ssh}/known_hosts`, 'github.com ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl\n');
  process.env.GIT_SSH_COMMAND = `ssh -i ${ssh}/id_ed25519 -o UserKnownHostsFile=${ssh}/known_hosts -o StrictHostKeyChecking=yes`;
  log('ssh deploy key installed');
}

// ---------- loops ----------
async function priceLoop() {
  for (;;) {
    try {
      const sw = await sweepCatalog();
      catalog = sw.items;
      log(`swept ${Object.keys(catalog).length}/${sw.total} items`);
      await buildAndPublish();
    } catch (e) { log('priceLoop error:', e.message); }
    await sleep(PRICE_INTERVAL_MS);
  }
}
async function volumeLoop() {
  // wait until the first sweep gives us a catalog
  while (Object.keys(catalog).length === 0) await sleep(2000);
  for (;;) {
    try {
      const names = Object.keys(catalog);
      // stalest first (oldest `at`, never-fetched = 0)
      let pick = null, oldest = Infinity;
      for (const n of names) { const at = (history.vol.items[n] || {}).at || 0; if (at < oldest) { oldest = at; pick = n; } }
      if (pick) {
        const pj = await priceoverview(pick);
        const now = Date.now();
        if (pj && pj.success) {
          const vRaw = (pj.volume || '').replace(/[^0-9]/g, '');
          const mRaw = (pj.median_price || '').replace(/[^0-9.]/g, '');
          history.vol.items[pick] = { vol: vRaw ? parseInt(vRaw, 10) : 0, medianCents: mRaw ? Math.round(parseFloat(mRaw) * 100) : 0, at: now };
        } else if (pj && !pj.success) {
          const prev = history.vol.items[pick] || {};
          history.vol.items[pick] = { vol: 0, medianCents: prev.medianCents || 0, at: now };
        } else {
          // unreachable: stamp `at` so rotation advances, keep last-good value
          const prev = history.vol.items[pick] || {};
          history.vol.items[pick] = { vol: prev.vol || 0, medianCents: prev.medianCents || 0, at: now };
        }
        history.vol.at = now;
      }
    } catch (e) { log('volumeLoop error:', e.message); }
    await sleep(VOL_INTERVAL_MS);
  }
}

// ---------- main ----------
(async () => {
  log(`TBH zeabur pipeline starting — price every ${PRICE_INTERVAL_MS / 1000}s, volume every ${VOL_INTERVAL_MS / 1000}s`);
  setupSsh();
  await loadNames();
  await loadState();
  priceLoop();
  volumeLoop();
})();
