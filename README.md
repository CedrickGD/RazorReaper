# RazorReaper

RazorReaper is a .NET MAUI Blazor Hybrid desktop app for ARK utility workflows.

## Repo layout

- `RazorReaper/`: main app project
- `cloudflare/backend/`: Cloudflare Worker telemetry backend (Wrangler + D1)

## Run the app

```powershell
dotnet run --project RazorReaper/RazorReaper.csproj -f net9.0-windows10.0.19041.0
```

## Telemetry backend ownership

This repo now owns the telemetry backend source.

Worker project path:

`cloudflare/backend`

Setup/deploy docs:

`cloudflare/backend/README.md`

Convenience script:

`scripts/telemetry-backend.ps1` (actions: `install`, `test`, `dev`, `deploy`, `whoami`, `migrate-local`, `migrate-remote`)

## App telemetry config

Base config in source control:

`RazorReaper/appsettings.json`

Local override (not committed):

`RazorReaper/appsettings.local.json`

Quick start:

```powershell
Copy-Item RazorReaper/appsettings.local.example.json RazorReaper/appsettings.local.json
```

Then set:

- `Telemetry:Endpoint` to your deployed Worker URL (`/v1/telemetry/event`)
- `Telemetry:AppKey` to match worker secret `APP_SHARED_KEY`
