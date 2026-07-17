# Silksong PS5 Haptics - uninstaller
# Stops the bridge and removes the mod's plugin folder + config.
# Leaves BepInEx and any other mods untouched.

$ErrorActionPreference = 'Stop'
$GameExeName  = 'Hollow Knight Silksong.exe'
$PluginFolder = 'SilksongPS5Haptics'
$ConfigFile   = 'com.will.silksong.ps5haptics.cfg'

function Clean-UserPath([string]$raw) {
    if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
    $s = $raw.Trim().Trim('"', "'", ' ', "`t")
    if (-not $s) { return $null }
    $s = [Environment]::ExpandEnvironmentVariables($s) -replace '/', '\'
    if ($s -match '\.exe\s*$') { try { $s = Split-Path $s -Parent } catch { return $null } }
    $s = $s.TrimEnd('\', ' ')
    if (-not $s) { return $null }
    return $s
}

function Resolve-GameDir([string]$dir) {
    if (-not $dir) { return $null }
    if (Test-Path -LiteralPath (Join-Path $dir $GameExeName)) { return $dir }
    $sub = Join-Path $dir 'Hollow Knight Silksong'
    if (Test-Path -LiteralPath (Join-Path $sub $GameExeName)) { return $sub }
    if ($dir -like '*Hollow Knight Silksong_Data') {
        $parent = Split-Path $dir -Parent
        if ($parent -and (Test-Path -LiteralPath (Join-Path $parent $GameExeName))) { return $parent }
    }
    return $null
}

function Find-SteamGame {
    $steamRoots = New-Object System.Collections.Generic.List[string]
    foreach ($reg in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam') {
        try {
            $p = (Get-ItemProperty $reg -ErrorAction Stop).SteamPath
            if (-not $p) { $p = (Get-ItemProperty $reg -ErrorAction Stop).InstallPath }
            if ($p) { $steamRoots.Add(($p -replace '/', '\')) }
        } catch { }
    }
    $steamRoots.Add('C:\Program Files (x86)\Steam')
    $steamRoots.Add('C:\Program Files\Steam')
    foreach ($steam in ($steamRoots | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $steam)) { continue }
        $libs = New-Object System.Collections.Generic.List[string]
        $libs.Add((Join-Path $steam 'steamapps'))
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf) {
            foreach ($m in (Select-String -LiteralPath $vdf -Pattern '"path"\s+"([^"]+)"' -AllMatches).Matches) {
                $libs.Add((Join-Path ($m.Groups[1].Value -replace '\\\\', '\') 'steamapps'))
            }
        }
        foreach ($lib in ($libs | Select-Object -Unique)) {
            $found = Resolve-GameDir (Join-Path $lib 'common\Hollow Knight Silksong')
            if ($found) { return $found }
        }
    }
    return $null
}

Write-Host ''
Write-Host '=== Silksong PS5 Haptics uninstaller ===' -ForegroundColor Cyan
Write-Host ''

$game = Find-SteamGame
if ($game) {
    Write-Host "Found Silksong: $game"
    if ((Read-Host 'Uninstall from here? [Y/n]') -match '^[nN]') { $game = $null }
}
while (-not $game) {
    $raw = Read-Host 'Paste your Hollow Knight Silksong folder (any format is fine)'
    if ([string]::IsNullOrWhiteSpace($raw)) { Write-Host 'Nothing entered - Ctrl+C to quit, or try again.' -ForegroundColor Yellow; continue }
    $game = Resolve-GameDir (Clean-UserPath $raw)
    if (-not $game) { Write-Host "  Couldn't find $GameExeName there." -ForegroundColor Yellow }
}

# Close the tray bridge so its files aren't locked.
Get-Process HapticsBridge -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$plugin = Join-Path $game (Join-Path 'BepInEx\plugins' $PluginFolder)
$cfg    = Join-Path $game (Join-Path 'BepInEx\config' $ConfigFile)
$removed = 0
if (Test-Path -LiteralPath $plugin) { Remove-Item -LiteralPath $plugin -Recurse -Force; Write-Host "Removed: $plugin"; $removed++ }
if (Test-Path -LiteralPath $cfg)    { Remove-Item -LiteralPath $cfg -Force;            Write-Host "Removed: $cfg";    $removed++ }

Write-Host ''
if ($removed -gt 0) {
    Write-Host '=== Uninstalled ===' -ForegroundColor Green
    Write-Host 'BepInEx and any other mods were left intact. Silksong returns to normal rumble.'
} else {
    Write-Host 'Nothing to remove - the mod was not found there.' -ForegroundColor Yellow
}
Write-Host ''
Read-Host 'Press Enter to exit'
