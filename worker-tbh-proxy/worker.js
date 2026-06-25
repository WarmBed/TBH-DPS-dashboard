// Thin egress proxy so the GitHub Actions cron (datacenter IP, which tbh-market 403s, and which Steam
// heavily rate-limits) can reach both upstreams through Cloudflare (not blocked / not throttled).
//   /api/*        -> tbh-market.com (order book)
//   /steam/search -> Steam Community Market search/render  (catalog sweep; CF egress isn't throttled)
//   /steam/price  -> Steam Community Market priceoverview  (24h volume + median; CF egress isn't throttled)
// Requires a shared secret (x-proxy-key) so it isn't an open proxy.
const STEAM_UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (env.PROXY_KEY && request.headers.get("x-proxy-key") !== env.PROXY_KEY)
      return new Response("forbidden", { status: 403 });

    // Steam market endpoints. Pass the query string through verbatim; surface Steam's real status
    // (incl. 429) so the cron can retry. No edge cache — these are polled fresh each run.
    if (url.pathname === "/steam/search" || url.pathname === "/steam/price") {
      const upstream = url.pathname === "/steam/search"
        ? "https://steamcommunity.com/market/search/render/"
        : "https://steamcommunity.com/market/priceoverview/";
      const sr = await fetch(upstream + url.search, { headers: { "User-Agent": STEAM_UA }, cf: { cacheTtl: 0, cacheEverything: false } });
      return new Response(sr.body, {
        status: sr.status,
        headers: { "content-type": sr.headers.get("content-type") || "application/json", "access-control-allow-origin": "*" },
      });
    }

    if (!url.pathname.startsWith("/api/"))
      return new Response("not found", { status: 404 });
    const target = "https://tbh-market.com" + url.pathname + url.search;
    const r = await fetch(target, {
      headers: { "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36", "Accept": "application/json,*/*" },
      cf: { cacheTtl: 60, cacheEverything: true },
    });
    return new Response(r.body, {
      status: r.status,
      headers: { "content-type": r.headers.get("content-type") || "application/json", "access-control-allow-origin": "*" },
    });
  }
}
