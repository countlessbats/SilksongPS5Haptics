# Silksong PS5 Haptics - installer
# Finds your Silksong install (or asks), ensures BepInEx, copies the mod in.

$ErrorActionPreference = 'Stop'
$BepInExUrl = 'https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip'
$GameExeName = 'Hollow Knight Silksong.exe'

function Clean-UserPath([string]$raw) {
    # Accept paths in whatever lazy format the user provides: unquoted,
    # quoted, single-quoted, mismatched quotes, trailing slashes, forward
    # slashes, env vars, the .exe path itself, or a parent folder.
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
    # Accepts the game root, its parent, or the _Data folder; returns the game root.
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
Write-Host '=== Silksong PS5 Haptics installer ===' -ForegroundColor Cyan
Write-Host ''

# --- 1. Locate the game ---
$game = Find-SteamGame
if ($game) {
    Write-Host "Found Silksong: $game"
    $answer = Read-Host 'Install here? [Y/n]'
    if ($answer -match '^[nN]') { $game = $null }
}
while (-not $game) {
    Write-Host ''
    $raw = Read-Host 'Paste your Hollow Knight Silksong folder (any format is fine)'
    if ([string]::IsNullOrWhiteSpace($raw)) { Write-Host 'Nothing entered - Ctrl+C to give up, or try again.'; continue }
    $cleaned = Clean-UserPath $raw
    $game = Resolve-GameDir $cleaned
    if (-not $game) {
        Write-Host "  Couldn't find `"$GameExeName`" at: $cleaned" -ForegroundColor Yellow
        Write-Host '  Tip: right-click the game in Steam > Manage > Browse local files, and copy that path.'
    }
}
Write-Host ''

# --- 2. Ensure BepInEx ---
$needBepInEx = -not ((Test-Path -LiteralPath (Join-Path $game 'BepInEx\core\BepInEx.dll')) -and (Test-Path -LiteralPath (Join-Path $game 'winhttp.dll')))
if ($needBepInEx) {
    Write-Host 'BepInEx not found - downloading 5.4.23.5...'
    $tmp = Join-Path $env:TEMP 'BepInEx_silksong_haptics.zip'
    Invoke-WebRequest -Uri $BepInExUrl -OutFile $tmp -UseBasicParsing
    Expand-Archive -LiteralPath $tmp -DestinationPath $game -Force
    Remove-Item -LiteralPath $tmp -Force
    Write-Host 'BepInEx installed.'
} else {
    Write-Host 'BepInEx already present.'
}

# --- 3. Copy the mod ---
$payload = Join-Path $PSScriptRoot 'files'
if (-not (Test-Path -LiteralPath $payload)) {
    Write-Host "Payload folder missing next to the installer: $payload" -ForegroundColor Red
    exit 1
}
Copy-Item -LiteralPath (Join-Path $payload 'BepInEx') -Destination $game -Recurse -Force
Write-Host 'Mod files copied.'

Write-Host ''
Write-Host '=== Done! ===' -ForegroundColor Green
Write-Host ''
Write-Host 'Next steps:'
Write-Host '  1. For WIRELESS haptics: in DSX (v3.2 beta + DSX+ DLC), enable'
Write-Host '     Haptics/Rumble page -> "BT Audio/Haptics". Or connect the controller via USB.'
Write-Host '  2. Launch Silksong. A tray icon appears; the first run extracts haptic'
Write-Host '     data from your game files (~10s), then the controller plays a chime.'
Write-Host '  3. Tray dot: green = haptics flowing, orange = waiting for game,'
Write-Host '     gray = no DualSense audio device.'
Write-Host ''
