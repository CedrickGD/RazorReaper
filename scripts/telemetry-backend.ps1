[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)]
	[ValidateSet("install", "test", "dev", "deploy", "whoami", "migrate-local", "migrate-remote")]
	[string]$Action
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$backendDir = Join-Path $repoRoot "cloudflare/backend"

Push-Location $backendDir
try {
	switch ($Action) {
		"install" { npm ci }
		"test" { npx vitest --run }
		"dev" { npx wrangler dev }
		"deploy" { npx wrangler deploy }
		"whoami" { npx wrangler whoami }
		"migrate-local" { npx wrangler d1 migrations apply razorreaper-telemetry-prod --local }
		"migrate-remote" { npx wrangler d1 migrations apply razorreaper-telemetry-prod --remote }
	}
}
finally {
	Pop-Location
}
