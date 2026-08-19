# Razor Reaper Trust, Privacy, and Authorization Design

**Date:** 2026-08-16  
**Status:** Approved scope; implementation pending  
**Workspace:** Existing local `navbar-search-usability-f99f82` worktree only

## 1. Binding constraints

- All application changes stay in the existing local critical worktree.
- Do not create a branch, merge, push, open a PR, publish an installer, or create a public release.
- The current Cloudflare dashboard Viewer mutation behavior is intentionally preserved.
- Discord Rich Presence and the current production Discord entitlement flow are not redesigned by this work.
- Hybrid telemetry/location is approved:
  - optional telemetry requires explicit consent;
  - precise device location requires a second, separate opt-in;
  - coarse country/region fallback is derived at the project-controlled edge;
  - source, precision, measured accuracy, and age are represented honestly.
- The application must be privacy-safe before it is launched to create public marketing screenshots.

## 2. Trust boundaries

### Privacy boundary

No optional analytics request may leave the device while telemetry consent is unknown or denied. The Windows location API may not be invoked until the user separately enables precise location. Revoking either choice takes effect immediately.

Functional license, access, quota, and update traffic is separate from optional telemetry. Analytics denial must not silently break purchased access.

### Content-trust boundary

No downloaded installer or FFmpeg executable may run unless immutable metadata, downloaded bytes, expected origin, and the executable itself pass the configured trust checks.

### Action-authority boundary

Local preferences, cached plan labels, UI locks, and locally stored expiry values are display hints. A governed operation must receive a fresh server decision immediately before its first effectful step.

### Revocation boundary

Suspension or ban state blocks every effectful entry point, including hotkeys and command-palette actions, and cancels already-running automation. Stop, revert, restore, cleanup, privacy settings, support, access re-check, update verification, and application exit remain available.

## 3. Target action flow

```text
UI / global hotkey / command palette
                |
                v
     action-owning application service
                |
       non-mutating local preflight
                |
                v
      server authorization decision
  access + live license + quota + idempotency
                |
          allow / explicit deny
                |
                v
        first local side effect
```

The background access poll drives fast UX updates and cancellation. It is complementary to, not a substitute for, the decision checked at each governed action.

## 4. Identity and preference seams

Create one functional client-identity service independent of telemetry initialization:

```csharp
public sealed record ClientIdentity(string InstallId, string HardwareId);

public interface IClientIdentityService
{
    ClientIdentity GetIdentity();
}
```

Introduce an injectable preference store so consent and identity policy can be tested without MAUI static APIs. Preserve the existing install identifier for compatibility, but treat it as application identity rather than a telemetry-owned credential. Keep `IHwidService` temporarily as an adapter until all consumers have migrated.

The raw license key should be exchanged for a device-bound opaque token and placed in `SecureStorage`; after a successful migration it must be removed from ordinary Preferences. Any remaining cached plan/expiry values are explicitly non-authoritative.

## 5. Consent model

```csharp
public enum ConsentChoice
{
    Unknown,
    Granted,
    Denied
}

public sealed record PrivacyConsentSnapshot(
    ConsentChoice Telemetry,
    bool PreciseLocation,
    int PolicyVersion,
    DateTimeOffset? DecidedAtUtc);
```

Rules:

- Existing installations migrate to `Unknown`, never silently to `Granted`.
- `Telemetry.Enabled` becomes a deployment kill switch; it can disable transport but cannot grant consent.
- `PreciseLocation` defaults to false and cannot be enabled while telemetry is off.
- Starting telemetry while consent is unknown/denied performs no network request.
- Every event re-checks consent immediately before sending.
- Revoking telemetry cancels heartbeat and in-flight work without a final analytics event.
- Revoking precise location cancels acquisition and clears any in-memory precise-location cache.
- The location service returns no cached coordinates while precise consent is disabled.
- Inject `TimeProvider` for deterministic freshness, expiry, consent, access, and update tests.

Remove machine name, raw HWID, process ID, redundant user label, and stored Discord username from optional telemetry. Keep only a pseudonymous install ID, session ID, app/build/platform version, allowlisted event/status, and constrained metrics.

The public client app key is not treated as a secret. A shipped desktop binary cannot protect a shared credential; abuse resistance must come from schema validation, replay controls, rate limits, and later device-bound request signing.

## 6. Hybrid location contract

```csharp
public enum GeoSource
{
    DeviceFused,
    DeviceLastKnown,
    EdgeNetwork
}

public enum GeoPrecision
{
    Coordinate,
    Region,
    Country
}

public sealed record TelemetryLocation(
    GeoSource Source,
    GeoPrecision Precision,
    double? Latitude,
    double? Longitude,
    double? AccuracyMeters,
    DateTimeOffset ObservedAtUtc,
    int? AgeSeconds,
    string? SignalSource,
    string? CountryCode,
    string? RegionCode);
```

Device rules:

- Never call the OS provider without precise opt-in.
- Require finite, in-range coordinates and a non-future observation time.
- Use a hard maximum age of 60 minutes; stale data is omitted, not reused indefinitely.
- Preserve measured accuracy when supplied. Never infer an accuracy radius from a requested OS mode.
- Preserve signal/source information and round coordinates no more precisely than the measurement justifies.
- If accuracy is unavailable, say so; never call a coordinate “exact.”

Edge rules:

- The application sends no fallback coordinates and calls no public IP-geolocation provider.
- The project-controlled Cloudflare ingest may derive country and region from trusted request metadata after a consented telemetry request.
- Edge fallback uses `source=edge_network`, `precision=country|region`, no coordinate, no fabricated accuracy, and no device capture timestamp.
- The admin panel displays source, precision, accuracy, and age. It never renders a region centroid as a precise user pin.

## 7. Trusted updater

Replace unsigned XML authority with a signed v2 manifest verified before deserialization. Pin accepted ES256 public keys and key identifiers in the app; do not accept a response-selected algorithm or remote key URL.

The signed payload contains:

- schema and monotonic release sequence;
- semantic version and minimum supported version;
- publish time and mandatory flag;
- immutable HTTPS artifact URL;
- exact byte size and SHA-256;
- pinned Authenticode publisher public-key hash;
- changelog URL and plain release notes.

Validation order:

1. Fetch only from configured HTTPS manifest origins with timeout and response-size limits.
2. Verify signature and pinned key identifier.
3. Validate schema, version, timestamps, sequence, size, and allowlisted artifact origin.
4. Reject rollback using the highest trusted release sequence.
5. Download to a unique app-owned partial file with exact/max size enforcement.
6. Hash while streaming and compare SHA-256.
7. Validate Authenticode chain and pinned publisher key as defense in depth.
8. Atomically stage the verified artifact.
9. Re-check hash and Authenticode immediately before launch.
10. Launch with a fixed local argument list; never accept remote installer arguments.

Any trust failure leaves the current version running and reports that nothing was changed. An unverified `mandatory` flag has no force. The new client must never fall back to unsigned XML after a signed-manifest error.

Existing public clients require one explicitly documented bridge release through the legacy updater. That bootstrap limitation cannot be repaired retroactively.

## 8. Trusted FFmpeg

Ship an embedded lock document containing one pinned upstream build, immutable mirror URLs for byte-identical content, archive size/hash, exact archive entry, executable size/hash, and the matching license notice.

Rules:

- No `/latest/`, master-latest, or unversioned download URLs.
- Hash the archive before extraction.
- Extract exactly the locked entry; reject traversal, duplicates, encryption, size anomalies, and pathological compression.
- Hash the executable after extraction and before atomic installation.
- Existing cached/bundled binaries are trusted only if size and hash match the lock.
- Re-verify immediately before every process start.
- Prefer a verified executable lease that holds the file between verification and launch.
- A failed integrity check disables only conversion/preview functionality and never launches the file.

## 9. Server-authoritative actions

Introduce `IActionAuthorizationService` with stable request/response schema and one `action_id` generated per user intent. Retries reuse that ID, so the backend returns the same decision and never consumes quota twice.

The server atomically checks:

1. subject identity;
2. suspension/ban;
3. live license and expiry;
4. free or premium tier;
5. quota status and, for free tier, consumption;
6. idempotent decision persistence.

Premium still calls the server. Network error, timeout, non-success status, malformed JSON, schema mismatch, or mismatched action ID denies governed side effects. Status chips may fail softly; authorization may not use their cache.

Stable denial reasons include `suspended`, `banned`, `license_invalid`, `license_expired`, `quota_exhausted`, `client_unsupported`, `request_invalid`, and `authorization_unavailable`.

The desktop cannot cryptographically prevent a modified client from performing a wholly local operation. This architecture makes the official client consistent and the server’s commercial records authoritative; it must not be marketed as undefeatable DRM.

## 10. Service-owned enforcement

Move checks out of Razor components and into the services that own the side effect. For every governed operation, prove:

```text
validate -> authorize -> mutate
deny -> zero mutation
```

Apply the pattern to:

- Sky Changer inject; restore remains unconditional.
- Loading Screen replace/convert-and-replace; restore remains unconditional.
- Font selection; returning to the system default remains unconditional.
- Desync activation before firewall mutation; deactivation remains unconditional.
- Stretched-resolution application before display change; revert remains unconditional.
- Fed Suit start before runner state; stop remains unconditional.
- Shared automation scripts before running state or task creation; stop remains unconditional.

UI buttons, global hotkeys, command palette, and direct service callers then share the same enforcement boundary.

## 11. Central access state and cancellation

Replace the implicit default-allowed boolean with an explicit state:

```csharp
public enum AccessState
{
    Unknown,
    Allowed,
    Suspended,
    Banned,
    Indeterminate
}
```

Persist only server-signed access snapshots with monotonic revisions. A cached ban remains effective offline until a newer signed lift arrives. A timed suspension that expires locally moves to Unknown/Indeterminate and re-checks; it does not fabricate Allowed.

On transition to suspended/banned, a coordinator stops scripts and macros, stops Fed Suit and Auto Antidote, removes an active desync firewall rule, reverts a pending display test, hides feature overlays where safe, and cancels in-flight effectful work.

Discord Rich Presence is not altered by suspension. Updater, access re-check, privacy, support, exit, and safety cleanup remain available.

`AccessBlocked.razor` remains the explanatory UX layer, not the enforcement boundary.

## 12. Minimal privacy UX

First run or first post-migration launch presents an unselected choice explaining:

- shared diagnostics data;
- pseudonymous installation ID;
- approximate country/region inferred at the edge after telemetry consent;
- precise device location being separately off by default.

Buttons: **Share diagnostics**, **Not now**, and **Privacy notice**. Closing or ignoring means Unknown and sends nothing.

Settings gains a Privacy section with:

- Usage & diagnostics toggle;
- Precise device location toggle, disabled while telemetry is off;
- honest current-location disclosure including source, accuracy, and age when applicable;
- Privacy notice link.

The Windows location permission prompt is requested only at the moment precise location is enabled. Denial leaves the switch off.

Update privacy and installer notices to separate functional network traffic, optional telemetry, edge-derived region, separately optional device location, identifiers, retention, and revocation. Continued use is not consent for optional analytics.

## 13. Failure copy

- Update: “Update could not be verified. Your current version was not changed.”
- FFmpeg: “The video converter failed its integrity check and was not run.”
- Authorization: “Couldn’t verify access right now. Nothing was changed.”
- Quota: use authoritative server counts and existing monthly-limit language.
- Suspension: preserve reason, expiry where present, and Re-check.
- Unknown startup: say “Checking access…” rather than implying a ban.

## 14. Compatibility order

1. Backend accepts telemetry v3, signed access snapshots, signed update v2, and action authorization before the desktop requires them.
2. Migrate identity without changing the stored install identifier.
3. Migrate every existing user to consent Unknown.
4. Send minimized v3 telemetry only after consent; retain v2 server support for old clients.
5. Exchange raw license key for opaque SecureStorage token.
6. Migrate one governed feature service at a time; keep status chips for display.
7. Add signed cached access internally while preserving current UI projections.
8. Ship the documented updater bridge; never downgrade later clients to unsigned XML.
9. Replace or retain FFmpeg only when it matches the lock.

## 15. Verification requirements

Add a real unit-test project before behavior changes. Policy logic must be plain .NET where possible; OS location, time, preferences, network, filesystem, signature verification, hash, and process launch receive injectable seams.

Required test groups:

- identity compatibility and no telemetry-coupled identity initialization;
- unknown/denied/granted consent and immediate revocation;
- location validation, maximum age, source, measured accuracy, and edge contract;
- telemetry payload excludes machine name, raw HWID, and Discord username;
- signed-manifest acceptance and tamper/rollback/origin/algorithm rejection;
- artifact size/hash/AuthentiCode/fixed-argument verification;
- pinned FFmpeg download, extraction, cached-binary, and launch-boundary verification;
- premium still contacts authorization server;
- every network/error/malformed path denies without mutation;
- same action ID is idempotent;
- every governed feature asserts validate-authorize-mutate ordering;
- suspension blocks starts, cancels active work, and leaves safety exits available;
- Discord presence behavior remains unchanged.

No test may invoke the real Windows location API, download or run an installer/FFmpeg binary, mutate the real display/firewall/game files, or call production endpoints.

