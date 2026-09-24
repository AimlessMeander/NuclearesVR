<#
.SYNOPSIS
  Populates lib/ with everything the project needs to build, but that we don't
  commit to git: the game's own assemblies (copyrighted, must come from your
  local install) and the OpenVR SDK files (small, but no reason to bloat the
  repo with binaries that don't change).

.PARAMETER GameDir
  Path to the Nucleares install. Defaults to the Steam location found on this
  machine; override if yours differs.
#>
param(
    [string]$GameDir = "E:\Programs\Steam\steamapps\common\Nuclear Last Darkness"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$managed = Join-Path $GameDir "Nucleares_Data\Managed"

if (-not (Test-Path $managed)) {
    throw "Couldn't find $managed - pass -GameDir pointing at your Nucleares install."
}

New-Item -ItemType Directory -Force -Path "$root\lib\game" | Out-Null
New-Item -ItemType Directory -Force -Path "$root\lib\bepinex" | Out-Null

$gameDlls = @(
    "Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll",
    "UnityEngine.dll", "UnityEngine.CoreModule.dll", "UnityEngine.PhysicsModule.dll",
    "UnityEngine.InputLegacyModule.dll", "UnityEngine.InputModule.dll", "UnityEngine.UI.dll",
    "UnityEngine.UIModule.dll", "UnityEngine.IMGUIModule.dll", "UnityEngine.AudioModule.dll",
    "UnityEngine.AnimationModule.dll", "UnityEngine.TextRenderingModule.dll",
    "UnityEngine.ImageConversionModule.dll", "UnityEngine.AssetBundleModule.dll",
    "UnityEngine.ScreenCaptureModule.dll", "Unity.TextMeshPro.dll",
    "Fusion.Runtime.dll", "Fusion.Common.dll", "Fusion.Realtime.dll", "Fusion.Sockets.dll",
    "Fusion.Log.dll", "Fusion.Unity.dll", "Photon3Unity3D.dll", "Newtonsoft.Json.dll"
)
foreach ($dll in $gameDlls) {
    $src = Join-Path $managed $dll
    if (Test-Path $src) {
        Copy-Item $src "$root\lib\game\" -Force
    } else {
        Write-Warning "Missing (skipped): $dll"
    }
}

$bepinexCore = Join-Path $GameDir "BepInEx\core"
if (-not (Test-Path $bepinexCore)) {
    throw "BepInEx isn't installed in $GameDir yet - install it first (see README.md)."
}
Copy-Item "$bepinexCore\BepInEx.dll" "$root\lib\bepinex\" -Force
Copy-Item "$bepinexCore\BepInEx.Harmony.dll" "$root\lib\bepinex\" -Force
Copy-Item "$bepinexCore\0Harmony.dll" "$root\lib\bepinex\" -Force

$openvrDll = "$root\lib\openvr_api.dll"
if (-not (Test-Path $openvrDll)) {
    Invoke-WebRequest -Uri "https://raw.githubusercontent.com/ValveSoftware/openvr/master/bin/win64/openvr_api.dll" -OutFile $openvrDll
}
$openvrCs = "$root\src\NuclearesVR\Vr\OpenVR.cs"
if (-not (Test-Path $openvrCs)) {
    Invoke-WebRequest -Uri "https://raw.githubusercontent.com/ValveSoftware/openvr/master/headers/openvr_api.cs" -OutFile $openvrCs
}

Write-Host "Dependencies ready. Build with: dotnet build src/NuclearesVR/NuclearesVR.csproj" -ForegroundColor Green
