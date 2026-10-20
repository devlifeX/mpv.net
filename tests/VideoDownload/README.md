# Buffered video downloads

Run with .NET SDK 10 and Python 3 on Windows:

```powershell
python tests/VideoDownload/run-integration.py
```

`DOTNET` can select a local SDK. These tests use real localhost HTTP connections
and verify eight simultaneous requests, playback-block reuse, SHA-256 equality,
pause of every reader, cancellation, resume into a new cache directory, retries,
changed ETags, invalid Content-Range, and unsupported/encoded range responses.
It also checks that one speed limit applies to the combined eight connections.

The native Windows dialog checks, including a translation containing invalid
filter separators, consistent text colors and three window sizes, run with:

```powershell
dotnet run --project tests/VideoDownloadUI/VideoDownloadUI.csproj -p:PublishSingleFile=false -- artifacts/ui-checks
```

For an additional real-player check, serve a direct video larger than 8 MiB
from an HTTP server with Range and ETag support, configure libmpv/FFmpeg as below,
and run `dotnet run --project tests/VideoDownload/VideoDownload.csproj -- URL
artifacts/media-check media`. This requires both cached and missing chunks and
checks verified export, original source URL capture and the foreground pause state.

Run the existing BufferedVideo, StreamCompletion and PlaybackRecovery integration
suites too. They require real libmpv and FFmpeg; point `MPVNET_TEST_LIBMPV` to the
DLL and put FFmpeg/FFprobe (including their DLL dependencies) on PATH.

For the GUI check, open a direct MP4/MKV/WebM HTTP URL with a stable ETag or
Last-Modified validator. Select Save Buffered Video, choose a destination, then
Start. The dialog defaults to eight connections (1–32), shows cached/resumed
chunks in blue and newly downloaded chunks in green, and supports pause/resume,
a KiB/s speed limit, cancel while retaining complete chunks, retry and opening
the destination folder. To resume after restarting the application, open the
same video and save to the same destination. Keep its `.mpvnet-parts` directory.
The server's URL, length, validator and connection settings must still match.

Only complete, verified byte ranges are counted as reusable. Previously demuxed
packet buffers are used directly when complete and verifiable. Partial packet
caches, HLS/DASH and HTTP transports requiring unsupported cookie/proxy/TLS
settings use libmpv's single-connection completion path; pause/resume there lasts
for the current window only. The dialog states this limitation. Direct media
opened with the new version retains original byte chunks during playback, so
the downloader can fetch only missing chunks. No timestamp-to-byte estimates
are used. A server without a representation validator uses the fallback path.

Saving keeps the captured video/audio selection and delay, checks packet counts,
duration and decoding, and replaces the destination only after verification.
FFmpeg is required beside the application or on PATH.

Packaging: `src/Tools/build-portable.ps1 -FrameworkDependent` creates a smaller
package requiring the **.NET 10 Desktop Runtime x64**. The default includes the
runtime. Both include standalone FFmpeg for export verification. Inno Setup uses
solid LZMA2/ultra64 compression; pass `/DMyAppSuffix=-framework-dependent` when
compiling the smaller installer to distinguish the two packages.
