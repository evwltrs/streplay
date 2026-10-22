# Builds the optional ffmpeg companion package: dist\1nf-STREPLAY_FFMPEG-<version>.zip, with ffmpeg.exe and
# StreplayFfmpeg.dll (ffmpeg-package\bridge: what starts ffmpeg, so the main STREPLAY package starts no programs).
# Run: powershell -File tools\pack-ffmpeg.ps1 -Ffmpeg "C:\path\to\ffmpeg.exe"
param([Parameter(Mandatory = $true)][string]$Ffmpeg)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$src = Join-Path $root 'ffmpeg-package'
$manifest = Get-Content (Join-Path $src 'manifest.json') -Raw | ConvertFrom-Json
$ver = $manifest.version_number

# the exe must be the build the README describes (the package's own version moves on its own since 9.0.3)
$readme = Get-Content (Join-Path $src 'README.md') -Raw
if ($readme -notmatch '\*\*FFmpeg ([0-9.]+)\*\*') { throw "README.md doesn't name the FFmpeg version (**FFmpeg x.y.z**)" }
$ffver = $Matches[1]
$banner = (& $Ffmpeg -hide_banner -version 2>&1 | Select-Object -First 1) -as [string]
if ($banner -notmatch "ffmpeg version $([regex]::Escape($ffver))") { throw "ffmpeg.exe reports '$banner', but README.md says FFmpeg $ffver. Update ffmpeg-package/README.md and CHANGELOG.md first." }

dotnet build (Join-Path $src 'bridge\StreplayFfmpeg.csproj') -c Release -v q -nologo
if ($LASTEXITCODE -ne 0) { throw "StreplayFfmpeg.dll didn't build" }

$stage = Join-Path $root 'obj\ffmpeg-package'
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage | Out-Null
# icon.png comes from the logo (tools/make-icons.py)
Copy-Item (Join-Path $src 'manifest.json'), (Join-Path $src 'README.md'), (Join-Path $src 'CHANGELOG.md'), (Join-Path $src 'LICENSE.txt'), (Join-Path $src 'icon.png') $stage
Copy-Item $Ffmpeg (Join-Path $stage 'ffmpeg.exe')
Copy-Item (Join-Path $src 'bridge\bin\Release\StreplayFfmpeg.dll') $stage

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$zip = Join-Path $dist "1nf-STREPLAY_FFMPEG-$ver.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
"Thunderstore package: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
