# Publishes both executables into dist\ as single self-contained files:
#   dist\WhyNotSleeping.exe     - the window, for everyone
#   dist\WhyNotSleepingCli.exe  - the console monitor
# Neither needs .NET installed on the machine that runs it.

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Remove-Item dist -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish src/WhyNotSleeping.Gui -c Release -o dist/gui
if ($LASTEXITCODE -ne 0) { throw 'GUI publish failed' }
dotnet publish src/WhyNotSleeping.Cli -c Release -o dist/cli
if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed' }

Move-Item dist/gui/WhyNotSleeping.exe, dist/cli/WhyNotSleepingCli.exe dist
Remove-Item dist/gui, dist/cli -Recurse -Force

Get-ChildItem dist -File | ForEach-Object { '{0,-24} {1,6:N1} MB' -f $_.Name, ($_.Length / 1MB) }
