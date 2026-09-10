# Builds the distributable: one self-contained NexusPlayer.exe with the .NET
# runtime baked in, plus the mpv toolchain beside it so the folder is a complete,
# movable app.
#
# Published outside bin/ deliberately — a rebuild wipes bin/, and the desktop
# shortcut points here.
#
#   powershell -File tools\publish.ps1
param(
    [string]$Out = "$env:USERPROFILE\NexusPlayer",
    [string]$MpvDir = ""
)

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)

# Not trimmed: WPF resolves plenty by reflection and trimming breaks it.
dotnet publish -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $Out --nologo

$exe = Join-Path $Out 'NexusPlayer.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "publish produced no exe at $exe" }

# --- the mpv toolchain -------------------------------------------------------
# The app is a frontend: it launches mpv.exe as a separate process and drives it
# over IPC. It does not contain mpv. Copying the binaries here matters because
# MpvLocator checks the app's own folder BEFORE the known locations, so the
# published folder stops depending on wherever mpv happened to be installed.
#
# ffmpeg.exe is deliberately not copied: mpv has FFmpeg linked in, and the
# standalone binary is only needed for yt-dlp's download-time merging, not for
# streaming playback. It would add ~107 MB for nothing.
if (-not $MpvDir) {
    foreach ($guess in @(
        "$env:USERPROFILE\Downloads\bootstrapper",
        "$env:USERPROFILE\scoop\apps\mpv\current",
        "$env:LOCALAPPDATA\Programs\mpv",
        "$env:ProgramFiles\mpv")) {
        if (Test-Path -LiteralPath (Join-Path $guess 'mpv.exe')) { $MpvDir = $guess; break }
    }
}

if ($MpvDir -and (Test-Path -LiteralPath (Join-Path $MpvDir 'mpv.exe'))) {
    foreach ($f in 'mpv.exe', 'yt-dlp.exe', 'd3dcompiler_43.dll') {
        $src = Join-Path $MpvDir $f
        if (Test-Path -LiteralPath $src) {
            Copy-Item -LiteralPath $src -Destination (Join-Path $Out $f) -Force
            "  bundled $f"
        } else {
            "  (not found, skipped) $f"
        }
    }
} else {
    Write-Warning "mpv.exe not found; the published folder will fall back to whatever mpv is installed on the machine. Pass -MpvDir to bundle it."
}

@"
Nexus Player
============

Run NexusPlayer.exe.

What is in this folder
----------------------
NexusPlayer.exe      the player. The .NET runtime is baked in, so no .NET
                     install is required.
mpv.exe              the actual video engine. Nexus Player is a frontend: it
                     launches mpv as a separate process, has it render into a
                     child window, and controls it over a JSON IPC pipe.
yt-dlp.exe           resolves streaming links (YouTube and friends). Keep it
                     current with "yt-dlp.exe -U" - sites break old versions
                     regularly, and the symptom is HTTP 403 on playback while
                     the title still resolves.
d3dcompiler_43.dll   shader compilation support for mpv on some systems.

Configuration still lives in %APPDATA%\mpv (mpv.conf, subtitle styling,
Anime4K shaders) and %APPDATA%\NexusPlayerV2 (settings, session, logs). On a
different machine those would need copying too, or mpv falls back to defaults.
"@ | Set-Content -LiteralPath (Join-Path $Out 'README.txt') -Encoding utf8

""
"{0}  ({1:N0} MB)" -f $exe, ((Get-Item -LiteralPath $exe).Length / 1MB)
"folder total: {0:N0} MB" -f ((Get-ChildItem -LiteralPath $Out -File | Measure-Object Length -Sum).Sum / 1MB)
