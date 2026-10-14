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
# script's $ErrorActionPreference, so without it a scan that never ran would go on to report a clean result.
#
# Retried: a definitions update that fails ("The remote procedure call failed") leaves Defender restarting
# for a few seconds, and a scan started then fails with "Provider load failure". It happens on hosted
# runners and locally alike. Only a scan that never runs at all fails the build.
for ($attempt = 1; ; $attempt++) {
  try {
    Start-MpScan -ScanType CustomScan -ScanPath $scanPath -ErrorAction Stop
    break
  } catch {
    if ($attempt -ge 6) { throw "Microsoft Defender couldn't scan $scanPath after $attempt tries: $($_.Exception.Message)" }
    Write-Warning "Scan attempt $attempt failed ($($_.Exception.Message)); waiting for Defender to come back."
    Start-Sleep -Seconds 15
  }
}

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
