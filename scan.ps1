# Scans dist\ with Microsoft Defender and fails if it finds anything, or removes a file.
# Run after build.ps1. CI runs it on every build, the release before publishing:
# self-extracting single-file executables are what antivirus false positives
# like best, and finding that out on a pull request beats finding it on release day.

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$status = Get-MpComputerStatus -ErrorAction Stop
if (-not $status.AntivirusEnabled) {
  throw 'Microsoft Defender Antivirus is unavailable; refusing to call the files scanned.'
}

# Hosted images can carry stale definitions. Best effort: a definitions outage
# should not block a build the scan is about to check.
try {
  Update-MpSignature -ErrorAction Stop
  Write-Host "Defender definitions: $((Get-MpComputerStatus).AntivirusSignatureVersion)"
} catch {
  Write-Warning "Could not update Defender definitions, scanning with what the machine has: $($_.Exception.Message)"
}

$scanPath = (Resolve-Path 'dist').Path
$scanStartedAt = Get-Date
# -ErrorAction Stop on each Defender call: the Defender cmdlets live in a module that doesn't see this
# script's $ErrorActionPreference, so without it a scan that never ran ("Provider load failure") would
# go on to report a clean result.
Start-MpScan -ScanType CustomScan -ScanPath $scanPath -ErrorAction Stop

# SilentlyContinue: with no detections at all, Get-MpThreatDetection reports an error rather than nothing.
$threats = @(Get-MpThreatDetection -ErrorAction SilentlyContinue | Where-Object {
  $_.InitialDetectionTime -ge $scanStartedAt -and
  ($_.Resources | Where-Object { $_ -like "*$scanPath*" } | Measure-Object).Count -gt 0
})
if ($threats) {
  $names = ($threats | ForEach-Object ThreatName | Select-Object -Unique) -join ', '
  throw "Microsoft Defender detected a threat in the release files: $names"
}

# Defender quarantines what it detects, so a missing file is a detection too.
$missing = 'dist/WhyNotSleeping.exe', 'dist/WhyNotSleepingCli.exe' | Where-Object { -not (Test-Path $_) }
if ($missing) { throw "Microsoft Defender removed a release file: $($missing -join ', ')" }

Write-Host "Microsoft Defender scanned the files in $scanPath and found nothing."
