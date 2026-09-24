<#
.SYNOPSIS
  Builds a zip to share the mod: the compiled plugin, a ready-configured copy of the BepInEx mod
  loader (taken from your own game folder), and the player install guide. Contains no game files.

.PARAMETER GameDir
  The Nucleares install folder that already has BepInEx installed (defaults to this machine's).

.OUTPUTS
  dist\NuclearesVR-<version>.zip
#>
param(
    [string]$GameDir = "E:\Programs\Steam\steamapps\common\Nuclear Last Darkness"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

# Build
& dotnet build "$root\src\NuclearesVR\NuclearesVR.csproj" -c Release | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$version = (Select-String -Path "$root\src\NuclearesVR\Plugin.cs" -Pattern 'Version = "([^"]+)"').Matches[0].Groups[1].Value
$out = "$root\dist\NuclearesVR-$version"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force -Path "$out\BepInEx\core", "$out\BepInEx\plugins\NuclearesVR" | Out-Null

# Mod loader (BepInEx 5, x64), exactly as installed in the game folder - but not its settings or logs
foreach ($f in "winhttp.dll", "doorstop_config.ini", ".doorstop_version") {
    if (-not (Test-Path "$GameDir\$f")) { throw "Missing $f in $GameDir - is BepInEx installed there?" }
    Copy-Item "$GameDir\$f" $out
}
Copy-Item "$GameDir\BepInEx\core\*" "$out\BepInEx\core" -Recurse
New-Item -ItemType Directory -Force -Path "$out\BepInEx\config", "$out\BepInEx\patchers" | Out-Null

# The plugin itself (built output), the OpenVR library and the SteamVR Input files
$bin = "$root\src\NuclearesVR\bin\Release"
if (-not (Test-Path "$bin\NuclearesVR.dll")) { $bin = "$root\src\NuclearesVR\bin" }
foreach ($f in "NuclearesVR.dll", "openvr_api.dll", "nuclearesvr_actions.json", "nuclearesvr_bindings_oculus_touch.json") {
    $src = Get-ChildItem -Path $bin -Recurse -Filter $f | Select-Object -First 1
    if (-not $src) { throw "Build output $f not found under $bin" }
    Copy-Item $src.FullName "$out\BepInEx\plugins\NuclearesVR"
}

Copy-Item "$root\release\INSTALL.txt" $out

# Safety check: nothing from the game itself may be in the package
$bad = Get-ChildItem $out -Recurse -Include "Assembly-CSharp*.dll", "UnityEngine*.dll", "Fusion*.dll", "Nucleares*.exe"
if ($bad) { throw "Game files found in the package: $($bad.FullName -join ', ')" }

$zip = "$root\dist\NuclearesVR-$version.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "Built $zip" -ForegroundColor Green
Get-ChildItem $out -Recurse -File | ForEach-Object { $_.FullName.Substring($out.Length + 1) }
