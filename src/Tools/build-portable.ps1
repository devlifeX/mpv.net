<# Builds the Windows x64 portable package, including .NET and native tools. #>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path "$PSScriptRoot/../..").Path
$output = Join-Path $root 'artifacts/mpv.net-win-x64'
$downloads = Join-Path $root 'artifacts/dependencies'
$sevenZip = 'C:/Program Files/7-Zip/7z.exe'
New-Item $output, $downloads -ItemType Directory -Force | Out-Null

function Download($url, $name, $sha256) {
    $file = Join-Path $downloads $name
    Invoke-WebRequest $url -OutFile $file -MaximumRetryCount 3 -RetryIntervalSec 3
    if ((Get-FileHash $file -Algorithm SHA256).Hash -ne $sha256) {
        throw "Checksum mismatch: $name"
    }
    return $file
}

function Extract($archive, $directory) {
    & $sevenZip x -y $archive "-o$directory" | Out-Null
    if ($LASTEXITCODE) { throw "Failed to extract $archive" }
}

dotnet publish "$root/src/MpvNet.Windows/MpvNet.Windows.csproj" `
    --configuration Release --runtime win-x64 --self-contained true --output $output
if ($LASTEXITCODE) { throw 'Windows publish failed.' }

# Pin and verify the native inputs so rebuilding a commit uses the same tools.
$mpvRelease = 'https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/20261019'
$mpv = Download "$mpvRelease/mpv-dev-x86_64-20261019-git-6a262d11f6.7z" 'libmpv.7z' `
    'ca8604754170db6f1b23139d8449782b1ae294a883211131954d30085985f77b'
$ffmpeg = Download "$mpvRelease/ffmpeg-x86_64-git-c2bbf2b83.7z" 'ffmpeg.7z' `
    '8888676c10ffe70406ead843f84ca04ce3243ba6faf500c77c15748d9dcc4952'
$mediaInfo = Download 'https://mediaarea.net/download/binary/libmediainfo0/26.10/MediaInfo_DLL_26.10_Windows_x64_WithoutInstaller.zip' 'mediainfo.zip' `
    '4efe172df014693732e8b64a004cf382b8599eaf86be26a9b38c0d2993e60e1d'
$launcher = Download 'https://github.com/mpvnet-player/file-host/releases/download/tag/mpvnet.com.txt' 'mpvnet.com' `
    'd4b0a80779dc775fb8817afa128a4ddcfe3bd07bca98a9d0c49ba44daf5cb5e3'

Extract $mpv "$downloads/mpv"
Extract $ffmpeg "$downloads/ffmpeg"
Extract $mediaInfo "$downloads/mediainfo"
Copy-Item "$downloads/mpv/libmpv-2.dll", "$downloads/ffmpeg/ffmpeg.exe", `
    "$downloads/mediainfo/MediaInfo.dll", $launcher $output
Copy-Item "$root/License.txt" $output
New-Item "$output/licenses" -ItemType Directory -Force | Out-Null
Copy-Item "$downloads/mediainfo/Developers/License.html" "$output/licenses/MediaInfo.html"
Get-ChildItem "$downloads/mpv", "$downloads/ffmpeg" -Recurse -File |
    Where-Object { $_.Name -match '^(LICENSE|COPYING)' } |
    ForEach-Object { Copy-Item $_.FullName (Join-Path "$output/licenses" ($_.Directory.Name + '-' + $_.Name)) }

foreach ($name in @('mpvnet.exe', 'mpvnet.com', 'libmpv-2.dll', 'MediaInfo.dll', 'ffmpeg.exe')) {
    $file = Join-Path $output $name
    if (-not (Test-Path $file) -or (Get-Item $file).Length -eq 0) {
        throw "Required package file is missing: $name"
    }
}

$commit = git -C $root rev-parse HEAD
@"
mpv.net Windows x64 portable build
Commit: $commit
Runtime: .NET 10, self-contained (no separate .NET installation required)
libmpv and FFmpeg: $mpvRelease
Native build sources: https://github.com/shinchiro/mpv-winbuild-cmake
MediaInfo 26.10: https://mediaarea.net/en/MediaInfo
Start: extract the entire archive, then run mpvnet.exe on Windows 10/11 x64.
Website streams that use yt-dlp require yt-dlp.exe beside mpvnet.exe or on PATH.
"@ | Set-Content "$output/build-info.txt" -Encoding utf8
