# Builds Squeaker as one portable exe.
# Usage:  .\publish.ps1        -> self-tests, then Squeaker.exe (+ Versions\Archive\Squeaker-<version>.exe)
#                                  A running Squeaker is closed and restarted on the new version.
#         .\publish.ps1 -Run   -> same, and starts it even if it wasn't running
#         .\publish.ps1 -Test  -> builds and self-tests into dist only; nothing is archived or replaced
# Refuses a version that's already archived: raise <Version> in src\Squeaker\Squeaker.csproj first.
param([switch]$Run, [switch]$Test)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root "src\Squeaker\Squeaker.csproj"
$out  = Join-Path $root "dist"
# The x64 SDK; the dotnet on PATH can be the x86 one, which has no SDK on some PCs.
$dotnet = if (Test-Path "$env:ProgramFiles\dotnet\dotnet.exe") { "$env:ProgramFiles\dotnet\dotnet.exe" } else { "dotnet" }

$version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$archive = Join-Path $root "Versions\Archive\Squeaker-$version.exe"
if (-not $Test -and (Test-Path $archive)) { throw "Version $version is already archived. Raise <Version> in the csproj, then publish again." }

& $dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $out
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }
Get-ChildItem $out -File | Where-Object { $_.Name -ne "Squeaker.exe" } | Remove-Item -Force

# The built exe checks its cursor files, pack import and apply/restore (on a test registry key) before it is kept.
$exe = Join-Path $out "Squeaker.exe"
# Not $test: PowerShell names ignore case, so it would be the -Test switch.
$check = [Diagnostics.Process]::Start($exe, "--selftest")
$check.WaitForExit()
if ($check.ExitCode -ne 0) {
    Get-Content (Join-Path $env:TEMP "squeaker-selftest.log") | Write-Host -ForegroundColor Red
    throw "Self-test failed; nothing was archived."
}
if ($Test) { Write-Host "Test build $version passed its self-test: $exe" -ForegroundColor Green; return }

New-Item -ItemType Directory -Force (Split-Path $archive) | Out-Null
Copy-Item $exe $archive
Write-Host ("Built Squeaker {0} ({1} MB), copy kept in Versions\Archive" -f $version, [math]::Round((Get-Item $exe).Length / 1MB, 1)) -ForegroundColor Green

# A running Squeaker is closed so the new version can replace it (the owner's standing wish), then started again.
# It only holds settings, which are saved as they change, so stopping it loses nothing.
$top = Join-Path $root "Squeaker.exe"
$running = @(Get-Process Squeaker -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $top })
foreach ($p in $running) {
    $p.Kill()
    $p.WaitForExit(10000) | Out-Null
}
Copy-Item $exe $top
Write-Host "Squeaker.exe is now $version." -ForegroundColor Green
if ($running.Count -gt 0 -or $Run) {
    # Started through Windows (not as this script's child) so it keeps running after the build; in the tray if it was hidden.
    Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = "`"$top`" --tray"; CurrentDirectory = $root } | Out-Null
}
