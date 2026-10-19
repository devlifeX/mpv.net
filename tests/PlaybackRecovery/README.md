# Continue regression checks

Requires .NET SDK 10:

```sh
dotnet run --project tests/PlaybackRecovery/PlaybackRecovery.csproj
```

This checks the first root-menu entry, legacy menu/key preservation, atomic
checkpoint replacement, backup recovery, corrupted state rejection, Unicode and
HTTP settings, and cleanup after a failed write.

The integration runner also requires FFmpeg and real libmpv. Set `DOTNET` to select
a custom SDK executable and `MPVNET_TEST_LIBMPV` to the absolute path of
`libmpv-2.dll` (Windows) or `libmpv.so.2` (Linux). Linux dependencies may additionally
need `LD_LIBRARY_PATH`.

```sh
MPVNET_TEST_LIBMPV=/absolute/path/libmpv.so.2 python3 tests/PlaybackRecovery/run-integration.py
```

The runner generates an H.264/AAC network video and an external subtitle file.
It kills the original player process immediately after a durable checkpoint,
without running quit/shutdown handlers. A fresh process must restore the fractional
position, speed, volume/mute, audio/subtitle delays, zoom, and selected external
subtitles with native watch-later resume disabled. The server requires a custom
HTTP header, verifying that connection settings are restored before loading.

It also tests a failed connection followed by a successful retry, and a network
interruption within the same process: after seeking and checkpointing, HTTP is
disabled, read-ahead is limited, and mpv's buffers are dropped to force another range read. The checkpoint
must survive the read/connection error and restore correctly when HTTP returns.
Missing external subtitles must pause playback and keep the checkpoint intact.
All fixtures and configuration directories are temporary. This simulates abrupt
process death, not a physical power cut or a Windows GUI session.

Manual Windows check: verify **Continue** is first in the root context menu, pause
and Continue, interrupt a stream, restore connectivity and Continue, then end the
player through Task Manager and launch it without a file before clicking Continue.
