param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [string]$Dll = "$PSScriptRoot\..\src\GungeonOnline\bin\Release\GungeonOnline.dll"
)
# Installs BepInEx 5 (if missing) and the Gungeon Online plugin into an Enter the Gungeon folder.
$ErrorActionPreference = "Stop"
if (-not (Test-Path (Join-Path $GameDir "EtG.exe"))) { throw "EtG.exe not found in $GameDir" }
$bep = Join-Path $GameDir "BepInEx"
if (-not (Test-Path (Join-Path $bep "core\BepInEx.dll"))) {
    $zip = Join-Path $env:TEMP "BepInEx_win_x64_5.4.23.5.zip"
    if (-not (Test-Path $zip)) {
        Invoke-WebRequest "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip" -OutFile $zip
    }
    Expand-Archive $zip -DestinationPath $GameDir -Force
    Write-Output "BepInEx installed."
}
$dst = Join-Path $bep "plugins\GungeonOnline"
New-Item -ItemType Directory -Force $dst | Out-Null
Copy-Item $Dll $dst -Force
Write-Output "Gungeon Online installed to $dst"
