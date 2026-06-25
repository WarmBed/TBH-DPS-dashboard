# TBH price-feed pipeline (Zeabur persistent service)

Replaces the GitHub Actions `steam-prices` cron + Cloudflare Worker proxy. Runs 24/7 on Zeabur
(Volcano Engine egress reaches Steam + tbh-market + GitHub directly, unthrottled on search).

- **priceLoop** every `PRICE_INTERVAL_MS` (default 300s): deterministic full-catalog sweep (~741) →
  build prices.json + market.json + detail → orphan-force-push to the `data` branch.
- **volumeLoop** every `VOL_INTERVAL_MS` (default 10000ms): one Steam priceoverview for the stalest
  item; 24h volume/median rotates through the whole catalog (~2h/cycle), never tripping rate limits.

## Env vars
- `DEPLOY_KEY_B64` — base64 of an ed25519 private key whose public half is a **write** deploy key on
  this repo (used for `git push` over SSH). Required.
- `VOL_INTERVAL_MS` (default 10000), `PRICE_INTERVAL_MS` (default 300000), `MAX_DETAIL` (default 140),
  `REPO` (default WarmBed/TBH-DPS-dashboard).

## Deploy / manage (zeabur CLI)
    zeabur deploy --service-id <svc> --environment-id <env>          # redeploy after edits
    zeabur deployment log --service-id <svc> --env-id <env> --project-id <proj> -t runtime
    zeabur service restart --id <svc> --env-id <env>
