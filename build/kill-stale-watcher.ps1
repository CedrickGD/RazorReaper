<#
.SYNOPSIS
    Frees the build output folder before MSBuild writes RazorReaper.exe into it.

.DESCRIPTION
    The ARK link feature registers an HKCU\...\Run entry that starts the app headless
    ("--arkwatch", see Platforms/Windows/ArkWatch.cs) at every Windows login. That watcher
    loops forever by design — it never exits — so on a dev machine, where the Run entry
    points straight at bin\<Config>\...\win-x64\, it keeps RazorReaper.exe permanently
    locked and every later build dies on the apphost copy with MSB3027 / MSB3021:

        "RazorReaper.exe" ... because it is being used by another process.

    The watcher has no window, so nothing on screen hints at the cause — the build just
    starts failing at some point after a login and keeps failing until the process is
    found and killed by hand.

    Only headless watcher instances started from the folder currently being built are
    terminated. A visible UI instance is deliberately left running and reported instead:
    killing an app the developer is actively using is not the build system's call.

.PARAMETER TargetDir
    The output folder being built ($(TargetDir)). Processes running from anywhere else —
    a real install, another configuration, another worktree — are ignored.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $TargetDir
)

# A dev machine with restricted WMI access must still be able to build; every failure in
# here degrades to "do nothing" and lets the build hit the original MSB3027 on its own.
$ErrorActionPreference = 'Continue'

if ([string]::IsNullOrWhiteSpace($TargetDir) -or -not (Test-Path -LiteralPath $TargetDir)) {
    return
}

$target = (Resolve-Path -LiteralPath $TargetDir).ProviderPath.TrimEnd('\')

try {
    $processes = @(Get-CimInstance Win32_Process -Filter "Name='RazorReaper.exe'" -ErrorAction Stop)
}
catch {
    Write-Host "RazorReaper: could not enumerate processes ($($_.Exception.Message)) - skipping the output-lock check."
    return
}

foreach ($p in $processes) {
    $exe = $p.ExecutablePath
    if ([string]::IsNullOrWhiteSpace($exe)) { continue }
    if (-not $exe.StartsWith("$target\", [StringComparison]::OrdinalIgnoreCase)) { continue }

    $commandLine = if ($p.CommandLine) { $p.CommandLine } else { '' }

    # Both flags mean headless watch mode — LegacyWaitForArkArg ("--waitforark") is still
    # honoured by ArkWatch.ShouldRunWatchMode, so a pre-watcher build left running from an
    # old Run entry locks the output exactly the same way.
    if ($commandLine -match '(?i)--(arkwatch|waitforark)\b') {
        try {
            Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop
            Write-Host "RazorReaper: stopped the headless ARK login watcher (PID $($p.ProcessId)) that was locking the build output."
        }
        catch {
            # Canonical MSBuild format so this surfaces in the VS Error List rather than
            # scrolling past in the build output.
            Write-Host "RazorReaper : warning RR0001: could not stop the ARK watcher (PID $($p.ProcessId)): $($_.Exception.Message)"
        }
    }
    else {
        Write-Host "RazorReaper : warning RR0002: RazorReaper (PID $($p.ProcessId)) is running from the build output - close it, or this build fails with MSB3027."
    }
}
