# Builds the distributable: one self-contained NexusPlayer.exe with the .NET
# runtime baked in, so it runs on a machine with no .NET installed.
#
# Published outside bin/ deliberately — a rebuild wipes bin/, and the desktop
# shortcut points here.
#
#   powershell -File tools\publish.ps1
param([string]$Out = "$env:USERPROFILE\NexusPlayer")

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
"{0}  ({1:N0} MB)" -f $exe, ((Get-Item -LiteralPath $exe).Length / 1MB)

# mpv is located at runtime (settings -> app folder -> PATH -> known locations).
# Dropping mpv.exe beside this exe makes the whole thing portable.
