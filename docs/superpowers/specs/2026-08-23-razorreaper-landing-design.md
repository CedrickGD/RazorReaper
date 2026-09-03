# RazorReaper marketing landing page design

**Date:** 2026-08-23  
**Status:** Approved concept; ready for implementation planning

## Purpose

Create a public, conversion-focused landing page for RazorReaper. It must explain the Windows desktop toolkit for ARK: Survival Evolved, provide a clear route to the paid shop and a separate route to the free version, and give the owner private conversion counts without displaying public popularity metrics.

The existing shop at `https://razorreaper.app` remains the commercial checkout. The landing page must not replace, redirect, or otherwise alter that root-domain shop.

## Public address and hosting

- Public landing address: `https://www.razorreaper.app`.
- Hosting: a dedicated Cloudflare Pages project.
- `razorreaper.app` remains DNS-only and points to SellHub.
- `www.razorreaper.app` is a separate Cloudflare Pages custom domain.
- Existing NAS/Tunnel hostnames (`api`, `bot`, `dl`, `media`, `origin`) are out of scope and must remain unchanged.

## Audience and voice

- Audience: Steam ARK: Survival Evolved players on Windows.
- Primary copy: English, direct, player-facing and technically credible; it must not read like a generic corporate SaaS page.
- Secondary copy: German translation with a visible `DE` / `EN` language selector.
- Do not fabricate testimonials, review quotes, user counts, or social proof.
- A future SellHub review flow may add only authentic customer reviews after a separate review of the shop integration.
- Clearly state the real platform boundary: Steam ARK: Survival Evolved on Windows 10/11; no console, ARK: Survival Ascended, or Microsoft Store support.

## Visual direction

- Default theme: dark, with a light-theme switcher; retain the selected theme locally across visits.
- Use RazorReaper’s existing logo and palette as the visual source of truth.
- Hero composition: real RazorReaper UI screenshot in the foreground plus a restrained, darkened ARK/in-game visual as ambience. The product screenshot must remain legible and primary.
- Avoid fake counters, fake testimonial cards, aggressive pop-ups, and content that visually imitates third-party reviews.
- Design priorities: fast first paint, mobile responsiveness, clear type hierarchy, highly visible primary call to action.

## Information architecture

1. **Header**
   - RazorReaper logo/wordmark.
   - Anchor navigation to Features, How it works, and Free version.
   - Theme switch and language switch.
   - Compact Shop call to action.

2. **Hero**
   - Short player-facing headline and proof-oriented supporting copy.
   - App screenshot + ARK ambience.
   - Primary CTA: `Get RazorReaper` -> `https://razorreaper.app`.
   - Secondary CTA: `Try the free version` -> `https://dl.razorreaper.app`.
   - Supported-platform note near the actions.

3. **Feature groups**
   - Config and visual control: INI tools, vision/gamma, skies/loading screens, safe backups/revert paths.
   - Automation and live play: hotkeys, scripts/macros, session HUD, notifier, optional Discord Rich Presence.
   - Intel and utility: breeding, maps/locations, boss/loot knowledge, installed Steam mods, file/system tools.
   - Copy derives only from documented real product features.

4. **How it works**
   - Download/install (self-contained installer).
   - Set Steam ARK path.
   - Tune/play from one searchable toolset.
   - Mention background updates are available and configurable.

5. **Free-version conversion block**
   - Explain that a free trial/entry option is available.
   - Repeat the Free Version and Shop calls to action.

6. **Footer**
   - Link to shop, free download, GitHub, Discord, privacy page, and a concise independent-project disclaimer.

## Conversion measurement

### Visitor behavior

- No visitor-facing counters or population claims.
- CTA clicks must remain fast and result in normal navigation to the current Shop or download destination.

### Owner reporting

- Instrument the Shop and Free Version CTAs separately through fixed Cloudflare-controlled redirect/event routes.
- Store only aggregate event counts required to report `shop_click` and `free_click` totals and time trends.
- Surface the results in a private Cloudflare dashboard or the closest native Cloudflare analytics surface supported by the deployed implementation.
- Do not claim that native Cloudflare Web Analytics provides custom button events; its current documented Web Analytics product does not.
- Design the data path to avoid affecting checkout/download availability if analytics is unavailable.

## Technical implementation

- Static, framework-light website in a dedicated `landing/` package in the RazorReaper repository.
- Deploy through Cloudflare Pages with a GitHub-connected production branch and preview deployments for pull requests.
- Use accessible semantic HTML, reduced-motion support, keyboard-operable toggles, responsive image handling, and no heavyweight third-party analytics scripts.
- Use a small Cloudflare Worker/Pages Function only where needed for fixed CTA measurement and redirect behavior.
- Add automated tests for language/theme persistence, CTA target selection, and redirect event routes.

## Validation and release

- Verify all live URLs, both theme modes, both languages, responsive layout, keyboard navigation, and both CTA destinations.
- Confirm root shop remains available before and after attaching `www`.
- Confirm click counters increment in the private reporting surface without appearing anywhere on the public page.
- Verify the landing page does not affect the existing app API, media delivery, bot, downloads, or NAS tunnel hostnames.

## Explicit non-goals for v1

- No fabricated reviews, testimonials, sales counts, or customer logos.
- No rework of SellHub checkout.
- No change to the root `razorreaper.app` DNS target.
- No remote NAS administration UI.
- No modification of the RazorReaper desktop application itself.
