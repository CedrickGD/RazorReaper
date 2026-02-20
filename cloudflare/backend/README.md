# RazorReaper Telemetry Worker

Cloudflare Worker backend for RazorReaper telemetry ingestion and admin analytics endpoints.

## Prerequisites

- Node.js 20+
- Cloudflare account with Workers + D1 enabled
- Wrangler auth (`wrangler login` or `CLOUDFLARE_API_TOKEN`)

## Install

```powershell
cd cloudflare/backend
npm ci
```

## Create D1 database (first-time setup)

```powershell
npx wrangler d1 create razorreaper-telemetry-prod
```

Copy the returned `database_id` into `wrangler.jsonc` under `d1_databases[0].database_id`.

## Apply migrations

```powershell
npm run cf:d1:migrate:remote
```

## Configure secrets

```powershell
npx wrangler secret put APP_SHARED_KEY
npx wrangler secret put INSTALL_ID_PEPPER
npx wrangler secret put ADMIN_API_KEY
```

`APP_SHARED_KEY` must match `Telemetry:AppKey` in the app config.

## Run locally

```powershell
npm run dev
```

## Deploy

```powershell
npm run deploy
```

After deploy, copy the worker URL:

`https://razorreaper-telemetry-backend.<your-workers-subdomain>.workers.dev/v1/telemetry/event`

Use that URL in `RazorReaper/appsettings.local.json` for `Telemetry:Endpoint`.
