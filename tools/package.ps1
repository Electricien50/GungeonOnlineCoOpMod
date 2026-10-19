param([string]$Out = "$PSScriptRoot\..\dist")
# Builds a zip players can unzip straight into their game folder (BepInEx + plugin). Same zip for Steam, GOG and Epic.
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\.."
$stage = Join-Path $Out "stage"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item (Join-Path $root "third_party\bepinex\*") $stage -Recurse -Force
$plug = Join-Path $stage "BepInEx\plugins\GungeonOnline"
New-Item -ItemType Directory -Force $plug | Out-Null
Copy-Item (Join-Path $root "src\GungeonOnline\bin\Release\GungeonOnline.dll") $plug -Force
Copy-Item (Join-Path $root "README.md") $stage -Force
$zip = Join-Path $Out "GungeonOnline.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
Write-Output "Package: $zip"
