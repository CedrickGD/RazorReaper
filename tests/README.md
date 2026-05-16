# Tests

Tests for RazorReaper. Two layers:

| Layer | Location | What it covers | How to run |
|---|---|---|---|
| **Runtime tests** | `RazorReaper/wwwroot/_tests/` | JS / DOM behavior inside the WebView2 (e.g. navbar drag, flyout positioning) | Auto-runs on app launch when enabled in `test-config.json` |
| **Unit tests** (future) | `tests/RazorReaper.UnitTests/` | C# services in isolation (ProcessService, ArkPathProvider, etc.) | `dotnet test` |

## Runtime tests

Live with the app because they need a real WebView2 to verify DOM/JS behavior — things you can't test from C# alone (drag handles, scroll containers, flyout positioning, keyboard shortcuts, etc.).

### Toggle them

Edit [`RazorReaper/wwwroot/_tests/test-config.json`](../RazorReaper/wwwroot/_tests/test-config.json):

```json
{
  "enabled": true,            // master switch
  "tests": {
    "navbar-drag": true,      // individual test toggles
    "navbar-collapse": false
  }
}
```

When `enabled: true`, the runner waits ~2 s for Blazor to render, then executes every test whose flag is `true`. Results are:

- printed to the WebView2 dev tools console (`console.log`), AND
- appended to `%LOCALAPPDATA%\RazorReaper\Logs\test-results.log` via a `[JSInvokable]` bridge.

### Adding a test

1. Add a new test inside [`RazorReaper/wwwroot/_tests/test-runner.js`](../RazorReaper/wwwroot/_tests/test-runner.js) — register it in the `TESTS` registry.
2. Add a corresponding flag in `test-config.json`.

A test is an async function returning `{ name, pass: boolean, reason: string }`.

### Shipping

`test-config.json` is committed with `enabled: false` so end-user installs never run them. Devs flip it on locally.

## Unit tests (planned)

A `tests/RazorReaper.UnitTests/` xUnit project covering services that don't need a WebView2. Will be added to the solution so `dotnet test` discovers it.
