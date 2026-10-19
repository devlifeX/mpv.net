# Complete selected-stream export checks

Requires .NET SDK 10, Python 3, FFmpeg, FFprobe (test assertions only), and real
libmpv. `MPVNET_TEST_LIBMPV` selects the absolute library path; `DOTNET` selects a
custom SDK. Set `LD_LIBRARY_PATH` on Linux if native dependencies need it.

```sh
MPVNET_TEST_LIBMPV=/absolute/path/libmpv.so.2 python3 tests/StreamCompletion/run-integration.py
```

The runner generates H.264 video and two distinct AAC tones. Its HTTP server
requires an authentication header for every request and supports byte ranges.
It exercises the production export with:

- Separately selected external video and audio, including remapping foreground track IDs.
- A fully cached video and separately selected audio 2, verifying that video is
  not downloaded again and every selected audio packet is preserved.
- Separate and internal audio tracks shorter than the video, preserving their actual duration.
- Internal audio switching midway through playback, with the first part missing
  from the newly selected track's buffer.
- A multi-stream EDL like yt-dlp's separate video/audio representation.
- An HLS master playlist with two alternate audio renditions and separate segments.
- Existing native watch-later bookmarks, which must not truncate background downloads.
- Dropped/evicted cache ranges that must be filled automatically.
- Positive and negative selected audio delays, checking A/V timestamps.
- A foreground track or source switch after selection capture, without changing the export.
- Corrupt cached H.264 slices, verifying the fixture really reports decoding errors
  (even when FFmpeg exits zero), then repairing the video from a healthy source.
- Actual HTTP failure followed by automatic retry and recovery.
- Cancellation during an active download, parent-player shutdown, persistent HTTP failure and truncated input, preserving an existing
  destination and cleaning temporary export files.

All decoded video frames and every AAC packet are compared with the original
selected source; the first AAC encoder priming packet may be omitted by mpv.
Foreground position, pause state and track selection must remain unchanged.
Fixtures, exports and native player processes are temporary. `COMPLETION_TEST_MODES`
can select a comma-separated subset of the scenario names in the runner.

The Windows Save dialog, packaged FFmpeg executable, and a real service requiring
expiring credentials still need manual testing on Windows. This suite uses a
controlled local HTTP origin, not a live streaming account.
