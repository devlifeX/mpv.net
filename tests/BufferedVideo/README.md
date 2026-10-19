# Buffered video regression checks

Requires .NET SDK 10. The console checks use the production cache validation code
and verify the default context-menu binding without a test framework:

```sh
dotnet run --project tests/BufferedVideo/BufferedVideo.csproj
```

For the integration test, also install `ffmpeg`, `ffprobe`, Python 3, and libmpv.
On Windows, point `MPVNET_TEST_LIBMPV` at `libmpv-2.dll`. On Linux, point it at
`libmpv.so.2` (and set `LD_LIBRARY_PATH` if its dependencies are not on the system
library path). `DOTNET` can specify a non-default dotnet executable.

```sh
MPVNET_TEST_LIBMPV=/absolute/path/libmpv.so.2 python3 tests/BufferedVideo/run-integration.py
```

The runner generates four-second H.264/AAC MP4 and MPEG-TS fixtures, serves each
over localhost, and exercises `MainPlayer` and `BufferedVideo.SaveAsync` with real
libmpv. It shuts down HTTP and waits for an explicit handshake before exporting.
FFmpeg then verifies every decoded video frame and the AAC payloads (allowing the
initial encoder priming packet to be omitted by mpv). It also checks duration and
codecs, localized filenames/timestamps, changed playback, stopped playback, muxer
errors, destination preservation, temporary-file cleanup, and network keep-open
restoration. All fixtures and exports are temporary.

The Windows GUI still needs a manual check: stream a video to EOF, right-click,
choose **File > Save Buffered Video...**, cancel once, then save and play the MKV.
Also check the overwrite prompt and a separate selected audio track. See
`tests/StreamCompletion` for automatic completion and audio-switch regression tests.
