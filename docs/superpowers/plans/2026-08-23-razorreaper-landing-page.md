# RazorReaper Landing Page Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish a fast, bilingual, conversion-focused RazorReaper landing page at `www.razorreaper.app` with private Shop/Free CTA measurement in Cloudflare.

**Architecture:** A standalone static Vite site in `landing/` is deployed as a Cloudflare Pages project. Its public CTAs point to Pages Functions at `/go/shop` and `/go/free`; those functions asynchronously write aggregate event labels to a Workers Analytics Engine binding, then issue fixed redirects to the existing Shop and installer URLs. Cloudflare Pages hosts `www`; the existing root-domain SellHub checkout is untouched.

**Tech Stack:** Vite, vanilla TypeScript, CSS custom properties, Vitest, Cloudflare Pages Functions, Workers Analytics Engine, GitHub Actions, Cloudflare Pages.

**Spec:** `docs/superpowers/specs/2026-08-23-razorreaper-landing-design.md`

## Global Constraints

- Production hostname is exactly `www.razorreaper.app`; `razorreaper.app` must remain a DNS-only SellHub target.
- Do not alter existing `api`, `bot`, `dl`, `media`, or `origin` hostnames, NAS tunnel configuration, app API, bot, installer, or shop checkout.
- English is the default public copy; German is a complete switchable translation.
- Dark is the default theme and the chosen theme/language persist locally.
- Only documented, real RazorReaper features may appear in copy.
- No fabricated testimonials, review quotes, sales figures, user counts, or public counters.
- CTA targets are fixed: Shop `https://razorreaper.app`, Free Version `https://dl.razorreaper.app`.
- Analytics records only the aggregate event label `shop_click` or `free_click`; it must not block redirects.
- Meet keyboard access, responsive design, and reduced-motion requirements.

---

## File structure

| Path | Responsibility |
| --- | --- |
| `landing/package.json` | Node scripts and development dependencies. |
| `landing/vite.config.ts` | Static build and Vitest configuration. |
| `landing/index.html` | Document metadata, root shell, font/preload hints. |
| `landing/src/main.ts` | Page bootstrap, theme/language state wiring. |
| `landing/src/content.ts` | Typed English/German copy and real feature data. |
| `landing/src/state.ts` | `LandingState`, local persistence, DOM state application. |
| `landing/src/render.ts` | Semantic section rendering and CTA markup. |
| `landing/src/styles.css` | Responsive RazorReaper visual system and both themes. |
| `landing/public/images/rr-logo.png` | Reused official RazorReaper logo. |
| `landing/public/images/app-dashboard.png` | Approved real RazorReaper desktop screenshot. |
| `landing/public/images/ark-hero.webp` | Licensed/owned ARK ambience image with an asset-source note. |
| `landing/functions/go/[destination].ts` | Fixed Shop/Free redirect + non-blocking aggregate Analytics Engine event. |
| `landing/wrangler.toml` | Pages project metadata and Analytics Engine binding declaration. |
| `landing/tests/state.test.ts` | Theme and language persistence unit tests. |
| `landing/tests/render.test.ts` | Copy/CTA/semantics rendering tests. |
| `landing/tests/redirect.test.ts` | Redirect allow-list and analytics-write behavior tests. |
| `.github/workflows/landing.yml` | Landing lint/test/build verification on pushes and pull requests. |
| `docs/landing-page-operations.md` | Deployment, DNS, dashboard, and later-review-operation runbook. |

## Task 1: Scaffold the isolated landing package and automated checks

**Files:**
- Create: `landing/package.json`
- Create: `landing/tsconfig.json`
- Create: `landing/vite.config.ts`
- Create: `landing/index.html`
- Create: `landing/src/main.ts`
- Create: `landing/src/styles.css`
- Create: `landing/tests/smoke.test.ts`
- Create: `.github/workflows/landing.yml`

**Interfaces:**
- Produces: `npm run dev`, `npm run build`, `npm run test`, and a static `dist/` directory for Cloudflare Pages.
- Consumes: no product-runtime code; it must remain deployable without the MAUI application.

- [ ] **Step 1: Write the failing package smoke test**

```ts
import { describe, expect, it } from "vitest";

describe("landing package", () => {
  it("exposes the RazorReaper app root", () => {
    document.body.innerHTML = '<main id="app"></main>';
    expect(document.querySelector("#app")).not.toBeNull();
  });
});
```

- [ ] **Step 2: Run the test to verify the package does not exist yet**

Run: `cd landing && npm test -- --run tests/smoke.test.ts`

Expected: FAIL because `landing/package.json` and the Vitest configuration do not exist.

- [ ] **Step 3: Create the minimal Vite/TypeScript/Vitest package**

```json
{
  "name": "razorreaper-landing",
  "private": true,
  "type": "module",
  "scripts": {
    "dev": "vite",
    "build": "tsc --noEmit && vite build",
    "test": "vitest"
  }
}
```

Configure Vite to use `src/main.ts`, Vitest with `jsdom`, and `dist` as the build output. Add an `index.html` containing only `<main id="app"></main>` and an entry script to `src/main.ts`. Add an initial import of `styles.css` in `main.ts`.

- [ ] **Step 4: Add the GitHub Actions verification workflow**

```yaml
name: Landing
on:
  pull_request:
    paths: ["landing/**", ".github/workflows/landing.yml"]
  push:
    paths: ["landing/**", ".github/workflows/landing.yml"]
jobs:
  verify:
    runs-on: ubuntu-latest
    defaults:
      run:
        working-directory: landing
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-node@v4
        with: { node-version: 22, cache: npm, cache-dependency-path: landing/package-lock.json }
      - run: npm ci
      - run: npm run test -- --run
      - run: npm run build
```

- [ ] **Step 5: Run the package checks**

Run: `cd landing && npm ci && npm run test -- --run && npm run build`

Expected: PASS, and `landing/dist/index.html` exists.

- [ ] **Step 6: Commit**

```bash
git add landing .github/workflows/landing.yml
git commit -m "feat: scaffold RazorReaper landing site"
```

## Task 2: Implement typed bilingual content and persisted display state

**Files:**
- Create: `landing/src/content.ts`
- Create: `landing/src/state.ts`
- Create: `landing/tests/state.test.ts`

**Interfaces:**
- Produces: `Locale = "en" | "de"`, `Theme = "dark" | "light"`, `LandingState`, `readState(storage)`, `writeState(state, storage)`, and `copyFor(locale)`.
- Consumes: `localStorage` through the injected `Storage` interface; render code must never hard-code public strings.

- [ ] **Step 1: Write failing persistence tests**

```ts
import { describe, expect, it } from "vitest";
import { readState, writeState } from "../src/state";

describe("landing display state", () => {
  it("defaults to dark English", () => {
    expect(readState(localStorage)).toEqual({ theme: "dark", locale: "en" });
  });

  it("round-trips a chosen light German state", () => {
    writeState({ theme: "light", locale: "de" }, localStorage);
    expect(readState(localStorage)).toEqual({ theme: "light", locale: "de" });
  });
});
```

- [ ] **Step 2: Run the state tests to verify they fail**

Run: `cd landing && npm run test -- --run tests/state.test.ts`

Expected: FAIL because `state.ts` does not exist.

- [ ] **Step 3: Implement state and real product copy**

Use exactly the keys `rr.landing.theme` and `rr.landing.locale`. Reject malformed stored values and fall back to `{ theme: "dark", locale: "en" }`. In `content.ts`, provide matching `en` and `de` records for: hero title/body, Shop CTA, Free CTA, platform note, three feature-group headings/bodies, three how-it-works steps, free-version block, footer labels, and legal disclaimer. Source every claim from `README.md`; use the terms `35+ tools`, `Steam ARK: Survival Evolved`, `Windows 10/11`, and `self-contained installer` accurately.

- [ ] **Step 4: Run the state tests**

Run: `cd landing && npm run test -- --run tests/state.test.ts`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add landing/src/content.ts landing/src/state.ts landing/tests/state.test.ts
git commit -m "feat: add landing copy and display state"
```

## Task 3: Render the conversion page and responsive RazorReaper visual system

**Files:**
- Create: `landing/src/render.ts`
- Modify: `landing/src/main.ts`
- Modify: `landing/src/styles.css`
- Create: `landing/public/images/rr-logo.png`
- Create: `landing/public/images/app-dashboard.png`
- Create: `landing/public/images/ark-hero.webp`
- Create: `landing/public/images/SOURCES.md`
- Create: `landing/tests/render.test.ts`

**Interfaces:**
- Consumes: `copyFor(locale)`, `LandingState`, `/go/shop`, `/go/free`.
- Produces: `renderLanding(root, state, onStateChange)` and semantic header, hero, features, how-it-works, free block, and footer.

- [ ] **Step 1: Write failing render tests**

```ts
import { describe, expect, it } from "vitest";
import { renderLanding } from "../src/render";

describe("landing render", () => {
  it("renders fixed Shop and Free CTA routes", () => {
    const root = document.createElement("main");
    renderLanding(root, { theme: "dark", locale: "en" }, () => undefined);
    expect(root.querySelector('a[href="/go/shop"]')).not.toBeNull();
    expect(root.querySelector('a[href="/go/free"]')).not.toBeNull();
  });

  it("renders language and theme controls with accessible labels", () => {
    const root = document.createElement("main");
    renderLanding(root, { theme: "dark", locale: "en" }, () => undefined);
    expect(root.querySelector('[aria-label="Switch language"]')).not.toBeNull();
    expect(root.querySelector('[aria-label="Switch theme"]')).not.toBeNull();
  });
});
```

- [ ] **Step 2: Run the render tests to verify they fail**

Run: `cd landing && npm run test -- --run tests/render.test.ts`

Expected: FAIL because `render.ts` does not exist.

- [ ] **Step 3: Add approved product assets**

Copy the existing `RazorReaper/wwwroot/images/RRlogo.png` into the landing public image directory. Capture one current RazorReaper dashboard screenshot from the signed release or a local Release build and save it as `app-dashboard.png`; do not use a mock interface. Add one owned/licensed ARK ambience image as `ark-hero.webp`. Record each file's source, license/ownership, and capture date in `SOURCES.md`.

- [ ] **Step 4: Implement semantic sections and interactions**

Render one `<header>`, one `<main>`, and one `<footer>`. Use `<button>` controls for language/theme. Apply `data-theme` and `lang` to `<html>` on every state change, call `writeState`, and rerender translated copy. Use fixed CTA anchors only; do not put raw Shop/Download URLs in page markup. Include the supported-platform statement next to the Hero CTAs and the independent-project disclaimer in the footer.

- [ ] **Step 5: Implement the CSS system**

Define dark and light token sets with CSS custom properties. Use dark as the no-JavaScript default. Create a responsive grid with the product screenshot as the primary hero visual and the ARK image only as a low-contrast backdrop. Add `@media (prefers-reduced-motion: reduce)` to remove decorative transitions. Ensure the layout remains usable at 320px and keyboard focus is visible in both themes.

- [ ] **Step 6: Run page checks and manually verify both visual modes**

Run: `cd landing && npm run test -- --run && npm run build && npm run dev -- --host 127.0.0.1`

Expected: all tests PASS; verify EN/DE, dark/light, keyboard tab order, 320px mobile layout, and desktop layout in a browser.

- [ ] **Step 7: Commit**

```bash
git add landing
git commit -m "feat: build RazorReaper conversion landing page"
```

## Task 4: Add fixed conversion redirect functions and aggregate event writes

**Files:**
- Create: `landing/functions/go/[destination].ts`
- Create: `landing/wrangler.toml`
- Create: `landing/tests/redirect.test.ts`
- Modify: `landing/package.json`

**Interfaces:**
- Consumes: `destination` route parameter and `context.env.LANDING_EVENTS` Analytics Engine binding.
- Produces: `onRequestGet(context): Response` with `302` redirects only to `https://razorreaper.app` and `https://dl.razorreaper.app`.
- Event contract: `indexes: ["shop_click" | "free_click"]`, `doubles: [1]`, no personal identifiers or request-derived dimensions.

- [ ] **Step 1: Write failing redirect tests**

```ts
import { describe, expect, it, vi } from "vitest";
import { onRequestGet } from "../functions/go/[destination]";

it("records shop_click and redirects to the fixed shop URL", async () => {
  const writeDataPoint = vi.fn();
  const response = await onRequestGet({
    params: { destination: "shop" },
    env: { LANDING_EVENTS: { writeDataPoint } }
  } as never);
  expect(response.status).toBe(302);
  expect(response.headers.get("location")).toBe("https://razorreaper.app");
  expect(writeDataPoint).toHaveBeenCalledWith({ indexes: ["shop_click"], doubles: [1] });
});

it("rejects every destination outside the fixed allow-list", async () => {
  const response = await onRequestGet({ params: { destination: "https://example.invalid" }, env: {} } as never);
  expect(response.status).toBe(404);
});
```

- [ ] **Step 2: Run the redirect tests to verify they fail**

Run: `cd landing && npm run test -- --run tests/redirect.test.ts`

Expected: FAIL because the Pages Function does not exist.

- [ ] **Step 3: Implement the allow-listed Pages Function**

Create a constant map:

```ts
const DESTINATIONS = {
  shop: { event: "shop_click", target: "https://razorreaper.app" },
  free: { event: "free_click", target: "https://dl.razorreaper.app" }
} as const;
```

For an allowed route, call `context.waitUntil(Promise.resolve(env.LANDING_EVENTS.writeDataPoint({ indexes: [event], doubles: [1] })).catch(() => undefined))`; return `Response.redirect(target, 302)` immediately. For an unknown route, return `new Response("Not found", { status: 404 })`. Do not include URLs, IP addresses, user agents, referrers, or client identifiers in events.

- [ ] **Step 4: Declare the Analytics Engine binding**

```toml
name = "razorreaper-landing"
pages_build_output_dir = "dist"

[[analytics_engine_datasets]]
binding = "LANDING_EVENTS"
dataset = "razorreaper_landing_conversions"
```

- [ ] **Step 5: Run redirect and full checks**

Run: `cd landing && npm run test -- --run && npm run build`

Expected: PASS. Confirm direct GETs to `/go/shop` and `/go/free` issue 302s when the binding write rejects or is unavailable in the test harness.

- [ ] **Step 6: Commit**

```bash
git add landing/functions/go/[destination].ts landing/wrangler.toml landing/tests/redirect.test.ts landing/package.json
git commit -m "feat: measure landing conversion redirects"
```

## Task 5: Deploy to Cloudflare Pages and document operations

**Files:**
- Modify: `landing/wrangler.toml`
- Create: `docs/landing-page-operations.md`
- Modify: `README.md`

**Interfaces:**
- Consumes: tested `landing/dist`, Pages project `razorreaper-landing`, custom domain `www.razorreaper.app`, Analytics Engine dataset `razorreaper_landing_conversions`.
- Produces: public `https://www.razorreaper.app` and private Cloudflare analytics instructions for the two event labels.

- [ ] **Step 1: Write the deployment acceptance checklist before live changes**

Add the following exact checks to `docs/landing-page-operations.md`:

```text
1. razorreaper.app still opens the SellHub shop.
2. www.razorreaper.app loads the Pages landing page over HTTPS.
3. /go/shop redirects to https://razorreaper.app.
4. /go/free redirects to https://dl.razorreaper.app.
5. A request to /go/anything-else returns 404.
6. Both dark/light and EN/DE choices survive a reload.
7. Workers Analytics Engine shows shop_click and free_click aggregate rows after test clicks.
```

- [ ] **Step 2: Create the Pages project and connect the production branch**

In Cloudflare Pages, create `razorreaper-landing` from this repository with `landing` as the root directory, `npm run build` as the build command, and `dist` as the build output. Bind `LANDING_EVENTS` to the `razorreaper_landing_conversions` Analytics Engine dataset in production and preview. Connect production to `master`; keep preview deployments enabled for pull requests.

- [ ] **Step 3: Attach only the www custom hostname**

Add `www.razorreaper.app` to the Pages project. Confirm Cloudflare creates/uses the proxied Pages DNS target for `www`. Do not edit the existing root `razorreaper.app` CNAME to `domains.sellhub.cx`.

- [ ] **Step 4: Create the private Cloudflare reporting view**

Use the Cloudflare Analytics Engine dataset to run the exact aggregate query:

```sql
SELECT
  index1 AS event,
  SUM(_sample_interval) AS clicks
FROM razorreaper_landing_conversions
WHERE timestamp > NOW() - INTERVAL '30' DAY
GROUP BY event
ORDER BY event
```

Save the query/instructions in `docs/landing-page-operations.md` and add a Cloudflare custom dashboard stat or request-path view when the dashboard exposes the selected dataset. If the dashboard cannot directly visualize Analytics Engine rows, retain the SQL query in the Cloudflare Analytics Engine UI; do not add an external analytics vendor or expose a public reporting page.

- [ ] **Step 5: Execute the acceptance checklist**

Run the seven checks documented in Step 1 from desktop and mobile browser viewports. Use one test click per CTA, then confirm the aggregate counts are visible privately. Verify root shop behavior again after the `www` hostname is live.

- [ ] **Step 6: Update repository links and commit**

Add `Website: https://www.razorreaper.app` to the README's top link block and add a short operations-link note. Then commit:

```bash
git add README.md docs/landing-page-operations.md landing/wrangler.toml
git commit -m "docs: publish RazorReaper landing operations"
```

## Plan self-review

- Spec coverage: Tasks 1-3 deliver the static bilingual themeable page, Task 4 delivers fixed non-blocking aggregate Shop/Free instrumentation, and Task 5 delivers Cloudflare Pages `www` hosting, private reporting, and live validation. Existing root shop and NAS/Tunnel hostnames are explicitly protected in all deployment steps.
- Completeness scan: every listed task contains concrete files, commands, expected checks, and commit boundaries.
- Interface consistency: both render and Pages Function tasks use the same `/go/shop` and `/go/free` routes; Analytics Engine uses the same `LANDING_EVENTS` binding and `razorreaper_landing_conversions` dataset throughout.
