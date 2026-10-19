# Gungeon Online installer. Finds Enter the Gungeon (Steam, GOG, Epic) and installs BepInEx + the mod.
# Run "Install Gungeon Online.bat" next to this file. Use -Uninstall to remove.
param([switch]$Uninstall, [string]$GameDir = "", [switch]$List)
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$payload = Join-Path $here "files"

function Find-Gungeon {
    $found = New-Object System.Collections.Generic.List[string]
    # Steam: every library folder
    $steamRoots = @()
    foreach ($k in "HKCU:\Software\Valve\Steam", "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam") {
        try { $p = (Get-ItemProperty $k -ErrorAction Stop); if ($p.SteamPath) { $steamRoots += $p.SteamPath }; if ($p.InstallPath) { $steamRoots += $p.InstallPath } } catch {}
    }
    foreach ($root in ($steamRoots | Select-Object -Unique)) {
        $vdf = [System.IO.Path]::Combine($root, "steamapps\libraryfolders.vdf")
        $libs = @($root)
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) { $libs += ($m.Groups[1].Value -replace '\\\\', '\') }
        }
        foreach ($l in ($libs | Select-Object -Unique)) {
            # Libraries can sit on drives that are not connected right now; skip those quietly.
            try {
                $g = [System.IO.Path]::Combine($l, "steamapps\common\Enter the Gungeon")
                if ([System.IO.File]::Exists([System.IO.Path]::Combine($g, "EtG.exe"))) { $found.Add($g) }
            } catch {}
        }
    }
    # GOG
    foreach ($k in "HKLM:\SOFTWARE\WOW6432Node\GOG.com\Games\1456912569", "HKLM:\SOFTWARE\GOG.com\Games\1456912569") {
        try { $p = (Get-ItemProperty $k -ErrorAction Stop).path; if ($p -and (Test-Path (Join-Path $p "EtG.exe"))) { $found.Add($p) } } catch {}
    }
    # Epic
    $man = "$env:ProgramData\Epic\EpicGamesLauncher\Data\Manifests"
    if (Test-Path $man) {
        foreach ($f in Get-ChildItem $man -Filter *.item) {
            try {
                $j = Get-Content $f.FullName -Raw | ConvertFrom-Json
                if ($j.DisplayName -like "*Gungeon*" -and (Test-Path (Join-Path $j.InstallLocation "EtG.exe"))) { $found.Add($j.InstallLocation) }
            } catch {}
        }
    }
    return $found | ForEach-Object { [System.IO.Path]::GetFullPath($_) } | Sort-Object -Unique
}

function Pick-Folder {
    Add-Type -AssemblyName System.Windows.Forms
    $d = New-Object System.Windows.Forms.FolderBrowserDialog
    $d.Description = "Select your Enter the Gungeon folder (the one with EtG.exe)"
    if ($d.ShowDialog() -eq "OK" -and (Test-Path (Join-Path $d.SelectedPath "EtG.exe"))) { return $d.SelectedPath }
    return $null
}

Write-Host ""
Write-Host "  Gungeon Online installer" -ForegroundColor Cyan
Write-Host ""
$games = if ($GameDir) { @($GameDir) } else { @(Find-Gungeon) }
if ($List) { $games | ForEach-Object { Write-Host "  Found: $_" }; exit 0 }
if ($games.Count -eq 0) {
    Write-Host "  Could not find Enter the Gungeon automatically. Pick its folder in the window that opens."
    $g = Pick-Folder
    if (-not $g) { Write-Host "  No game folder chosen. Nothing was changed." -ForegroundColor Yellow; exit 1 }
    $games = @($g)
}
if (-not $GameDir -and (Get-Process EtG -ErrorAction SilentlyContinue)) {
    Write-Host "  Enter the Gungeon is running. Close it and run the installer again." -ForegroundColor Yellow
    exit 1
}

# Program Files needs admin rights: ask Windows once, only if a target folder is not writable.
function Test-Writable($dir) {
    try { $t = Join-Path $dir ".go-write-test"; [System.IO.File]::WriteAllText($t, "x"); Remove-Item $t -Force; return $true } catch { return $false }
}
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin -and ($games | Where-Object { -not (Test-Writable $_) })) {
    Write-Host "  Your game is in a protected folder. Windows will ask for permission."
    $args2 = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$($MyInvocation.MyCommand.Path)`"")
    if ($Uninstall) { $args2 += "-Uninstall" }
    if ($GameDir) { $args2 += @("-GameDir", "`"$GameDir`"") }
    $p = Start-Process powershell -Verb RunAs -ArgumentList $args2 -PassThru -Wait
    exit $p.ExitCode
}

foreach ($g in $games) {
    if ($Uninstall) {
        Remove-Item (Join-Path $g "BepInEx\plugins\GungeonOnline") -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "  Removed Gungeon Online from $g" -ForegroundColor Green
        continue
    }
    Copy-Item (Join-Path $payload "*") $g -Recurse -Force
    Write-Host "  Installed into $g" -ForegroundColor Green
}
Write-Host ""
if (-not $Uninstall) {
    Write-Host "  Done. Start the game, pause, and pick Play Online (or hold Back + Start, or press F8)."
}
Write-Host ""
