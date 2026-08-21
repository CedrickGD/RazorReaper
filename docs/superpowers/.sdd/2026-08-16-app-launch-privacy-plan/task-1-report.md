# Task 1 Report: Windows Unit-Test Harness

## Status

Implemented and verified in the required detached/private worktree. No production behavior changed. No commit was created.

One environment concern is disclosed in full under **Local-only assets generation**: although the temporary NuGet config contained `<clear />` plus only the required global-cache source, the .NET 10 preview SDK also reported its implicit local `C:\Program Files\dotnet\library-packs` feed. No remote URL or `nuget.org` source was named, found in restore artifacts, or contacted.

## Worktree and safety baseline

- Working directory: `C:\Users\cedri\source\repos\CedrickGD\RazorReaper\.claude\worktrees\navbar-search-usability-f99f82`
- Starting HEAD: `17d9be955dfa6c07bc5da252a4496a08dd335201`
- Final HEAD: `17d9be955dfa6c07bc5da252a4496a08dd335201`
- Branch state before and after: `DETACHED_HEAD`
- Commits: none
- Existing untracked `docs/` planning material was preserved.
- No branch was created or switched; no commit, merge, reset, checkout, push, PR, publish, release, deploy, App launch, live-service access, or subagent occurred.
- The only restore was the single assets-generation restore documented below.

## Files changed

Tracked modifications:

- `RazorReaper.sln`
- `RazorReaper/RazorReaper.csproj`

Created harness files:

- `tests/RazorReaper.UnitTests/RazorReaper.UnitTests.csproj`
- `tests/RazorReaper.UnitTests/GlobalUsings.cs`
- `tests/RazorReaper.UnitTests/SmokeTests.cs`
- `tests/RazorReaper.UnitTests/Infrastructure/FakePreferencesStore.cs`
- `tests/RazorReaper.UnitTests/Infrastructure/FakeOsLocationProvider.cs`
- `tests/RazorReaper.UnitTests/Infrastructure/RecordingHttpMessageHandler.cs`
- `tests/RazorReaper.UnitTests/Infrastructure/ManualTimeProvider.cs`

Local report:

- `docs/superpowers/.sdd/2026-08-16-app-launch-privacy-plan/task-1-report.md`

No other production source or behavior file was modified.

## Scaffold and normalization

The project was scaffolded without restore and before any App reference or solution wiring:

```powershell
dotnet new xunit --no-restore --name RazorReaper.UnitTests --output .\tests\RazorReaper.UnitTests
```

The installed template generated `net10.0` and newer uncached package versions (`coverlet.collector` 6.0.4, `Microsoft.NET.Test.Sdk` 17.14.1, `xunit` 2.9.3, and `xunit.runner.visualstudio` 3.1.4). Before restore, the project was normalized to:

- `net10.0-windows10.0.19041.0`
- `ImplicitUsings=enable`
- `Nullable=enable`
- `IsPackable=false`
- `IsTestProject=true`
- `Microsoft.NET.Test.Sdk` 17.12.0
- `xunit` 2.9.2
- `xunit.runner.visualstudio` 2.8.2 with normal private/include assets
- `coverlet.collector` 6.0.2 with normal private/include assets
- no mocking package
- no `<Using Include="Xunit" />`; `GlobalUsings.cs` is the sole xUnit global-using owner
- generated `UnitTest1.cs` deleted

The App reference and solution entry were deliberately deferred until after the intended RED. The exact mechanical commands used afterward were:

```powershell
dotnet add .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj reference .\RazorReaper\RazorReaper.csproj
dotnet sln .\RazorReaper.sln add .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj
```

The solution tool also generated x64/x86 solution configurations. Those generated extras were removed during the green refactor checkpoint, preserving only the contract's Debug/Release Any CPU mappings. The generated `tests` solution folder was retained.

## Strict TDD evidence

### RED 1: assets absent

Command:

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~SmokeTests"
```

Exact relevant output and exit status:

```text
ASSETS_PRESENT=False
C:\Program Files\dotnet\sdk\10.0.400-preview.0.26322.102\Sdks\Microsoft.NET.Sdk\targets\Microsoft.NET.RuntimeIdentifierInference.targets(383,5): message NETSDK1057: Sie verwenden eine Vorschauversion von .NET. Weitere Informationen: https://aka.ms/dotnet-support-policy [C:\Users\cedri\source\repos\CedrickGD\RazorReaper\.claude\worktrees\navbar-search-usability-f99f82\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj]
C:\Program Files\dotnet\sdk\10.0.400-preview.0.26322.102\Sdks\Microsoft.NET.Sdk\targets\Microsoft.PackageDependencyResolution.targets(266,5): error NETSDK1004: Die Ressourcendatei "C:\Users\cedri\source\repos\CedrickGD\RazorReaper\.claude\worktrees\navbar-search-usability-f99f82\tests\RazorReaper.UnitTests\obj\project.assets.json" wurde nicht gefunden. Führen Sie eine NuGet-Paketwiederherstellung aus, um diese Datei zu generieren. [C:\Users\cedri\source\repos\CedrickGD\RazorReaper\.claude\worktrees\navbar-search-usability-f99f82\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj]
exit code: 1
```

This is the brief-permitted pre-assets RED, not a typo or test assertion failure.

### Intended RED: missing App integration

After the single local assets generation, with the App reference and solution wiring still absent, the same focused command produced:

```text
TEMP_CONFIG_PRESENT=False
C:\Program Files\dotnet\sdk\10.0.400-preview.0.26322.102\Sdks\Microsoft.NET.Sdk\targets\Microsoft.NET.RuntimeIdentifierInference.targets(383,5): message NETSDK1057: Sie verwenden eine Vorschauversion von .NET. Weitere Informationen: https://aka.ms/dotnet-support-policy [C:\Users\cedri\source\repos\CedrickGD\RazorReaper\.claude\worktrees\navbar-search-usability-f99f82\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj]
C:\Users\cedri\source\repos\CedrickGD\RazorReaper\.claude\worktrees\navbar-search-usability-f99f82\tests\RazorReaper.UnitTests\SmokeTests.cs(8,44): error CS0246: Der Typ- oder Namespacename "MauiProgram" wurde nicht gefunden (möglicherweise fehlt eine using-Direktive oder ein Assemblyverweis). [C:\Users\cedri\source\repos\CedrickGD\RazorReaper\.claude\worktrees\navbar-search-usability-f99f82\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj]
exit code: 1
```

This is the intended behavioral RED: removing/missing the App project integration makes the real `MauiProgram` type unavailable.

### Minimal GREEN: App assembly smoke test

The minimal integration change was one project reference, one solution entry, and one SDK-supported `InternalsVisibleTo Include="RazorReaper.UnitTests"` item. No other App project change was made.

Initial GREEN output:

```text
RazorReaper -> C:\Users\cedri\source\repos\CedrickGD\RazorReaper\.claude\worktrees\navbar-search-usability-f99f82\RazorReaper\bin\Debug\net10.0-windows10.0.19041.0\win-x64\RazorReaper.dll
RazorReaper.UnitTests -> C:\Users\cedri\source\repos\CedrickGD\RazorReaper\.claude\worktrees\navbar-search-usability-f99f82\tests\RazorReaper.UnitTests\bin\Debug\net10.0-windows10.0.19041.0\RazorReaper.UnitTests.dll
Bestanden!   : Fehler:     0, erfolgreich:     1, übersprungen:     0, gesamt:     1, Dauer: 269 ms - RazorReaper.UnitTests.dll (net10.0)
exit code: 0
```

Final fresh GREEN output is recorded under **Mandatory final verification**.

### Infrastructure RED/GREEN

After the smoke gate was green, infrastructure behavior tests were added first. The focused infrastructure run failed at the intended missing seam:

```text
RazorReaper -> C:\Users\cedri\source\repos\CedrickGD\RazorReaper\.claude\worktrees\navbar-search-usability-f99f82\RazorReaper\bin\Debug\net10.0-windows10.0.19041.0\win-x64\RazorReaper.dll
C:\Users\cedri\source\repos\CedrickGD\RazorReaper\.claude\worktrees\navbar-search-usability-f99f82\tests\RazorReaper.UnitTests\SmokeTests.cs(2,29): error CS0234: Der Typ- oder Namespacename "Infrastructure" ist im Namespace "RazorReaper.UnitTests" nicht vorhanden. (Möglicherweise fehlt ein Assemblyverweis.) [C:\Users\cedri\source\repos\CedrickGD\RazorReaper\.claude\worktrees\navbar-search-usability-f99f82\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj]
exit code: 1
```

After minimal infrastructure implementation:

```text
Bestanden!   : Fehler:     0, erfolgreich:    12, übersprungen:     0, gesamt:    12, Dauer: 46 ms - RazorReaper.UnitTests.dll (net10.0)
exit code: 0
```

## Local-only assets generation

### Exact package availability preflight

Before restore, exact `.nupkg` files were confirmed under `C:\Users\cedri\.nuget\packages` for the net10-relevant direct/transitive closure:

```text
FOUND microsoft.net.test.sdk 17.12.0
FOUND xunit 2.9.2
FOUND xunit.runner.visualstudio 2.8.2
FOUND coverlet.collector 6.0.2
FOUND microsoft.testplatform.testhost 17.12.0
FOUND microsoft.codecoverage 17.12.0
FOUND microsoft.testplatform.objectmodel 17.12.0
FOUND newtonsoft.json 13.0.1
FOUND system.reflection.metadata 1.6.0
FOUND xunit.core 2.9.2
FOUND xunit.assert 2.9.2
FOUND xunit.analyzers 1.16.0
FOUND xunit.extensibility.core 2.9.2
FOUND xunit.extensibility.execution 2.9.2
```

`System.Reflection.Metadata` 1.6.0's selected `.NETCoreApp2.1` dependency group is empty for this net10 target, so its older framework-only `System.Collections.Immutable` dependency is not in the selected closure.

### Temporary config

The temporary file was created outside the repository at `C:\Users\cedri\AppData\Local\Temp\razorreaper-unit-tests-local-only.config` with exactly:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local-cache" value="C:\Users\cedri\.nuget\packages" />
  </packageSources>
</configuration>
```

Exact one-time command:

```powershell
dotnet restore .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj --configfile C:\Users\cedri\AppData\Local\Temp\razorreaper-unit-tests-local-only.config -p:NuGetAudit=false --verbosity normal
```

Exact source/result portion of output:

```text
Die Assetdatei wird auf den Datenträger geschrieben. Pfad: C:\Users\cedri\source\repos\CedrickGD\RazorReaper\.claude\worktrees\navbar-search-usability-f99f82\tests\RazorReaper.UnitTests\obj\project.assets.json
"C:\Users\cedri\source\repos\CedrickGD\RazorReaper\.claude\worktrees\navbar-search-usability-f99f82\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj" wiederhergestellt (in 222 ms.).

Verwendete NuGet-Konfigurationsdateien:
    C:\Users\cedri\AppData\Local\Temp\razorreaper-unit-tests-local-only.config

Verwendete Feeds:
    C:\Users\cedri\.nuget\packages
    C:\Program Files\dotnet\library-packs

Der Buildvorgang wurde erfolgreich ausgeführt.
    0 Warnung(en)
    0 Fehler
exit code: 0
```

The config was immediately deleted with `apply_patch`; final check: `TEMP_CONFIG_PRESENT=False`.

Important disclosure: the temporary config itself named only the required global cache after `<clear />`, but SDK `10.0.400-preview.0.26322.102` automatically reported its built-in local `C:\Program Files\dotnet\library-packs` feed. This was not present in the config and is not remote. Restore artifacts confirm:

```text
assets packageFolders:
C:\Users\cedri\.nuget\packages\

restore graph sources:
C:\Program Files\dotnet\library-packs
C:\Users\cedri\.nuget\packages

REMOTE_URL_SCAN_OK no matches
```

The URL scan covered `project.assets.json`, `RazorReaper.UnitTests.csproj.nuget.dgspec.json`, and `project.nuget.cache` using `rg -n -i "https?://|nuget\.org"`. No remote source appeared. No second restore was run.

## Infrastructure behavior

- `FakePreferencesStore`: ordinal in-memory keys; deterministic `Get` with caller-provided default, `Set`, boolean `Remove`, and `Clear`; no MAUI static API.
- `FakeOsLocationProvider`: dependency-free `object?` programmed result plus cancellation-token call snapshots; intentionally does not define the future production location result contract.
- `RecordingHttpMessageHandler`: derives from `HttpMessageHandler`; snapshots method, URI, and body into immutable records; uses a programmable response factory; checks and passes cancellation; never delegates to a network transport.
- `ManualTimeProvider`: derives from `TimeProvider`; normalizes the initial value to UTC, exposes the controlled value through `GetUtcNow`, advances explicitly, is lock-protected, and throws `ArgumentOutOfRangeException` for backward movement.

Twelve narrow infrastructure tests protect preference state semantics, programmed/recorded location behavior, HTTP request snapshotting/response/cancellation, and UTC normalization/forward-only movement.

## Mandatory final verification

### Focused smoke

Command:

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore --filter "FullyQualifiedName~SmokeTests"
```

Exact result:

```text
RazorReaper -> C:\Users\cedri\source\repos\CedrickGD\RazorReaper\.claude\worktrees\navbar-search-usability-f99f82\RazorReaper\bin\Debug\net10.0-windows10.0.19041.0\win-x64\RazorReaper.dll
RazorReaper.UnitTests -> C:\Users\cedri\source\repos\CedrickGD\RazorReaper\.claude\worktrees\navbar-search-usability-f99f82\tests\RazorReaper.UnitTests\bin\Debug\net10.0-windows10.0.19041.0\RazorReaper.UnitTests.dll
Testlauf für "C:\Users\cedri\source\repos\CedrickGD\RazorReaper\.claude\worktrees\navbar-search-usability-f99f82\tests\RazorReaper.UnitTests\bin\Debug\net10.0-windows10.0.19041.0\RazorReaper.UnitTests.dll" (.NETCoreApp,Version=v10.0)
Insgesamt 1 Testdateien stimmten mit dem angegebenen Muster überein.

Bestanden!   : Fehler:     0, erfolgreich:     1, übersprungen:     0, gesamt:     1, Dauer: 101 ms - RazorReaper.UnitTests.dll (net10.0)
exit code: 0
```

### Full Task 1 test project

Command:

```powershell
dotnet test .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj -c Debug --no-restore
```

Exact result:

```text
RazorReaper -> C:\Users\cedri\source\repos\CedrickGD\RazorReaper\.claude\worktrees\navbar-search-usability-f99f82\RazorReaper\bin\Debug\net10.0-windows10.0.19041.0\win-x64\RazorReaper.dll
RazorReaper.UnitTests -> C:\Users\cedri\source\repos\CedrickGD\RazorReaper\.claude\worktrees\navbar-search-usability-f99f82\tests\RazorReaper.UnitTests\bin\Debug\net10.0-windows10.0.19041.0\RazorReaper.UnitTests.dll
Testlauf für "C:\Users\cedri\source\repos\CedrickGD\RazorReaper\.claude\worktrees\navbar-search-usability-f99f82\tests\RazorReaper.UnitTests\bin\Debug\net10.0-windows10.0.19041.0\RazorReaper.UnitTests.dll" (.NETCoreApp,Version=v10.0)
Insgesamt 1 Testdateien stimmten mit dem angegebenen Muster überein.

Bestanden!   : Fehler:     0, erfolgreich:    13, übersprungen:     0, gesamt:    13, Dauer: 132 ms - RazorReaper.UnitTests.dll (net10.0)
exit code: 0
```

### Standalone App Debug build

Command:

```powershell
dotnet build .\RazorReaper\RazorReaper.csproj -c Debug --no-restore
```

Exact result:

```text
RazorReaper -> C:\Users\cedri\source\repos\CedrickGD\RazorReaper\.claude\worktrees\navbar-search-usability-f99f82\RazorReaper\bin\Debug\net10.0-windows10.0.19041.0\win-x64\RazorReaper.dll

Der Buildvorgang wurde erfolgreich ausgeführt.
    0 Warnung(en)
    0 Fehler

Verstrichene Zeit 00:00:07.18
exit code: 0
```

All three commands emitted SDK informational message `NETSDK1057` because the installed SDK is a preview; the App build still reports zero warnings and zero errors.

## Solution/reference checks

The exact static verifier passed with:

```text
PACKAGE_OK Microsoft.NET.Test.Sdk 17.12.0
PACKAGE_OK xunit 2.9.2
PACKAGE_OK xunit.runner.visualstudio 2.8.2
PACKAGE_OK coverlet.collector 6.0.2
CSHARP_PROJECTS_OK count=2 app=1 tests=1
TEST_ANY_CPU_MAPPINGS_OK count=4
PROJECT_REFERENCE_OK count=1 resolved=C:\Users\cedri\source\repos\CedrickGD\RazorReaper\.claude\worktrees\navbar-search-usability-f99f82\RazorReaper\RazorReaper.csproj
INTERNALS_VISIBLE_TO_OK count=1 include=RazorReaper.UnitTests
GLOBAL_USING_OK global using Xunit;
GENERATED_FRIEND_ATTRIBUTE_OK C:\Users\cedri\source\repos\CedrickGD\RazorReaper\.claude\worktrees\navbar-search-usability-f99f82\RazorReaper\obj\Debug\net10.0-windows10.0.19041.0\win-x64\RazorReaper.AssemblyInfo.cs
TEMP_CONFIG_ABSENT_OK
```

`dotnet sln .\RazorReaper.sln list`:

```text
Projekt(e)
----------
RazorReaper\RazorReaper.csproj
tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj
```

`dotnet list .\tests\RazorReaper.UnitTests\RazorReaper.UnitTests.csproj reference`:

```text
Projektverweis(e)
-----------------
..\..\RazorReaper\RazorReaper.csproj
```

The generated App assembly info contains exactly:

```csharp
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("RazorReaper.UnitTests")]
```

## Scoped diff and hygiene checkpoint

Final task-scoped status (with `--untracked-files=all`):

```text
 M RazorReaper.sln
 M RazorReaper/RazorReaper.csproj
?? docs/superpowers/.sdd/2026-08-16-app-launch-privacy-plan/task-1-report.md
?? tests/RazorReaper.UnitTests/GlobalUsings.cs
?? tests/RazorReaper.UnitTests/Infrastructure/FakeOsLocationProvider.cs
?? tests/RazorReaper.UnitTests/Infrastructure/FakePreferencesStore.cs
?? tests/RazorReaper.UnitTests/Infrastructure/ManualTimeProvider.cs
?? tests/RazorReaper.UnitTests/Infrastructure/RecordingHttpMessageHandler.cs
?? tests/RazorReaper.UnitTests/RazorReaper.UnitTests.csproj
?? tests/RazorReaper.UnitTests/SmokeTests.cs
```

The final full status also showed only the pre-existing untracked planning/specification documents listed at baseline plus the owned files above; none of those unrelated documents was edited or removed.

Tracked task-scoped diff stat:

```text
RazorReaper.sln                | 11 +++++++++++
RazorReaper/RazorReaper.csproj |  4 ++++
2 files changed, 15 insertions(+)
```

`git diff --check` exit code: `0`.

Because Git does not include untracked files in ordinary `git diff --stat`, every created harness source/project file was additionally reviewed with `git diff --no-index -- NUL <absolute-file>`. The only tracked production-project delta is the four-line friend-assembly item group. The solution delta is the generated solution folder/project entry, four Debug/Release Any CPU test mappings, and nested-project relationship. The untracked source whitespace scan returned `UNTRACKED_WHITESPACE_SCAN_OK no matches`.

Git emitted only line-ending notices (`CRLF will be replaced by LF the next time Git touches it`) for the solution/App project/test project; `git diff --check` remained clean.

## Final safety confirmation

- `HEAD` remained `17d9be955dfa6c07bc5da252a4496a08dd335201` at detached HEAD; no commit or branch operation occurred.
- `Get-Process -Name RazorReaper` returned `NO_RAZORREAPER_PROCESS`; the App was never launched.
- The temporary NuGet config is absent.
- Restore artifacts contain no HTTP/HTTPS URL and no `nuget.org` string.
- The single restore used the cleared temporary config and local filesystem feeds only; the SDK-injected `library-packs` disclosure is recorded above.
- All verification after assets generation used `--no-restore`.
- No remote command, remote service, push, PR, publish, deploy, launch, or installer/updater/telemetry/location/Discord/access/UI/Shop behavior change occurred.
- No subagent was spawned.
